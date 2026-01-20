namespace LegendBorn.Models;

public sealed class LauncherConfig
{
    public string ConfigSchemaVersion { get; set; } = "0.2.6";

    // UI/UX
    public string? LastServerId { get; set; }
    public bool AutoLogin { get; set; } = true;

    // Minecraft
    public string? GameRootPath { get; set; }          // если пусто — используем AppData\LegendBorn
    public int RamMb { get; set; } = 4096;
    public string? JavaPath { get; set; }              // опционально

    // Connection
    public string? LastServerIp { get; set; }

    // Misc
    public DateTimeOffset? LastSuccessfulLoginUtc { get; set; }
}