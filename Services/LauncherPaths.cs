// File: Services/LauncherPaths.cs
using System;
using System.IO;

namespace LegendBorn.Services;

public static class LauncherPaths
{
    public const string AppName = "LegendBorn";

    // Сравнение путей: Windows case-insensitive, Linux/macOS case-sensitive
    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static string AppDir => CombineSafe(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        AppName);

    public static string LocalDir => CombineSafe(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        AppName);

    // ===== Core dirs =====
    // Логи/краши разумнее хранить в Local (не роумить). Если хочешь как было — верни AppDir.
    public static string LogsDir  => Path.Combine(LocalDir, "logs");
    public static string CrashDir => Path.Combine(LocalDir, "crash");
    public static string CacheDir => Path.Combine(LocalDir, "cache");

    // ===== Core files =====
    public static string ConfigFile => Path.Combine(AppDir, "launcher.config.json");
    public static string TokenFile  => Path.Combine(LocalDir, "launcher.tokens.dat"); // токены лучше в Local

    // (опционально, но полезно для релиза) - файл лок-метки процесса
    public static string ProcessLockFile => Path.Combine(LocalDir, "launcher.lock");

    public static string DefaultGameDir => Path.Combine(LocalDir, "game");

    public static string LauncherLogFile => Path.Combine(LogsDir, "launcher.log");

    public static void EnsureAppDirs()
    {
        EnsureDir(AppDir);
        EnsureDir(LocalDir);
        EnsureDir(LogsDir);
        EnsureDir(CrashDir);
        EnsureDir(CacheDir);

        EnsureParentDirForFile(ConfigFile);
        EnsureParentDirForFile(TokenFile);
        EnsureParentDirForFile(LauncherLogFile);
        EnsureParentDirForFile(ProcessLockFile);
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
            if (string.IsNullOrWhiteSpace(filePath))
                return filePath;

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
    /// ВАЖНО: относительный путь НЕ может "выйти" наружу AppDir через ".." — иначе fallback.
    /// </summary>
    public static string NormalizePathOr(string? path, string fallback)
    {
        try
        {
            var p = (path ?? "").Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(p))
                return fallback;

            var wasRelative = !Path.IsPathRooted(p);

            // относительные разворачиваем от AppDir
            if (wasRelative)
                p = Path.Combine(AppDir, p);

            p = Path.GetFullPath(p);

            if (string.IsNullOrWhiteSpace(p))
                return fallback;

            // защита: если ввод был относительный — не даём выйти за AppDir
            if (wasRelative)
            {
                var appRoot = Path.GetFullPath(AppDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

                // Нормализуем разделители для стабильности сравнения
                appRoot = appRoot.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
                p = p.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);

                if (!p.StartsWith(appRoot, PathComparison))
                    return fallback;
            }

            return p;
        }
        catch
        {
            return fallback;
        }
    }

    /// <summary>
    /// Если путь находится внутри AppDir — вернёт относительный (в стиле "sub\file"),
    /// иначе вернёт null.
    /// </summary>
    public static string? TryGetRelativeToAppDir(string fullPath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(fullPath))
                return null;

            var root = Path.GetFullPath(AppDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var fp = Path.GetFullPath(fullPath);

            root = root.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
            fp = fp.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);

            if (!fp.StartsWith(root, PathComparison))
                return null;

            return Path.GetRelativePath(root, fp);
        }
        catch
        {
            return null;
        }
    }

    private static string CombineSafe(string baseDir, string name)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(baseDir))
                baseDir = Path.GetTempPath();

            if (string.IsNullOrWhiteSpace(name))
                name = AppName;

            return Path.Combine(baseDir, name);
        }
        catch
        {
            return Path.Combine(Path.GetTempPath(), AppName);
        }
    }
}
