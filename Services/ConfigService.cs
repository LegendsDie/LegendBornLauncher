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
                return Current;
            }
            catch
            {
                TryBackupBrokenConfig();

                Current = new LauncherConfig();
                TryNormalize(Current);

                try { SaveInternal(force: true); } catch { }

                return Current;
            }
        }
    }

    public void Save()
    {
        lock (_sync)
        {
            SaveInternal(force: false);
        }
    }

    private void SaveInternal(bool force)
    {
        if (Current is null)
            Current = new LauncherConfig();

        if (!force)
        {
            var now = Environment.TickCount64;
            var last = _lastSaveTick;
            if (now - last < MinSaveIntervalMs)
                return;

            _lastSaveTick = now;
        }

        try
        {
            TryNormalize(Current);

            EnsureParentDir(ConfigPath);

            var tmp = ConfigPath + ".tmp";
            var json = JsonSerializer.Serialize(Current, JsonOptions);

            File.WriteAllText(tmp, json, Utf8NoBom);

            ReplaceOrMoveAtomic(tmp, ConfigPath);

            TryDeleteQuiet(tmp);
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
