using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LegendBorn.Services;

public enum LogLevel
{
    Trace = 0,
    Debug = 1,
    Info  = 2,
    Warn  = 3,
    Error = 4,
    Fatal = 5
}

/// <summary>
/// Централизованный логгер лаунчера:
/// - Пишет в файл (один writer task)
/// - Потокобезопасен
/// - Ротация по размеру
/// - Есть Noop (реально ничего не делает, без IO и без фоновых задач)
/// </summary>
public sealed class LogService : IDisposable
{
    private readonly string _logFilePath;
    private readonly long _maxBytes;

    private readonly ConcurrentQueue<string> _queue = new();
    private readonly AutoResetEvent _signal = new(false);

    private CancellationTokenSource? _cts;
    private Task? _writerTask;

    private volatile bool _disposed;
    private volatile bool _enabled;

    /// <summary>Минимальный уровень логирования.</summary>
    public LogLevel MinimumLevel { get; set; } = LogLevel.Info;

    /// <summary>Заглушка: не пишет ничего, безопасно принимает вызовы.</summary>
    public static LogService Noop { get; } = new LogService(isNoop: true);

    /// <param name="logFilePath">Полный путь к launcher.log</param>
    /// <param name="maxBytes">Максимальный размер файла до ротации</param>
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

    // приватный Noop ctor: не создаёт задач/CTS/IO
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

    // ===== Public API =====
    public void Trace(string message) => Write(LogLevel.Trace, message, null);
    public void Debug(string message) => Write(LogLevel.Debug, message, null);
    public void Info (string message) => Write(LogLevel.Info,  message, null);
    public void Warn (string message) => Write(LogLevel.Warn,  message, null);

    public void Error(string message, Exception? ex = null) => Write(LogLevel.Error, message, ex);
    public void Fatal(string message, Exception? ex = null) => Write(LogLevel.Fatal, message, ex);

    public void Write(LogLevel level, string message, Exception? ex = null)
    {
        if (_disposed) return;
        if (!_enabled) return;
        if (level < MinimumLevel) return;

        var line = FormatLine(level, message, ex);
        _queue.Enqueue(line);
        _signal.Set();
    }

    /// <summary>Принудительно сбросить очередь в файл (best-effort).</summary>
    public void Flush()
    {
        if (_disposed) return;
        if (!_enabled) return;

        try { _signal.Set(); } catch { }
        TryFlushOnce();
    }

    public bool TryFlush()
    {
        if (_disposed) return false;
        if (!_enabled) return false;
        return TryFlushOnce();
    }

    // ===== Internals =====
    private static string FormatLine(LogLevel level, string message, Exception? ex)
    {
        var ts = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
        var lvl = level.ToString().ToUpperInvariant();

        var msg = (message ?? "").Replace("\r", " ").Replace("\n", " ").Trim();

        if (ex is null)
            return $"[{ts}] [{lvl}] {msg}";

        var sb = new StringBuilder();
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

                await FlushOnceAsync(ct).ConfigureAwait(false);
            }
        }
        catch
        {
            // never crash app because of logger
        }
        finally
        {
            try { await FlushOnceAsync(ct).ConfigureAwait(false); } catch { }
        }
    }

    private bool TryFlushOnce()
    {
        try
        {
            if (_cts is null) return false;
            FlushOnceAsync(_cts.Token).GetAwaiter().GetResult();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task FlushOnceAsync(CancellationToken ct)
    {
        if (_disposed) return;
        if (!_enabled) return;
        if (_queue.IsEmpty) return;

        try
        {
            RotateIfNeeded();

            var sb = new StringBuilder();

            while (_queue.TryDequeue(out var line))
            {
                sb.AppendLine(line);
                if (sb.Length > 64_000) break;
            }

            var text = sb.ToString();
            if (string.IsNullOrEmpty(text)) return;

            await File.AppendAllTextAsync(_logFilePath, text, Encoding.UTF8, ct)
                      .ConfigureAwait(false);
        }
        catch
        {
            // ignore
        }
    }

    private void RotateIfNeeded()
    {
        try
        {
            var fi = new FileInfo(_logFilePath);
            if (!fi.Exists) return;
            if (fi.Length < _maxBytes) return;

            var dir = fi.DirectoryName ?? Path.GetDirectoryName(_logFilePath);
            if (string.IsNullOrWhiteSpace(dir)) return;

            var bak = Path.Combine(dir, $"launcher_{DateTime.Now:yyyyMMdd_HHmmss}.log");
            File.Move(_logFilePath, bak, overwrite: true);
        }
        catch
        {
            // ignore
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            if (_enabled)
            {
                try { Flush(); } catch { }
            }
        }
        catch { }

        try { _cts?.Cancel(); } catch { }
        try { _signal.Set(); } catch { }

        try
        {
            if (_writerTask is not null)
                _writerTask.Wait(600);
        }
        catch { }

        try { _signal.Dispose(); } catch { }
        try { _cts?.Dispose(); } catch { }

        _cts = null;
        _writerTask = null;
        _enabled = false;
    }
}
