using System;
using System.IO;
using System.Text.Json;
using LegendBorn.Models;

namespace LegendBorn.Services;

/// <summary>
/// Релизный bootstrap конфигурации лаунчера (launcher.config.json).
/// Цели:
/// 1) Гарантировать наличие валидного конфига.
/// 2) Держать версию схемы (ConfigSchemaVersion) отдельно от версии лаунчера.
/// 3) Самовосстановление при битом JSON (backup + reset).
/// </summary>
internal static class SettingsBootstrapper
{
    private const string ConfigSchemaVersion = "0.2.6";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private static string ConfigPath => LauncherPaths.ConfigFile;
    private static string ConfigDir => Path.GetDirectoryName(ConfigPath) ?? LauncherPaths.AppDir;

    public static void Bootstrap()
    {
        try
        {
            Directory.CreateDirectory(ConfigDir);

            var cfg = ReadOrCreateDefault();
            EnsureSchemaVersion(cfg);
            SaveSafe(cfg);
        }
        catch
        {
            // release-safe: при любой проблеме пробуем восстановить
            try
            {
                ResetCorruptedConfig();
                Directory.CreateDirectory(ConfigDir);

                var cfg = CreateDefaultConfig();
                EnsureSchemaVersion(cfg);
                SaveSafe(cfg);
            }
            catch
            {
                // не валим запуск
            }
        }
    }

    private static LauncherConfig ReadOrCreateDefault()
    {
        if (!File.Exists(ConfigPath))
        {
            var fresh = CreateDefaultConfig();
            SaveSafe(fresh);
            return fresh;
        }

        try
        {
            var json = File.ReadAllText(ConfigPath);
            var cfg = JsonSerializer.Deserialize<LauncherConfig>(json, JsonOpts);
            return cfg ?? CreateDefaultConfig();
        }
        catch
        {
            ResetCorruptedConfig();
            var fresh = CreateDefaultConfig();
            SaveSafe(fresh);
            return fresh;
        }
    }

    private static void EnsureSchemaVersion(LauncherConfig cfg)
    {
        var current = (cfg.ConfigSchemaVersion ?? "").Trim();
        if (string.IsNullOrWhiteSpace(current))
            current = "0.0.0";

        // Миграции добавляются сюда:
        // if (current == "0.2.5") { ... }

        if (!string.Equals(current, ConfigSchemaVersion, StringComparison.OrdinalIgnoreCase))
            cfg.ConfigSchemaVersion = ConfigSchemaVersion;

        // лёгкая нормализация (чтобы релиз был стабильнее)
        if (cfg.RamMb < 1024) cfg.RamMb = 4096;
        cfg.LastServerIp ??= "";
        cfg.GameRootPath ??= "";
    }

    private static LauncherConfig CreateDefaultConfig()
    {
        return new LauncherConfig
        {
            ConfigSchemaVersion = ConfigSchemaVersion,
            LastServerId = null,
            AutoLogin = true,
            GameRootPath = "",
            RamMb = 4096,
            JavaPath = null,
            LastServerIp = "",
            LastSuccessfulLoginUtc = null
        };
    }

    private static void SaveSafe(LauncherConfig cfg)
    {
        try
        {
            var tmp = ConfigPath + ".tmp";
            var json = JsonSerializer.Serialize(cfg, JsonOpts);

            File.WriteAllText(tmp, json);

            if (File.Exists(ConfigPath))
            {
                try
                {
                    File.Replace(tmp, ConfigPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
                }
                catch
                {
                    File.Delete(ConfigPath);
                    File.Move(tmp, ConfigPath);
                }
            }
            else
            {
                File.Move(tmp, ConfigPath);
            }
        }
        catch
        {
            try
            {
                var tmp = ConfigPath + ".tmp";
                if (File.Exists(tmp)) File.Delete(tmp);
            }
            catch { }
        }
    }

    private static void ResetCorruptedConfig()
    {
        try
        {
            if (!File.Exists(ConfigPath))
                return;

            var bak = Path.Combine(ConfigDir, $"launcher_corrupt_{DateTime.Now:yyyyMMdd_HHmmss}.json");
            try { File.Copy(ConfigPath, bak, overwrite: true); } catch { }

            try { File.Delete(ConfigPath); } catch { }
        }
        catch
        {
            // ignore
        }
    }
}
