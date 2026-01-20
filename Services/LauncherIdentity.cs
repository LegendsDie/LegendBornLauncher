using System;
using System.Reflection;

namespace LegendBorn.Services;

public static class LauncherIdentity
{
    private static readonly Lazy<Assembly> _asm = new(() =>
        Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly());

    private static readonly Lazy<string> _fullInformational = new(() =>
    {
        try
        {
            var asm = _asm.Value;

            var iv =
                asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? asm.GetName().Version?.ToString()
                ?? "0.0.0";

            iv = (iv ?? "").Trim();
            return string.IsNullOrWhiteSpace(iv) ? "0.0.0" : iv;
        }
        catch
        {
            return "0.0.0";
        }
    });

    private static readonly Lazy<string> _infoNoMeta = new(() =>
    {
        try
        {
            var iv = _fullInformational.Value;

            // "0.2.6+sha" -> "0.2.6"
            var plus = iv.IndexOf('+');
            if (plus >= 0)
                iv = iv.Substring(0, plus).Trim();

            return string.IsNullOrWhiteSpace(iv) ? "0.0.0" : iv;
        }
        catch
        {
            return "0.0.0";
        }
    });

    /// <summary>Полная informational версия (может содержать +build-metadata).</summary>
    public static string FullInformationalVersion => _fullInformational.Value;

    /// <summary>Версия для UI/логов без +metadata.</summary>
    public static string InformationalVersion => _infoNoMeta.Value;

    /// <summary>Красивый формат для UI: "v0.2.6".</summary>
    public static string DisplayVersion => "v" + InformationalVersion;

    /// <summary>User-Agent для сетевых запросов.</summary>
    public static string UserAgent => $"LegendBornLauncher/{InformationalVersion}";

    /// <summary>Assembly Version (если нужно для диагностики).</summary>
    public static string AssemblyVersion
    {
        get
        {
            try { return _asm.Value.GetName().Version?.ToString() ?? InformationalVersion; }
            catch { return InformationalVersion; }
        }
    }
}
