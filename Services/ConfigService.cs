// ConfigService.cs
using System;
using System.IO;
using System.Text;
using System.Text.Json;
using LegendBorn.Models;

namespace LegendBorn.Services;

public sealed class ConfigService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private readonly object _sync = new();

    // анти-дребезг записи на диск (Save может вызываться часто)
    private long _lastSaveTick;
    private const int MinSaveIntervalMs = 250;

    // если Save() дёргают чаще, чем MinSaveIntervalMs — мы гарантируем сохранение
    // не позднее чем через MaxDeferredSaveMs
    private const int MaxDeferredSaveMs = 2500;

    private bool _dirty;
    private long _firstDirtyTick;

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

                if (!File.Exists(ConfigPath))
                {
                    Current = new LauncherConfig();
                    TryNormalize(Current);
                    SaveInternal(force: true);
                    return Current;
                }

                var json = File.ReadAllText(ConfigPath, Utf8NoBom);

                var cfg = TryDeserialize(json) ?? new LauncherConfig();
                TryNormalize(cfg);

                Current = cfg;

                // сбрасываем флаги «грязности» после успешной загрузки
                _dirty = false;
                _firstDirtyTick = 0;

                return Current;
            }
            catch
            {
                TryBackupBrokenConfig();

                Current = new LauncherConfig();
                TryNormalize(Current);

                try { SaveInternal(force: true); } catch { }

                _dirty = false;
                _firstDirtyTick = 0;

                return Current;
            }
        }
    }

    /// <summary>
    /// Пометить конфиг изменённым и попытаться сохранить.
    /// Если вызовы частые — сработает анти-дребезг, но сохранение всё равно
    /// произойдёт максимум через MaxDeferredSaveMs.
    /// </summary>
    public void Save()
    {
        lock (_sync)
        {
            _dirty = true;

            var now = Environment.TickCount64;
            if (_firstDirtyTick == 0)
                _firstDirtyTick = now;

            SaveInternal(force: false);
        }
    }

    /// <summary>
    /// Гарантированное сохранение сейчас (например, при закрытии лаунчера).
    /// </summary>
    public void Flush()
    {
        lock (_sync)
        {
            _dirty = true;
            SaveInternal(force: true);
        }
    }

    private void SaveInternal(bool force)
    {
        if (Current is null)
            Current = new LauncherConfig();

        if (!force)
        {
            // если нечего сохранять — не трогаем диск
            if (!_dirty)
                return;

            var now = Environment.TickCount64;
            var last = _lastSaveTick;

            // обычный анти-дребезг
            if (now - last < MinSaveIntervalMs)
            {
                // но если конфиг «грязный» слишком долго — сохраняем принудительно
                if (_firstDirtyTick != 0 && (now - _firstDirtyTick) >= MaxDeferredSaveMs)
                {
                    force = true;
                }
                else
                {
                    return;
                }
            }

            _lastSaveTick = now;
        }
        else
        {
            _lastSaveTick = Environment.TickCount64;
        }

        try
        {
            TryNormalize(Current);

            EnsureParentDir(ConfigPath);

            // если на диске уже ровно то же самое — ничего не делаем (снижает лишние записи)
            // (безопасно: если файл битый/пустой — просто перезапишем)
            var json = JsonSerializer.Serialize(Current, JsonOptions);
            if (!force && File.Exists(ConfigPath))
            {
                try
                {
                    var existing = File.ReadAllText(ConfigPath, Utf8NoBom);
                    if (!string.IsNullOrWhiteSpace(existing) && string.Equals(existing, json, StringComparison.Ordinal))
                    {
                        _dirty = false;
                        _firstDirtyTick = 0;
                        return;
                    }
                }
                catch
                {
                    // игнор — перезапишем
                }
            }

            var tmp = ConfigPath + ".tmp";

            // ✅ надёжная запись + flush на диск
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var sw = new StreamWriter(fs, Utf8NoBom))
            {
                sw.Write(json);
                sw.Flush();
                fs.Flush(flushToDisk: true);
            }

            ReplaceOrMoveAtomic(tmp, ConfigPath);

            TryDeleteQuiet(tmp);

            _dirty = false;
            _firstDirtyTick = 0;
        }
        catch
        {
            try
            {
                var tmp = ConfigPath + ".tmp";
                TryDeleteQuiet(tmp);
            }
            catch { }
        }
    }

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

    private void TryBackupBrokenConfig()
    {
        try
        {
            if (!File.Exists(ConfigPath)) return;

            EnsureParentDir(ConfigPath);

            var ts = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
            var bak = ConfigPath + ".broken." + ts + ".bak";

            File.Copy(ConfigPath, bak, overwrite: true);
        }
        catch { }
    }

    private static void TryNormalize(LauncherConfig cfg)
    {
        try { cfg.Normalize(); }
        catch { }
    }

    private static void EnsureParentDir(string filePath)
    {
        try
        {
            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);
        }
        catch { }
    }

    private static void ReplaceOrMoveAtomic(string sourceTmp, string destPath)
    {
        try
        {
            if (OperatingSystem.IsWindows() && File.Exists(destPath))
            {
                var backup = destPath + ".bak";
                try
                {
                    TryDeleteQuiet(backup);
                    File.Replace(sourceTmp, destPath, backup, ignoreMetadataErrors: true);
                }
                finally
                {
                    TryDeleteQuiet(backup);
                }
                return;
            }

            File.Move(sourceTmp, destPath, overwrite: true);
        }
        catch
        {
            // fallback: старый способ
            try
            {
                if (File.Exists(destPath))
                    File.Delete(destPath);

                File.Move(sourceTmp, destPath);
            }
            catch
            {
                // ignore
            }
        }
    }

    private static void TryDeleteQuiet(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
