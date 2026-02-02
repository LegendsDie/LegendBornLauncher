// File: Services/SettingsBootstrapper.cs
using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using LegendBorn.Models;

namespace LegendBorn.Services;

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

    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private static string ConfigPath => LauncherPaths.ConfigFile;
    private static string ConfigDir => Path.GetDirectoryName(ConfigPath) ?? LauncherPaths.AppDir;

    public static void Bootstrap()
    {
        try
        {
            Directory.CreateDirectory(ConfigDir);

            var cfg = ReadOrCreateDefault(out var needSave);

            // ensure schema + migrations + normalize (может изменить cfg)
            var changed = EnsureSchemaVersionAndNormalize(cfg);

            if (needSave || changed)
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
                _ = EnsureSchemaVersionAndNormalize(cfg); // нормализуем и проставим версию схемы
                SaveSafe(cfg);
            }
            catch
            {
                // не валим запуск
            }
        }
    }

    private static LauncherConfig ReadOrCreateDefault(out bool needSave)
    {
        needSave = false;

        if (!File.Exists(ConfigPath))
        {
            needSave = true;
            return CreateDefaultConfig();
        }

        try
        {
            var json = File.ReadAllText(ConfigPath, Utf8NoBom);
            var cfg = JsonSerializer.Deserialize<LauncherConfig>(json, JsonOpts);

            // если cfg null -> дефолт
            if (cfg is null)
            {
                needSave = true;
                return CreateDefaultConfig();
            }

            return cfg;
        }
        catch
        {
            ResetCorruptedConfig();
            needSave = true;
            return CreateDefaultConfig();
        }
    }

    /// <summary>
    /// Возвращает true, если cfg был изменён (версия схемы/миграции/Normalize).
    /// </summary>
    private static bool EnsureSchemaVersionAndNormalize(LauncherConfig cfg)
    {
        if (cfg is null) return false;

        var changed = false;

        // ===== Version parsing (semver-ish) =====
        var currentRaw = (cfg.ConfigSchemaVersion ?? "").Trim();
        if (string.IsNullOrWhiteSpace(currentRaw))
            currentRaw = "0.0.0";

        var currentVer = TryParseSchemaVersion(currentRaw, out var parsedCurrent)
            ? parsedCurrent
            : new Version(0, 0, 0);

        var targetVer = TryParseSchemaVersion(ConfigSchemaVersion, out var parsedTarget)
            ? parsedTarget
            : currentVer; // fallback: если вдруг константа странная

        // ===== Migrations go here (based on currentVer) =====
        // IMPORTANT: миграции должны быть ИДЕМПОТЕНТНЫМИ
        //
        // Пример:
        // if (currentVer < new Version(0,2,8))
        // {
        //     // migrate...
        //     changed = true;
        // }

        // ===== Schema version policy =====
        // Не даунгрейдим схему, если у пользователя версия новее (например, откатил лаунчер)
        if (currentVer < targetVer)
        {
            cfg.ConfigSchemaVersion = ConfigSchemaVersion;
            changed = true;
        }
        else if (string.IsNullOrWhiteSpace(cfg.ConfigSchemaVersion))
        {
            // если строка пустая, но Version уже >= target (редкий кейс) — всё равно проставим
            cfg.ConfigSchemaVersion = ConfigSchemaVersion;
            changed = true;
        }

        // ===== Normalize (может менять поля) =====
        try
        {
            var before = SafeSerialize(cfg);

            cfg.Normalize();

            var after = SafeSerialize(cfg);
            if (!string.Equals(before, after, StringComparison.Ordinal))
                changed = true;
        }
        catch
        {
            // если Normalize упал — не валим запуск, но и не считаем changed
        }

        return changed;
    }

    private static LauncherConfig CreateDefaultConfig()
    {
        // ✅ дефолт: AUTO RAM (0), а не принудительно 4096
        // пользователь потом сам выставит, но мы не «ломаем» конфиг
        return new LauncherConfig
        {
            ConfigSchemaVersion = ConfigSchemaVersion,
            LastServerId = null,
            AutoLogin = true,
            GameRootPath = null,
            RamMb = 0, // AUTO
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
        // Пишем только если можем создать директорию
        try { Directory.CreateDirectory(ConfigDir); } catch { }

        var tmp = ConfigPath + ".tmp";

        try
        {
            // Снижаем лишние записи: если файл уже идентичен — ничего не делаем
            var json = JsonSerializer.Serialize(cfg, JsonOpts);

            try
            {
                if (File.Exists(ConfigPath))
                {
                    var existing = File.ReadAllText(ConfigPath, Utf8NoBom);
                    if (!string.IsNullOrWhiteSpace(existing) && string.Equals(existing, json, StringComparison.Ordinal))
                        return;
                }
            }
            catch
            {
                // игнор — перезапишем
            }

            TryDeleteQuiet(tmp);

            // ✅ надёжная запись tmp (UTF8 no-BOM) + flush to disk
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var sw = new StreamWriter(fs, Utf8NoBom))
            {
                sw.Write(json);
                sw.Flush();
                fs.Flush(flushToDisk: true);
            }

            ReplaceOrMoveAtomic(tmp, ConfigPath);
        }
        catch
        {
            // игнор в релизе
        }
        finally
        {
            TryDeleteQuiet(tmp);
        }
    }

    private static void ReplaceOrMoveAtomic(string sourceTmp, string destPath)
    {
        try
        {
            if (OperatingSystem.IsWindows() && File.Exists(destPath))
            {
                // можно включить backup при желании:
                // var backup = destPath + ".bak";
                // File.Replace(sourceTmp, destPath, backup, ignoreMetadataErrors: true);

                File.Replace(sourceTmp, destPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
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

    private static void ResetCorruptedConfig()
    {
        try
        {
            if (!File.Exists(ConfigPath))
                return;

            Directory.CreateDirectory(ConfigDir);

            var ts = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture);
            var bak = Path.Combine(ConfigDir, $"launcher.config.broken.{ts}.json");

            try { File.Copy(ConfigPath, bak, overwrite: true); } catch { }

            try { File.Delete(ConfigPath); } catch { }
        }
        catch
        {
            // ignore
        }
    }

    private static void TryDeleteQuiet(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static string SafeSerialize(LauncherConfig cfg)
    {
        try { return JsonSerializer.Serialize(cfg, JsonOpts); }
        catch { return ""; }
    }

    /// <summary>
    /// Пытается распарсить версию схемы (semver-ish).
    /// Поддерживает "0.2.8" и "0.2.8-beta" (суффикс отрезаем).
    /// </summary>
    private static bool TryParseSchemaVersion(string? input, out Version version)
    {
        version = new Version(0, 0, 0);

        var s = (input ?? "").Trim();
        if (string.IsNullOrWhiteSpace(s))
            return false;

        // отрезаем pre-release/metadata: 0.2.8-beta+123 -> 0.2.8
        var dash = s.IndexOf('-');
        if (dash >= 0) s = s[..dash];

        var plus = s.IndexOf('+');
        if (plus >= 0) s = s[..plus];

        // Version требует минимум major.minor, но 0.0.0 тоже ок
        if (Version.TryParse(s, out var v))
        {
            version = v;
            return true;
        }

        // fallback: пытаемся дополнить до major.minor.build
        // например "0.2" => "0.2.0"
        var parts = s.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 2 && int.TryParse(parts[0], out var a) && int.TryParse(parts[1], out var b))
        {
            version = new Version(a, b, 0);
            return true;
        }

        return false;
    }
}
