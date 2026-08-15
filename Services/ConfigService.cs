// File: Services/ConfigService.cs
using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using LegendBorn.Models;

namespace LegendBorn.Services;

public sealed class ConfigService : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    private const long MaxConfigBytes = 1024L * 1024;

    private readonly object _sync = new();

    private long _lastSaveTick;
    private const int MinSaveIntervalMs = 250;
    private const int MaxDeferredSaveMs = 2500;
    private const int RetrySaveDelayMs = 1200;

    private bool _dirty;
    private long _firstDirtyTick;
    private Timer? _saveTimer;
    private string? _lastSavedJson;

    public string ConfigPath { get; }
    public LauncherConfig Current { get; private set; } = new();

    public ConfigService(string configPath)
    {
        if (string.IsNullOrWhiteSpace(configPath))
            throw new ArgumentException("configPath is null/empty", nameof(configPath));

        ConfigPath = configPath;
    }

    public LauncherConfig LoadOrCreate()
    {
        lock (_sync)
        {
            try
            {
                EnsureParentDir(ConfigPath);
                RecoverOrCleanupTmp();

                if (!File.Exists(ConfigPath))
                {
                    Current = new LauncherConfig();
                    TryNormalize(Current);
                    if (!SaveNowInternal(force: true))
                        ScheduleRetry_NoLock();
                    return Current;
                }

                var info = new FileInfo(ConfigPath);
                if (info.Length <= 0 || info.Length > MaxConfigBytes)
                    return RecoverBrokenConfig_NoLock("invalid config size");

                var jsonOnDisk = File.ReadAllText(ConfigPath, Utf8NoBom);
                var cfg = TryDeserialize(jsonOnDisk);
                if (cfg is null)
                    return RecoverBrokenConfig_NoLock("invalid config JSON");

                TryNormalize(cfg);
                Current = cfg;

                var canonical = Serialize(Current);
                _lastSavedJson = canonical;
                _dirty = false;
                _firstDirtyTick = 0;
                CancelTimer_NoLock();

                // Persist schema migrations/normalization immediately. Previously a normalized
                // in-memory config could diverge from the broken/stale file indefinitely.
                if (!string.Equals(jsonOnDisk, canonical, StringComparison.Ordinal))
                {
                    MarkDirty_NoLock();
                    if (!SaveNowInternal(force: true))
                        ScheduleRetry_NoLock();
                }

                return Current;
            }
            catch
            {
                return RecoverBrokenConfig_NoLock("config load failed");
            }
        }
    }

    private LauncherConfig RecoverBrokenConfig_NoLock(string reason)
    {
        _ = reason; // Kept for debugger clarity; ConfigService itself has no logger dependency.
        TryBackupBrokenConfig();

        Current = new LauncherConfig();
        TryNormalize(Current);
        _lastSavedJson = null;
        _dirty = true;
        _firstDirtyTick = Environment.TickCount64;
        CancelTimer_NoLock();

        if (!SaveNowInternal(force: true))
            ScheduleRetry_NoLock();

        return Current;
    }

    /// <summary>
    /// Mark the config dirty and schedule persistence. A failed immediate write is retried instead
    /// of silently clearing the save request.
    /// </summary>
    public void Save()
    {
        lock (_sync)
        {
            MarkDirty_NoLock();
            ScheduleSave_NoLock();
        }
    }

    /// <summary>Force persistence now (used during process shutdown).</summary>
    public void Flush()
    {
        lock (_sync)
        {
            MarkDirty_NoLock();
            CancelTimer_NoLock();
            if (!SaveNowInternal(force: true))
                throw new IOException($"Failed to persist launcher config: {ConfigPath}");
        }
    }

    private void MarkDirty_NoLock()
    {
        _dirty = true;
        var now = Environment.TickCount64;
        if (_firstDirtyTick == 0)
            _firstDirtyTick = now;
    }

    private void ScheduleSave_NoLock()
    {
        if (!_dirty)
            return;

        var now = Environment.TickCount64;
        var sinceLast = now - _lastSaveTick;
        if (sinceLast >= MinSaveIntervalMs)
        {
            if (!SaveNowInternal(force: false))
                ScheduleRetry_NoLock();
            return;
        }

        var dueMin = MinSaveIntervalMs - (int)sinceLast;
        if (dueMin < 1) dueMin = 1;

        var sinceDirty = _firstDirtyTick == 0 ? 0 : now - _firstDirtyTick;
        var dueMax = MaxDeferredSaveMs - (int)sinceDirty;
        if (dueMax <= 0)
        {
            if (!SaveNowInternal(force: true))
                ScheduleRetry_NoLock();
            return;
        }

        EnsureTimer_NoLock();
        _saveTimer!.Change(Math.Min(dueMin, dueMax), Timeout.Infinite);
    }

    private void ScheduleRetry_NoLock()
    {
        if (!_dirty)
            return;
        EnsureTimer_NoLock();
        _saveTimer!.Change(RetrySaveDelayMs, Timeout.Infinite);
    }

    private void EnsureTimer_NoLock()
    {
        _saveTimer ??= new Timer(_ =>
        {
            lock (_sync)
            {
                if (!_dirty)
                    return;

                var now = Environment.TickCount64;
                var force = _firstDirtyTick != 0 && now - _firstDirtyTick >= MaxDeferredSaveMs;
                var ok = SaveNowInternal(force);

                if (!ok && _dirty)
                    ScheduleRetry_NoLock();
                else if (!_dirty)
                    CancelTimer_NoLock();
            }
        }, null, Timeout.Infinite, Timeout.Infinite);
    }

    private void CancelTimer_NoLock()
    {
        try { _saveTimer?.Change(Timeout.Infinite, Timeout.Infinite); } catch { }
    }

    private bool SaveNowInternal(bool force)
    {
        Current ??= new LauncherConfig();
        if (!force && !_dirty)
            return true;

        _lastSaveTick = Environment.TickCount64;
        var tmp = ConfigPath + ".tmp";

        try
        {
            TryNormalize(Current);
            EnsureParentDir(ConfigPath);

            var json = Serialize(Current);
            if (Utf8NoBom.GetByteCount(json) > MaxConfigBytes)
                throw new InvalidOperationException("Launcher config exceeds the safety bound.");

            if (!force && File.Exists(ConfigPath) && _lastSavedJson is not null &&
                string.Equals(_lastSavedJson, json, StringComparison.Ordinal))
            {
                _dirty = false;
                _firstDirtyTick = 0;
                return true;
            }

            TryDeleteQuiet(tmp);
            using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var sw = new StreamWriter(fs, Utf8NoBom))
            {
                sw.Write(json);
                sw.Flush();
                fs.Flush(flushToDisk: true);
            }

            ReplaceOrMoveAtomic(tmp, ConfigPath);
            TryDeleteQuiet(tmp);

            _lastSavedJson = json;
            _dirty = false;
            _firstDirtyTick = 0;
            return true;
        }
        catch
        {
            TryDeleteQuiet(tmp);
            _dirty = true;
            if (_firstDirtyTick == 0)
                _firstDirtyTick = Environment.TickCount64;
            return false;
        }
    }

    private static string Serialize(LauncherConfig cfg)
        => JsonSerializer.Serialize(cfg, JsonOptions);

    private static LauncherConfig? TryDeserialize(string json)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(json))
                return null;
            return JsonSerializer.Deserialize<LauncherConfig>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private void RecoverOrCleanupTmp()
    {
        try
        {
            var tmp = ConfigPath + ".tmp";
            if (!File.Exists(tmp))
                return;

            if (!File.Exists(ConfigPath))
            {
                try
                {
                    File.Move(tmp, ConfigPath, overwrite: true);
                    return;
                }
                catch
                {
                }
            }

            TryDeleteQuiet(tmp);
        }
        catch
        {
        }
    }

    private void TryBackupBrokenConfig()
    {
        try
        {
            if (!File.Exists(ConfigPath)) return;
            EnsureParentDir(ConfigPath);

            var ts = DateTime.UtcNow.ToString("yyyyMMddHHmmssfff");
            var bak = ConfigPath + ".broken." + ts + ".bak";
            File.Copy(ConfigPath, bak, overwrite: false);
        }
        catch
        {
        }
    }

    private static void TryNormalize(LauncherConfig cfg)
    {
        try { cfg.Normalize(); } catch { }
    }

    private static void EnsureParentDir(string filePath)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrWhiteSpace(dir))
            Directory.CreateDirectory(dir);
    }

    private static void ReplaceOrMoveAtomic(string sourceTmp, string destPath)
    {
        if (OperatingSystem.IsWindows() && File.Exists(destPath))
        {
            var backup = destPath + ".bak";
            TryDeleteQuiet(backup);
            try
            {
                File.Replace(sourceTmp, destPath, backup, ignoreMetadataErrors: true);
                return;
            }
            catch (IOException)
            {
                // Fall through to Move(overwrite). If that also fails, propagate to the retry loop.
            }
            catch (UnauthorizedAccessException)
            {
            }
            finally
            {
                TryDeleteQuiet(backup);
            }
        }

        File.Move(sourceTmp, destPath, overwrite: true);
    }

    private static void TryDeleteQuiet(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            try { _saveTimer?.Dispose(); } catch { }
            _saveTimer = null;
        }
    }
}
