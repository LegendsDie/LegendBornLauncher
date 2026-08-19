using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LegendBorn.Services;

/// <summary>
/// Owns the Minecraft multiplayer endpoint used by the LegendBorn launcher.
/// The launcher may still consume the backend catalog for build/loader/pack metadata, but the
/// actual game connection and vanilla multiplayer list are pinned to the canonical LegendBorn host.
/// </summary>
public static class MinecraftServerListPolicy
{
    public const string CanonicalServerName = "LegendBorn";
    public const string CanonicalServerAddress = "legendborn.minerent.io";

    private const string ServersFileName = "servers.dat";
    private const int RewriteDebounceMs = 600;
    private const int RetryCount = 6;

    private static readonly byte[] CanonicalPayload = BuildCanonicalServersDat();

    private static readonly ConcurrentDictionary<string, object> WriteLocks =
        new(StringComparer.OrdinalIgnoreCase);

    // Process-lifetime watchers are intentional: while the launcher is alive, Minecraft or a clean
    // install must not be able to re-introduce stale/foreign entries into servers.dat.
    private static readonly ConcurrentDictionary<string, Enforcement> Enforcements =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Returns the production endpoint used for Quick Play. Catalog addresses are informational only
    /// for this public server so a stale catalog/cache cannot send the player to a retired host.
    /// </summary>
    public static string ResolveLaunchAddress(string? catalogAddress, Action<string>? log = null)
    {
        var observed = (catalogAddress ?? string.Empty).Trim();
        if (observed.Length > 0 &&
            !string.Equals(observed, CanonicalServerAddress, StringComparison.OrdinalIgnoreCase))
        {
            log?.Invoke(
                $"Minecraft endpoint: каталог указал '{observed}', использую канонический {CanonicalServerAddress}.");
        }

        return CanonicalServerAddress;
    }

    /// <summary>
    /// Ensures servers.dat contains exactly one server and keeps that invariant for the lifetime of
    /// the launcher. This also heals deletion performed by a clean-install reset after a short debounce.
    /// </summary>
    public static void StartEnforcement(string gameDir, Action<string>? log = null)
    {
        var root = NormalizeGameDir(gameDir);
        Directory.CreateDirectory(root);

        var enforcement = Enforcements.GetOrAdd(root, path => new Enforcement(path, log));
        enforcement.EnsureSoon(immediate: true);
    }

    /// <summary>
    /// Synchronously writes the canonical vanilla multiplayer list when it is absent or different.
    /// Public primarily so runtime smoke tests can validate the exact on-disk contract.
    /// </summary>
    public static void EnsureCanonicalServerList(string gameDir, Action<string>? log = null)
    {
        var root = NormalizeGameDir(gameDir);
        Directory.CreateDirectory(root);

        var gate = WriteLocks.GetOrAdd(root, static _ => new object());
        lock (gate)
        {
            var target = Path.Combine(root, ServersFileName);
            var changed = true;

            if (File.Exists(target))
            {
                try
                {
                    var current = File.ReadAllBytes(target);
                    changed = !current.AsSpan().SequenceEqual(CanonicalPayload);
                }
                catch (IOException)
                {
                    throw;
                }
                catch (UnauthorizedAccessException)
                {
                    throw;
                }
            }

            if (changed)
            {
                var temp = Path.Combine(
                    root,
                    $"{ServersFileName}.legendborn.{Guid.NewGuid():N}.tmp");

                try
                {
                    File.WriteAllBytes(temp, CanonicalPayload);
                    ClearReadOnly(target);
                    File.Move(temp, target, overwrite: true);
                }
                finally
                {
                    TryDelete(temp);
                }

                log?.Invoke(
                    $"Minecraft servers.dat: оставлен только {CanonicalServerName} ({CanonicalServerAddress}).");
            }

            DeleteLegacyServerListArtifacts(root);
        }
    }

    /// <summary>Returns a defensive copy of the exact canonical NBT payload.</summary>
    public static byte[] GetCanonicalServersDatBytes() => CanonicalPayload.ToArray();

    private static string NormalizeGameDir(string gameDir)
    {
        if (string.IsNullOrWhiteSpace(gameDir))
            throw new ArgumentException("Game directory is empty.", nameof(gameDir));

        return Path.GetFullPath(gameDir)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static void DeleteLegacyServerListArtifacts(string root)
    {
        foreach (var fileName in new[]
                 {
                     "servers.dat_old",
                     "servers.dat.bak",
                     "servers.dat.tmp"
                 })
        {
            TryDelete(Path.Combine(root, fileName));
        }

        try
        {
            foreach (var path in Directory.EnumerateFiles(
                         root,
                         "servers.dat.legendborn.*.tmp",
                         SearchOption.TopDirectoryOnly))
            {
                TryDelete(path);
            }
        }
        catch
        {
            // Best effort only; servers.dat itself remains authoritative.
        }
    }

    private static void ClearReadOnly(string path)
    {
        if (!File.Exists(path))
            return;

        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReadOnly) != 0)
            File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (!File.Exists(path))
                return;

            ClearReadOnly(path);
            File.Delete(path);
        }
        catch
        {
            // A short-lived lock is handled by the watcher retry path; stale backups are non-fatal.
        }
    }

    private static byte[] BuildCanonicalServersDat()
    {
        using var stream = new MemoryStream(capacity: 128);
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);

        // Root TAG_Compound with an empty name.
        writer.Write((byte)10);
        WriteNbtString(writer, string.Empty);

        // TAG_List "servers", containing exactly one TAG_Compound.
        writer.Write((byte)9);
        WriteNbtString(writer, "servers");
        writer.Write((byte)10);
        WriteInt32BigEndian(writer, 1);

        // Server entry: only the canonical name and address. Optional icon/resource-pack fields are
        // deliberately omitted so vanilla applies its normal defaults.
        writer.Write((byte)8);
        WriteNbtString(writer, "name");
        WriteNbtString(writer, CanonicalServerName);

        writer.Write((byte)8);
        WriteNbtString(writer, "ip");
        WriteNbtString(writer, CanonicalServerAddress);

        writer.Write((byte)0); // end server compound
        writer.Write((byte)0); // end root compound
        writer.Flush();

        return stream.ToArray();
    }

    private static void WriteNbtString(BinaryWriter writer, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        if (bytes.Length > ushort.MaxValue)
            throw new InvalidOperationException("NBT string is too long.");

        Span<byte> length = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(length, checked((ushort)bytes.Length));
        writer.Write(length);
        writer.Write(bytes);
    }

    private static void WriteInt32BigEndian(BinaryWriter writer, int value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        writer.Write(bytes);
    }

    private sealed class Enforcement : IDisposable
    {
        private readonly string _root;
        private readonly Action<string>? _log;
        private readonly FileSystemWatcher _watcher;
        private readonly Timer _timer;
        private int _workerRunning;
        private int _disposed;

        public Enforcement(string root, Action<string>? log)
        {
            _root = root;
            _log = log;

            _timer = new Timer(OnTimer, null, Timeout.Infinite, Timeout.Infinite);
            _watcher = new FileSystemWatcher(root, ServersFileName)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName |
                               NotifyFilters.LastWrite |
                               NotifyFilters.Size |
                               NotifyFilters.CreationTime,
                EnableRaisingEvents = false
            };

            _watcher.Changed += OnFileChanged;
            _watcher.Created += OnFileChanged;
            _watcher.Deleted += OnFileChanged;
            _watcher.Renamed += OnFileRenamed;
            _watcher.Error += OnWatcherError;
            _watcher.EnableRaisingEvents = true;
        }

        public void EnsureSoon(bool immediate = false)
        {
            if (Volatile.Read(ref _disposed) != 0)
                return;

            try
            {
                _timer.Change(immediate ? 0 : RewriteDebounceMs, Timeout.Infinite);
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private void OnFileChanged(object sender, FileSystemEventArgs e) => EnsureSoon();

        private void OnFileRenamed(object sender, RenamedEventArgs e) => EnsureSoon();

        private void OnWatcherError(object sender, ErrorEventArgs e)
        {
            _log?.Invoke($"Minecraft servers.dat watcher: {e.GetException().Message}");
            EnsureSoon();
        }

        private void OnTimer(object? state)
        {
            if (Volatile.Read(ref _disposed) != 0)
                return;

            if (Interlocked.Exchange(ref _workerRunning, 1) != 0)
            {
                EnsureSoon();
                return;
            }

            _ = Task.Run(async () =>
            {
                Exception? lastError = null;

                try
                {
                    for (var attempt = 1; attempt <= RetryCount; attempt++)
                    {
                        if (Volatile.Read(ref _disposed) != 0)
                            return;

                        try
                        {
                            EnsureCanonicalServerList(_root, _log);
                            return;
                        }
                        catch (IOException ex)
                        {
                            lastError = ex;
                        }
                        catch (UnauthorizedAccessException ex)
                        {
                            lastError = ex;
                        }

                        await Task.Delay(150 * attempt).ConfigureAwait(false);
                    }

                    if (lastError is not null)
                    {
                        _log?.Invoke(
                            $"Minecraft servers.dat: не удалось восстановить канонический список — {lastError.Message}");
                    }
                }
                finally
                {
                    Interlocked.Exchange(ref _workerRunning, 0);
                }
            });
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            try { _watcher.EnableRaisingEvents = false; } catch { }
            try { _watcher.Dispose(); } catch { }
            try { _timer.Dispose(); } catch { }
        }
    }
}
