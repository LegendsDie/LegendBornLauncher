namespace LegendBorn.ViewModels;

public sealed partial class MainViewModel
{
    /// <summary>
    /// Raw selected Minecraft skin is preferred for the local 3D renderer. PreviewUrl remains a
    /// compatibility fallback for older profile payloads that do not expose SkinUrl yet.
    /// </summary>
    public string? DashboardSkinUrl => NormalizePublicUrl(
        Profile?.Minecraft?.SelectedSkin?.SkinUrl ??
        Profile?.Minecraft?.SelectedSkin?.PreviewUrl);
}
