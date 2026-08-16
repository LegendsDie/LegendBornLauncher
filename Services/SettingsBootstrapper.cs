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
    private const string ConfigSchemaVersion = LauncherConfig.CurrentSchemaVersion;
    private const long MaxConfigBytes = 1024L * 1024;

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
            var changed = EnsureSchemaVersionAndNormalize(cfg);

            if (needSave || changed)
                SaveSafe(cfg);
        }
        catch
        {
            try
            {
                ResetCorruptedConfig();
                Directory.CreateDirectory(ConfigDir);

                var cfg = CreateDefaultConfig();
                _ = EnsureSchemaVersionAndNormalize(cfg);
                SaveSafe(cfg);
            }
            catch
            {
                // Bootstrap must never make the launcher unstartable.
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
            var info = new FileInfo(ConfigPath);
            if (info.Length <= 0 || info.Length > MaxConfigBytes)
            {
                ResetCorruptedConfig();
                needSave = true;
                return CreateDefaultConfig();
            }

            var json = File.ReadAllText(ConfigPath, Utf8NoBom);
            var cfg = JsonSerializer.Deserialize<LauncherConfig>(json, JsonOpts);
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

    private static bool EnsureSchemaVersionAndNormalize(LauncherConfig cfg)
    {
        if (cfg is null) return false;

        var changed = false;
        var currentRaw = (cfg.ConfigSchemaVersion ?? "").Trim();
        if (string.IsNullOrWhiteSpace(currentRaw))
            currentRaw = "0.0.0";

        var currentVer = TryParseSchemaVersion(currentRaw, out var parsedCurrent)
            ? parsedCurrent
            : new Version(0, 0, 0);

        var targetVer = TryParseSchemaVersion(ConfigSchemaVersion, out var parsedTarget)
            ? parsedTarget
            : currentVer;

        if (currentVer < targetVer)
        {
            cfg.ConfigSchemaVersion = ConfigSchemaVersion;
            changed = true;
        }
        else if (string.IsNullOrWhiteSpace(cfg.ConfigSchemaVersion))
        {
            cfg.ConfigSchemaVersion = ConfigSchemaVersion;
            changed = true;
        }

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
        }

        return changed;
    }

    private static LauncherConfig CreateDefaultConfig()
        => new()
        {
            ConfigSchemaVersion = ConfigSchemaVersion,
            LastServerId = null,
            AutoLogin = true,
            GameRootPath = null,
            RamMb = 0,
            JavaPath = null,
            LastServerIp = null,
            LastSuccessfulLoginUtc = null,
            LastLauncherStartUtc = null,
            LastUpdateCheckUtc = null,
            LastLauncherVersion = null
        };

    private static void SaveSafe(LauncherConfig cfg)
    {
        try { Directory.CreateDirectory(ConfigDir); } catch { }

        var tmp = ConfigPath + ".tmp";

        try
        {
            var json = JsonSerializer.Serialize(cfg, JsonOpts);
            if (Utf8NoBom.GetByteCount(json) > MaxConfigBytes)
                return;

            try
            {
                if (File.Exists(ConfigPath))
                {
                    var existingInfo = new FileInfo(ConfigPath);
                    if (existingInfo.Length is > 0 and <= MaxConfigBytes)
                    {
                        var existing = File.ReadAllText(ConfigPath, Utf8NoBom);
                        if (string.Equals(existing, json, StringComparison.Ordinal))
                            return;
                    }
                }
            }
            catch
            {
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
        }
        catch
        {
        }
        finally
        {
            TryDeleteQuiet(tmp);
        }
    }

    private static void ReplaceOrMoveAtomic(string sourceTmp, string destPath)
    {
        if (OperatingSystem.IsWindows() && File.Exists(destPath))
        {
            var backup = destPath + ".bootstrap.bak";
            TryDeleteQuiet(backup);
            try
            {
                File.Replace(sourceTmp, destPath, backup, ignoreMetadataErrors: true);
                return;
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
            finally
            {
                TryDeleteQuiet(backup);
            }
        }

        // Keep the existing config intact until the replacement move itself succeeds. Never delete
        // the destination first: a crash or access error in between used to turn a recoverable write
        // failure into a lost config.
        File.Move(sourceTmp, destPath, overwrite: true);
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

            // Do not duplicate an unexpectedly huge file during recovery.
            try
            {
                var info = new FileInfo(ConfigPath);
                if (info.Length is > 0 and <= MaxConfigBytes)
                    File.Copy(ConfigPath, bak, overwrite: true);
            }
            catch
            {
            }

            try { File.Delete(ConfigPath); } catch { }
        }
        catch
        {
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

    private static bool TryParseSchemaVersion(string? input, out Version version)
    {
        version = new Version(0, 0, 0);

        var s = (input ?? "").Trim();
        if (string.IsNullOrWhiteSpace(s))
            return false;

        var dash = s.IndexOf('-');
        if (dash >= 0) s = s[..dash];

        var plus = s.IndexOf('+');
        if (plus >= 0) s = s[..plus];

        if (Version.TryParse(s, out var v))
        {
            version = v;
            return true;
        }

        var parts = s.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 2 && int.TryParse(parts[0], out var a) && int.TryParse(parts[1], out var b))
        {
            version = new Version(a, b, 0);
            return true;
        }

        return false;
    }
}
