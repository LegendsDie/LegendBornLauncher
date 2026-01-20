using System;
using System.IO;

namespace LegendBorn.Services;

/// <summary>
/// Централизованные пути лаунчера.
/// Roaming: %AppData%\LegendBorn        (настройки/токены/логи)
/// Local:   %LocalAppData%\LegendBorn  (кэш/игра/тяжёлые файлы)
/// </summary>
public static class LauncherPaths
{
    public static string AppName => "LegendBorn";

    /// <summary>%AppData%\LegendBorn</summary>
    public static string AppDir => CombineSafe(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        AppName);

    /// <summary>%LocalAppData%\LegendBorn</summary>
    public static string LocalDir => CombineSafe(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        AppName);

    // ===== Core dirs =====
    public static string LogsDir  => Path.Combine(AppDir, "logs");
    public static string CrashDir => Path.Combine(AppDir, "crash");
    public static string CacheDir => Path.Combine(LocalDir, "cache");

    // ===== Core files =====
    public static string ConfigFile => Path.Combine(AppDir, "launcher.config.json");
    public static string TokenFile  => Path.Combine(AppDir, "launcher.tokens.dat");

    /// <summary>
    /// Игровая папка по умолчанию: LocalAppData (не раздувает Roaming).
    /// </summary>
    public static string DefaultGameDir => Path.Combine(LocalDir, "game");

    // ===== Logging =====
    public static string LauncherLogFile => Path.Combine(LogsDir, "launcher.log");

    // ===== Ensure helpers =====
    public static void EnsureAppDirs()
    {
        EnsureDir(AppDir);
        EnsureDir(LocalDir);
        EnsureDir(LogsDir);
        EnsureDir(CrashDir);
        EnsureDir(CacheDir);
    }

    public static string EnsureDir(string path)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(path))
                Directory.CreateDirectory(path);
        }
        catch { }
        return path;
    }

    public static string EnsureParentDirForFile(string filePath)
    {
        try
        {
            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);
        }
        catch { }
        return filePath;
    }

    /// <summary>
    /// Нормализует путь (делает абсолютным). Если пустой/битый — отдаёт fallback.
    /// Если путь относительный — разворачивает его относительно AppDir.
    /// </summary>
    public static string NormalizePathOr(string? path, string fallback)
    {
        try
        {
            var p = (path ?? "").Trim();
            if (string.IsNullOrWhiteSpace(p))
                return fallback;

            if (!Path.IsPathRooted(p))
                p = Path.Combine(AppDir, p);

            p = Path.GetFullPath(p);
            return string.IsNullOrWhiteSpace(p) ? fallback : p;
        }
        catch
        {
            return fallback;
        }
    }

    private static string CombineSafe(string baseDir, string name)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(baseDir))
                baseDir = Path.GetTempPath();

            if (string.IsNullOrWhiteSpace(name))
                name = "App";

            return Path.Combine(baseDir, name);
        }
        catch
        {
            return Path.Combine(Path.GetTempPath(), "LegendBorn");
        }
    }
}
