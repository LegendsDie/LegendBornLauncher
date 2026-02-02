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

    private readonly object _sync = new();

    // анти-дребезг записи на диск
    private long _lastSaveTick;
    private const int MinSaveIntervalMs = 250;

    // гарантируем сохранение не позднее чем через...
    private const int MaxDeferredSaveMs = 2500;

    // retry при неудачной записи (например, файл занят антивирусом)
    private const int RetrySaveDelayMs = 1200;

    private bool _dirty;
    private long _firstDirtyTick;

    // планировщик сохранения (чтобы "Save один раз" тоже сохранялся)
    private Timer? _saveTimer;

    // кеш последней сохранённой канонической строки JSON (чтобы не читать файл каждый раз)
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
                    SaveNowInternal(force: true);
                    return Current;
                }

                var jsonOnDisk = File.ReadAllText(ConfigPath, Utf8NoBom);

                var cfg = TryDeserialize(jsonOnDisk) ?? new LauncherConfig();
                TryNormalize(cfg);

                Current = cfg;

                // Ставим канонический JSON в кеш (а не "как на диске"),
                // чтобы сравнение дальше было стабильным.
                _lastSavedJson = Serialize(Current);

                _dirty = false;
                _firstDirtyTick = 0;

                CancelTimer_NoLock();
                return Current;
            }
            catch
            {
                TryBackupBrokenConfig();

                Current = new LauncherConfig();
                TryNormalize(Current);

                try { SaveNowInternal(force: true); } catch { }

                _dirty = false;
                _firstDirtyTick = 0;

                CancelTimer_NoLock();
                return Current;
            }
        }
    }

    /// <summary>
    /// Пометить конфиг изменённым и запланировать сохранение.
    /// Сохранение произойдёт:
    /// - не раньше MinSaveIntervalMs после последнего успешного save
    /// - но не позже MaxDeferredSaveMs после первой "грязности"
    /// </summary>
    public void Save()
    {
        lock (_sync)
        {
            MarkDirty_NoLock();
            ScheduleSave_NoLock();
        }
    }

    /// <summary>
    /// Гарантированное сохранение сейчас (например, при закрытии лаунчера).
    /// </summary>
    public void Flush()
    {
        lock (_sync)
        {
            MarkDirty_NoLock();
            CancelTimer_NoLock();
            SaveNowInternal(force: true);
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

        // Если можно сохранить прямо сейчас — сохраняем синхронно (как и было),
        // иначе планируем таймером ближайшее допустимое время.
        var sinceLast = now - _lastSaveTick;
        if (sinceLast >= MinSaveIntervalMs)
        {
            SaveNowInternal(force: false);
            return;
        }

        var dueMin = MinSaveIntervalMs - (int)sinceLast;
        if (dueMin < 1) dueMin = 1;

        var sinceDirty = (_firstDirtyTick == 0) ? 0 : (now - _firstDirtyTick);
        var dueMax = MaxDeferredSaveMs - (int)sinceDirty;

        // если уже превысили MaxDeferredSaveMs — сохраняем сразу
        if (dueMax <= 0)
        {
            SaveNowInternal(force: true);
            return;
        }

        var due = Math.Min(dueMin, dueMax);

        EnsureTimer_NoLock();
        _saveTimer!.Change(due, Timeout.Infinite);
    }

    private void EnsureTimer_NoLock()
    {
        _saveTimer ??= new Timer(_ =>
        {
            // таймерный callback НЕ под UI-lock — берём наш лок
            lock (_sync)
            {
                if (!_dirty)
                    return;

                var now = Environment.TickCount64;
                var force = _firstDirtyTick != 0 && (now - _firstDirtyTick) >= MaxDeferredSaveMs;

                var ok = SaveNowInternal(force);

                // если не удалось записать — попробуем позже
                if (!ok && _dirty)
                {
                    EnsureTimer_NoLock();
                    _saveTimer!.Change(RetrySaveDelayMs, Timeout.Infinite);
                }
                else
                {
                    // если всё чисто — таймер можно не дёргать
                    if (!_dirty)
                        CancelTimer_NoLock();
                }
            }
        }, null, Timeout.Infinite, Timeout.Infinite);
    }

    private void CancelTimer_NoLock()
    {
        try { _saveTimer?.Change(Timeout.Infinite, Timeout.Infinite); } catch { }
    }

    /// <summary>
    /// Реальная запись на диск. Возвращает true если успешно записали/или писать было не нужно.
    /// </summary>
    private bool SaveNowInternal(bool force)
    {
        if (Current is null)
            Current = new LauncherConfig();

        // нечего сохранять
        if (!force && !_dirty)
            return true;

        _lastSaveTick = Environment.TickCount64;

        try
        {
            TryNormalize(Current);

            EnsureParentDir(ConfigPath);

            var json = Serialize(Current);

            // если на диске уже то же самое — ничего не делаем (без лишних записей)
            // но если файла нет — обязаны создать
            if (!force && File.Exists(ConfigPath) && _lastSavedJson is not null &&
                string.Equals(_lastSavedJson, json, StringComparison.Ordinal))
            {
                _dirty = false;
                _firstDirtyTick = 0;
                return true;
            }

            var tmp = ConfigPath + ".tmp";
            TryDeleteQuiet(tmp);

            // надёжная запись + flush на диск
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
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
            try { TryDeleteQuiet(ConfigPath + ".tmp"); } catch { }
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

            // если основной конфиг отсутствует — восстанавливаем из tmp
            if (!File.Exists(ConfigPath))
            {
                try
                {
                    File.Move(tmp, ConfigPath, overwrite: true);
                    return;
                }
                catch
                {
                    // если не удалось — просто удалим tmp ниже
                }
            }

            // если основной есть — tmp это мусор от прошлого падения
            TryDeleteQuiet(tmp);
        }
        catch { }
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

    public void Dispose()
    {
        lock (_sync)
        {
            try { _saveTimer?.Dispose(); } catch { }
            _saveTimer = null;
        }
    }
}
