namespace LegendBorn.Services;

public static class LauncherPaths
{
    public static string AppDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LegendBorn");

    public static string LogsDir => Path.Combine(AppDir, "logs");
    public static string ConfigFile => Path.Combine(AppDir, "launcher.config.json");
    public static string TokenFile => Path.Combine(AppDir, "launcher.tokens.dat");
    public static string DefaultGameDir => Path.Combine(AppDir, "game");
}