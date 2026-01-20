using System;
using System.IO;
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

    public string ConfigPath { get; }
    public LauncherConfig Current { get; private set; } = new();

    public ConfigService(string configPath)
    {
        ConfigPath = configPath;
    }

    public LauncherConfig LoadOrCreate()
    {
        try
        {
            var dir = Path.GetDirectoryName(ConfigPath);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);

            if (!File.Exists(ConfigPath))
            {
                Save(); // создаём файл из Current
                return Current;
            }

            var json = File.ReadAllText(ConfigPath);
            var cfg = JsonSerializer.Deserialize<LauncherConfig>(json, JsonOptions) ?? new LauncherConfig();
            Current = cfg;
            return Current;
        }
        catch
        {
            // если конфиг битый — сохраняем бэкап и пересоздаём
            TryBackupBrokenConfig();
            Current = new LauncherConfig();
            Save();
            return Current;
        }
    }

    public void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(ConfigPath);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);

            var tmp = ConfigPath + ".tmp";
            var json = JsonSerializer.Serialize(Current, JsonOptions);
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
            // release-safe ignore
            try
            {
                var tmp = ConfigPath + ".tmp";
                if (File.Exists(tmp)) File.Delete(tmp);
            }
            catch { }
        }
    }

    private void TryBackupBrokenConfig()
    {
        try
        {
            if (!File.Exists(ConfigPath)) return;
            var bak = ConfigPath + ".broken." + DateTime.UtcNow.ToString("yyyyMMddHHmmss") + ".bak";
            File.Copy(ConfigPath, bak, overwrite: true);
        }
        catch { /* ignore */ }
    }
}
