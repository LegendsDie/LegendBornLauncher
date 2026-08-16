using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LegendBorn.Services;

public enum LogLevel
{
    Trace = 0,
    Debug = 1,
    Info = 2,
    Warn = 3,
    Error = 4,
    Fatal = 5
}

public sealed class LogService : IDisposable
{
    private readonly string _logFilePath;
    private readonly long _maxBytes;

    private readonly ConcurrentQueue<string> _queue = new();
    private readonly AutoResetEvent _signal = new(false);
    private readonly SemaphoreSlim _flushGate = new(1, 1);

    private CancellationTokenSource? _cts;
    private Task? _writerTask;

    private volatile bool _disposed;
    private volatile bool _enabled;

    private int _queueCount;
    private int _droppedLines;

    public LogLevel MinimumLevel { get; set; } = LogLevel.Info;

    public static LogService Noop { get; } = new LogService(isNoop: true);

    private const int MaxQueueLines = 8000;
    private const int FlushChunkMaxChars = 128_000;

    public LogService(string logFilePath, long maxBytes = 2_000_000)
    {
        _logFilePath = logFilePath ?? throw new ArgumentNullException(nameof(logFilePath));
        _maxBytes = maxBytes < 64 * 1024 ? 64 * 1024 : maxBytes;
        _enabled = true;

        try
        {
            var dir = Path.GetDirectoryName(_logFilePath);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);
        }
        catch
        {
            _enabled = false;
        }

        if (_enabled)
            StartWriter();
    }

    private LogService(bool isNoop)
    {
        _logFilePath = "";
        _maxBytes = 0;
        _enabled = false;
        MinimumLevel = LogLevel.Fatal;
    }

    private void StartWriter()
    {
        try
        {
            _cts = new CancellationTokenSource();
            _writerTask = Task.Run(() => WriterLoopAsync(_cts.Token));
        }
        catch
        {
            _enabled = false;
            try { _cts?.Dispose(); } catch { }
            _cts = null;
            _writerTask = null;
        }
    }

    public void Trace(string message) => Write(LogLevel.Trace, message, null);
    public void Debug(string message) => Write(LogLevel.Debug, message, null);
    public void Info(string message) => Write(LogLevel.Info, message, null);
    public void Warn(string message) => Write(LogLevel.Warn, message, null);
    public void Error(string message, Exception? ex = null) => Write(LogLevel.Error, message, ex);
    public void Fatal(string message, Exception? ex = null) => Write(LogLevel.Fatal, message, ex);

    public void Write(LogLevel level, string message, Exception? ex = null)
    {
        if (_disposed || !_enabled || level < MinimumLevel) return;

        var line = FormatLine(level, message, ex);
        _queue.Enqueue(line);
        var cnt = Interlocked.Increment(ref _queueCount);

        if (cnt > MaxQueueLines)
        {
            var drop = 0;
            while (Interlocked.CompareExchange(ref _queueCount, 0, 0) > MaxQueueLines && _queue.TryDequeue(out _))
            {
                drop++;
                Interlocked.Decrement(ref _queueCount);
                if (drop > 256) break;
            }

            if (drop > 0)
                Interlocked.Add(ref _droppedLines, drop);
        }

        try { _signal.Set(); } catch { }
    }

    public void Flush()
    {
        if (_disposed || !_enabled) return;
        try { _signal.Set(); } catch { }
        TryFlushOnce();
    }

    public bool TryFlush()
    {
        if (_disposed || !_enabled) return false;
        return TryFlushOnce();
    }

    private static string FormatLine(LogLevel level, string message, Exception? ex)
    {
        var ts = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
        var lvl = level.ToString().ToUpperInvariant();
        var msg = (message ?? "").Replace("\r", " ").Replace("\n", " ").Trim();

        if (ex is null)
            return $"[{ts}] [{lvl}] {msg}";

        var sb = new StringBuilder(1024);
        sb.Append($"[{ts}] [{lvl}] ").Append(msg);
        sb.Append(" | ex=").Append(ex.GetType().Name).Append(": ").Append(ex.Message);

        if (!string.IsNullOrWhiteSpace(ex.StackTrace))
        {
            sb.AppendLine();
            sb.AppendLine(ex.StackTrace);
        }

        var inner = ex.InnerException;
        var depth = 0;
        while (inner is not null && depth < 5)
        {
            depth++;
            sb.AppendLine();
            sb.Append("Inner").Append(depth).Append(": ")
                .Append(inner.GetType().Name).Append(": ").Append(inner.Message);

            if (!string.IsNullOrWhiteSpace(inner.StackTrace))
            {
                sb.AppendLine();
                sb.AppendLine(inner.StackTrace);
            }

            inner = inner.InnerException;
        }

        return sb.ToString().TrimEnd();
    }

    private async Task WriterLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                _signal.WaitOne(250);
                if (ct.IsCancellationRequested) break;
                await FlushOnceAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch
        {
        }
        finally
        {
            if (!_disposed)
            {
                try { await FlushOnceAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
            }
        }
    }

    private bool TryFlushOnce()
    {
        try
        {
            FlushOnceAsync(CancellationToken.None).GetAwaiter().GetResult();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task FlushOnceAsync(CancellationToken ct)
    {
        if (_disposed || !_enabled) return;

        await _flushGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_disposed || !_enabled) return;

            if (_queue.IsEmpty)
            {
                var dropped0 = Interlocked.Exchange(ref _droppedLines, 0);
                if (dropped0 > 0)
                {
                    _queue.Enqueue(FormatLine(LogLevel.Warn, $"Log queue overflow: dropped {dropped0} lines.", null));
                    Interlocked.Increment(ref _queueCount);
                }
                else
                {
                    return;
                }
            }

            var drained = new List<string>();
            var dropped = 0;

            try
            {
                EnsureLogDir();
                RotateIfNeeded();
                CleanupOldLogsSafe(maxKeep: 10);

                var sb = new StringBuilder(FlushChunkMaxChars);
                dropped = Interlocked.Exchange(ref _droppedLines, 0);
                if (dropped > 0)
                    sb.AppendLine(FormatLine(LogLevel.Warn, $"Log queue overflow: dropped {dropped} lines.", null));

                while (_queue.TryDequeue(out var line))
                {
                    Interlocked.Decrement(ref _queueCount);
                    drained.Add(line);
                    sb.AppendLine(line);
                    if (sb.Length >= FlushChunkMaxChars)
                        break;
                }

                var text = sb.ToString();
                if (string.IsNullOrEmpty(text)) return;

                await File.AppendAllTextAsync(_logFilePath, text, Encoding.UTF8, ct).ConfigureAwait(false);
            }
            catch
            {
                // A transient disk/locking error must not silently consume log entries. Requeue the
                // drained lines for the next flush. Their exact order relative to newly queued lines
                // is less important than preserving diagnostics at all.
                if (dropped > 0)
                    Interlocked.Add(ref _droppedLines, dropped);

                foreach (var line in drained)
                {
                    _queue.Enqueue(line);
                    Interlocked.Increment(ref _queueCount);
                }
            }
        }
        finally
        {
            _flushGate.Release();
        }
    }

    private void EnsureLogDir()
    {
        try
        {
            var dir = Path.GetDirectoryName(_logFilePath);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);
        }
        catch { }
    }

    private void RotateIfNeeded()
    {
        try
        {
            var fi = new FileInfo(_logFilePath);
            if (!fi.Exists || fi.Length < _maxBytes) return;

            var dir = fi.DirectoryName ?? Path.GetDirectoryName(_logFilePath);
            if (string.IsNullOrWhiteSpace(dir)) return;

            var bak = Path.Combine(dir, $"launcher_{DateTime.Now:yyyyMMdd_HHmmss}.log");
            File.Move(_logFilePath, bak, overwrite: true);
        }
        catch
        {
        }
    }

    private void CleanupOldLogsSafe(int maxKeep)
    {
        try
        {
            maxKeep = Math.Clamp(maxKeep, 3, 50);
            var dir = Path.GetDirectoryName(_logFilePath);
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return;

            var files = Directory.EnumerateFiles(dir, "launcher_*.log", SearchOption.TopDirectoryOnly)
                .Select(p => new FileInfo(p))
                .Where(f => f.Exists)
                .OrderByDescending(f => f.CreationTimeUtc)
                .ToList();

            if (files.Count <= maxKeep) return;

            foreach (var f in files.Skip(maxKeep))
            {
                try { f.Delete(); } catch { }
            }
        }
        catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;

        // Flush while the service is still alive. Previously _disposed was set first, which made
        // the final Flush() return immediately and could drop the last shutdown diagnostics.
        if (_enabled)
        {
            try { Flush(); } catch { }
        }

        _disposed = true;

        try { _cts?.Cancel(); } catch { }
        try { _signal.Set(); } catch { }

        try
        {
            if (_writerTask is not null)
                _writerTask.Wait(800);
        }
        catch { }

        try { _signal.Dispose(); } catch { }
        try { _cts?.Dispose(); } catch { }

        // Do not dispose _flushGate during process shutdown. If a very slow filesystem write keeps
        // the writer alive beyond the short join timeout, disposing the gate underneath its finally
        // block can create a late ObjectDisposedException. The process is exiting, so retaining this
        // tiny synchronization object is safer than racing its disposal.

        _cts = null;
        _writerTask = null;
        _enabled = false;
    }
}
