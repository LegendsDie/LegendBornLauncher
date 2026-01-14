using System;
using System.Reflection;

namespace LegendBorn.Services;

public static class LauncherIdentity
{
    private static readonly Lazy<string> _infoVersion = new(() =>
    {
        try
        {
            var asm = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();

            var iv =
                asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? asm.GetName().Version?.ToString()
                ?? "0.0.0";

            iv = (iv ?? "").Trim();
            if (string.IsNullOrWhiteSpace(iv))
                iv = "0.0.0";

            // "0.2.2+sha" -> "0.2.2"
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

    /// <summary>
    /// Версия приложения для отображения и логов (без build-metadata).
    /// </summary>
    public static string InformationalVersion => _infoVersion.Value;

    /// <summary>
    /// User-Agent для сетевых запросов.
    /// </summary>
    public static string UserAgent => $"LegendBornLauncher/{InformationalVersion}";
}