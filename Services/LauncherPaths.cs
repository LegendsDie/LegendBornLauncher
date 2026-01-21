using System;
using System.IO;

namespace LegendBorn.Services;

public static class LauncherPaths
{
    public static string AppName => "LegendBorn";

    public static string AppDir => CombineSafe(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        AppName);

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

    // (опционально, но полезно для релиза) - файл лок-метки процесса
    public static string ProcessLockFile => Path.Combine(AppDir, "launcher.lock");

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
    /// Если получился путь вне диска/битый — fallback.
    /// </summary>
    public static string NormalizePathOr(string? path, string fallback)
    {
        try
        {
            var p = (path ?? "").Trim();
            if (string.IsNullOrWhiteSpace(p))
                return fallback;

            // не допускаем "C:\.." в виде относительного грязного ввода
            if (!Path.IsPathRooted(p))
                p = Path.Combine(AppDir, p);

            p = Path.GetFullPath(p);

            // доп. защита: пустота -> fallback
            if (string.IsNullOrWhiteSpace(p))
                return fallback;

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
            if (string.IsNullOrWhiteSpace(fullPath)) return null;

            var root = Path.GetFullPath(AppDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var fp = Path.GetFullPath(fullPath);

            if (!fp.StartsWith(root, StringComparison.OrdinalIgnoreCase))
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
                name = "App";

            return Path.Combine(baseDir, name);
        }
        catch
        {
            return Path.Combine(Path.GetTempPath(), "LegendBorn");
        }
    }
}
