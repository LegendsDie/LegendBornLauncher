using System;
using System.Collections.Generic;
using System.Linq;

namespace LegendBorn.Services;

internal sealed record NeoForgeDistributionSpec(
    string LoaderVersion,
    string InstallerUrl,
    string[] InstallerMirrors,
    string InstallerSha256,
    string[] MavenMirrors,
    string InstallerMirrorArgument);

/// <summary>
/// Runtime registry for the NeoForge distribution contract received from the authoritative
/// LegendBorn server catalog. The catalog is the source of truth; this class deliberately does
/// not inject a web-app Maven proxy or mutate installer artifacts.
/// </summary>
internal static class NeoForgeDistributionBootstrap
{
    internal const string MirrorEnvironmentVariable = "LEGENDBORN_NEOFORGE_MAVEN_MIRRORS";
    internal const string BmclApiMavenBase = "https://bmclapi2.bangbang93.com/maven/";
    internal const string OfficialMavenBase = "https://maven.neoforged.net/releases/";
    internal const string RequiredMirrorArgument = "--mirror";

    private static readonly object Sync = new();
    private static readonly Dictionary<string, NeoForgeDistributionSpec> Specs =
        new(StringComparer.OrdinalIgnoreCase);
    private static string[] _registeredMavenMirrors = Array.Empty<string>();

    internal static void Reset()
    {
        lock (Sync)
        {
            Specs.Clear();
            _registeredMavenMirrors = Array.Empty<string>();
        }
    }

    internal static bool TryRegister(
        string? loaderVersion,
        string? installerUrl,
        IEnumerable<string>? installerMirrors,
        string? installerSha256,
        IEnumerable<string>? mavenMirrors,
        string? installerMirrorArgument,
        out string error)
    {
        error = "";

        var version = (loaderVersion ?? "").Trim();
        if (version.Length == 0)
        {
            error = "loader.version is empty";
            return false;
        }

        var digest = NormalizeSha256(installerSha256);
        if (!IsSha256(digest))
        {
            error = "loader.installerSha256 must be exactly 64 hexadecimal characters";
            return false;
        }

        var primary = NormalizeHttpsUrl(installerUrl);
        var installers = (installerMirrors ?? Array.Empty<string>())
            .Prepend(primary)
            .Select(NormalizeHttpsUrl)
            .Where(static value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (installers.Length == 0)
        {
            error = "loader.installerMirrors is empty";
            return false;
        }

        var mavens = (mavenMirrors ?? Array.Empty<string>())
            .Select(NormalizeHttpsBase)
            .Where(static value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (mavens.Length == 0)
        {
            error = "loader.mavenMirrors is empty";
            return false;
        }

        var mirrorArgument = (installerMirrorArgument ?? "").Trim();
        if (!string.Equals(mirrorArgument, RequiredMirrorArgument, StringComparison.Ordinal))
        {
            error = $"loader.installerMirrorArgument must be '{RequiredMirrorArgument}'";
            return false;
        }

        var spec = new NeoForgeDistributionSpec(
            version,
            primary.Length > 0 ? primary : installers[0],
            installers,
            digest,
            mavens,
            mirrorArgument);

        lock (Sync)
        {
            Specs[version] = spec;
            _registeredMavenMirrors = Specs.Values
                .SelectMany(static value => value.MavenMirrors)
                .Select(NormalizeHttpsBase)
                .Where(static value => value.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        return true;
    }

    internal static bool TryResolve(string? loaderVersion, out NeoForgeDistributionSpec spec)
    {
        var version = (loaderVersion ?? "").Trim();
        lock (Sync)
            return Specs.TryGetValue(version, out spec!);
    }

    internal static string[] GetRegisteredMavenMirrors()
    {
        lock (Sync)
            return (string[])_registeredMavenMirrors.Clone();
    }

    internal static string[] GetEffectiveMavenMirrors(NeoForgeDistributionSpec spec)
    {
        var values = new List<string>(spec.MavenMirrors);

        try
        {
            var configured = Environment.GetEnvironmentVariable(MirrorEnvironmentVariable) ?? "";
            values.AddRange(configured.Split(
                new[] { ';', ',', '\r', '\n' },
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }
        catch
        {
        }

        return values
            .Select(NormalizeHttpsBase)
            .Where(static value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    internal static string DescribeSource(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return "unknown";

        if (uri.Host.Contains("selstorage.ru", StringComparison.OrdinalIgnoreCase) ||
            uri.Host.Contains("selcloud.ru", StringComparison.OrdinalIgnoreCase))
            return "LegendBorn Selectel";

        if (uri.Host.Equals("bmclapi2.bangbang93.com", StringComparison.OrdinalIgnoreCase))
            return "BMCLAPI";

        if (uri.Host.Equals("maven.neoforged.net", StringComparison.OrdinalIgnoreCase))
            return "NeoForge official";

        return uri.Host;
    }

    internal static string NormalizeSha256(string? value)
        => (value ?? "").Trim().ToLowerInvariant();

    internal static bool IsSha256(string? value)
    {
        var text = NormalizeSha256(value);
        if (text.Length != 64) return false;

        foreach (var ch in text)
        {
            if (ch is >= '0' and <= '9') continue;
            if (ch is >= 'a' and <= 'f') continue;
            return false;
        }

        return true;
    }

    internal static string NormalizeHttpsUrl(string? value)
    {
        var text = (value ?? "").Trim();
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            return "";

        return new UriBuilder(uri) { Fragment = "" }.Uri.ToString();
    }

    internal static string NormalizeHttpsBase(string? value)
    {
        var url = NormalizeHttpsUrl(value);
        if (url.Length == 0) return "";
        return url.EndsWith("/", StringComparison.Ordinal) ? url : url + "/";
    }
}
