using System.Reflection;

namespace LegendBorn.Services;

internal static class LauncherIdentity
{
    public static string InformationalVersion
    {
        get
        {
            var asm = typeof(LauncherIdentity).Assembly;
            var iv = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(iv))
                return iv.Trim();

            return asm.GetName().Version?.ToString() ?? "0.0.0";
        }
    }

    public static string UserAgent => $"LegendBornLauncher/{InformationalVersion}";
}