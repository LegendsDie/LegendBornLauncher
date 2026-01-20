using System.Text.Json;
using LegendBorn.Models;

namespace LegendBorn.Services;

public sealed class ConfigService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
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
            if (!File.Exists(ConfigPath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
                Save();
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
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        var json = JsonSerializer.Serialize(Current, JsonOptions);
        File.WriteAllText(ConfigPath, json);
    }

    private void TryBackupBrokenConfig()
    {
        try
        {
            var bak = ConfigPath + ".broken." + DateTime.UtcNow.ToString("yyyyMMddHHmmss") + ".bak";
            File.Copy(ConfigPath, bak, overwrite: true);
        }
        catch { /* ignore */ }
    }
}