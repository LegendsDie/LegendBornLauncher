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
                var dir = Path.GetDirectoryName(ConfigPath);
                if (!string.IsNullOrWhiteSpace(dir))
                    Directory.CreateDirectory(dir);

                if (!File.Exists(ConfigPath))
                {
                    Current = new LauncherConfig();
                    TryNormalize(Current);
                    Save();
                    return Current;
                }

                var json = File.ReadAllText(ConfigPath, Utf8NoBom);
                var cfg = JsonSerializer.Deserialize<LauncherConfig>(json, JsonOptions) ?? new LauncherConfig();

                TryNormalize(cfg);

                Current = cfg;
                return Current;
            }
            catch
            {
                TryBackupBrokenConfig();

                Current = new LauncherConfig();
                TryNormalize(Current);

                try { Save(); } catch { }

                return Current;
            }
        }
    }

    public void Save()
    {
        lock (_sync)
        {
            try
            {
                TryNormalize(Current);

                var dir = Path.GetDirectoryName(ConfigPath);
                if (!string.IsNullOrWhiteSpace(dir))
                    Directory.CreateDirectory(dir);

                var tmp = ConfigPath + ".tmp";
                var json = JsonSerializer.Serialize(Current, JsonOptions);

                File.WriteAllText(tmp, json, Utf8NoBom);

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
    }

    private void TryBackupBrokenConfig()
    {
        try
        {
            if (!File.Exists(ConfigPath)) return;

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
}
