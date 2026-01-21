using System;
using System.Reflection;
using System.Runtime.InteropServices;

namespace LegendBorn.Services;

public static class LauncherIdentity
{
    private static readonly Lazy<Assembly> _asm = new(() =>
        Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly());

    private static readonly Lazy<string> _productName = new(() =>
    {
        try
        {
            var asm = _asm.Value;
            return asm.GetName().Name?.Trim() is { Length: > 0 } n ? n : "LegendBornLauncher";
        }
        catch
        {
            return "LegendBornLauncher";
        }
    });

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

    private static readonly Lazy<string> _buildMeta = new(() =>
    {
        try
        {
            var iv = _fullInformational.Value;
            var plus = iv.IndexOf('+');
            if (plus < 0) return "";
            var meta = iv.Substring(plus + 1).Trim();
            return meta;
        }
        catch
        {
            return "";
        }
    });

    public static string ProductName => _productName.Value;

    public static string FullInformationalVersion => _fullInformational.Value;

    public static string InformationalVersion => _infoNoMeta.Value;

    public static string BuildMetadata => _buildMeta.Value;

    public static string DisplayVersion => "v" + InformationalVersion;

    public static string UserAgent
    {
        get
        {
            try
            {
                var os = RuntimeInformation.OSDescription?.Trim();
                if (string.IsNullOrWhiteSpace(os)) os = "Windows";
                return $"{ProductName}/{InformationalVersion} ({os}; {RuntimeInformation.FrameworkDescription})";
            }
            catch
            {
                return $"{ProductName}/{InformationalVersion}";
            }
        }
    }

    public static string AssemblyVersion
    {
        get
        {
            try { return _asm.Value.GetName().Version?.ToString() ?? InformationalVersion; }
            catch { return InformationalVersion; }
        }
    }
}
