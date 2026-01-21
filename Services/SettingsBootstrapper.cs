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
///
/// Важно: bootstrapper НЕ должен ломать пользовательские значения
/// (например, RamMb/AUTO), а только обеспечивать существование и валидность.
/// </summary>
internal static class SettingsBootstrapper
{
    // ✅ единый источник правды
    private const string ConfigSchemaVersion = LauncherConfig.CurrentSchemaVersion;

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
            EnsureSchemaVersionAndNormalize(cfg);
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
                EnsureSchemaVersionAndNormalize(cfg);
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

            // если cfg null -> дефолт
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

    private static void EnsureSchemaVersionAndNormalize(LauncherConfig cfg)
    {
        // ✅ версия схемы
        var current = (cfg.ConfigSchemaVersion ?? "").Trim();
        if (string.IsNullOrWhiteSpace(current))
            current = "0.0.0";

        // Миграции добавляются сюда:
        // if (current == "0.2.6") { ... }

        if (!string.Equals(current, ConfigSchemaVersion, StringComparison.OrdinalIgnoreCase))
            cfg.ConfigSchemaVersion = ConfigSchemaVersion;

        // ✅ главное: нормализация через единый метод
        // (он уже содержит правила RAM 4..16 и т.п.)
        try { cfg.Normalize(); } catch { }
    }

    private static LauncherConfig CreateDefaultConfig()
    {
        // ✅ дефолт: AUTO RAM (0), а не принудительно 4096
        // так пользователь потом сам выставит, но мы не «ломаем» конфиг
        return new LauncherConfig
        {
            ConfigSchemaVersion = ConfigSchemaVersion,
            LastServerId = null,
            AutoLogin = true,
            GameRootPath = null,
            RamMb = 0,          // AUTO
            JavaPath = null,
            LastServerIp = null,
            LastSuccessfulLoginUtc = null,
            LastLauncherStartUtc = null,
            LastUpdateCheckUtc = null,
            LastLauncherVersion = null
        };
    }

    private static void SaveSafe(LauncherConfig cfg)
    {
        try
        {
            var tmp = ConfigPath + ".tmp";
            var json = JsonSerializer.Serialize(cfg, JsonOpts);

            // ✅ надёжная запись tmp
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var sw = new StreamWriter(fs))
            {
                sw.Write(json);
                sw.Flush();
                fs.Flush(flushToDisk: true);
            }

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
