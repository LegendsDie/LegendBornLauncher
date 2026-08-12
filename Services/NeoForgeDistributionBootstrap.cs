using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;

namespace LegendBorn.Services;

/// <summary>
/// Ensures every launcher process knows about independent NeoForge Maven fallbacks before
/// MinecraftService constructs LoaderInstaller. Operators can still append additional mirrors
/// through LEGENDBORN_NEOFORGE_MAVEN_MIRRORS.
/// </summary>
internal static class NeoForgeDistributionBootstrap
{
    internal const string MirrorEnvironmentVariable = "LEGENDBORN_NEOFORGE_MAVEN_MIRRORS";
    internal const string LegendBornProxyBase = "https://legendborn.xyz/api/maven/neoforge/";
    internal const string BmclApiMavenBase = "https://bmclapi2.bangbang93.com/maven/";
    internal const string LegendBornMavenBase = "https://maven.legendborn.ru/";

    [ModuleInitializer]
    internal static void Initialize()
    {
        try
        {
            var mirrors = new List<string>
            {
                // First-party proxy first, independent restricted-network mirror second.
                LegendBornProxyBase,
                BmclApiMavenBase,
                LegendBornMavenBase
            };

            var existing = Environment.GetEnvironmentVariable(MirrorEnvironmentVariable) ?? "";
            mirrors.AddRange(existing.Split(
                new[] { ';', ',', '\r', '\n' },
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

            var value = string.Join(
                ';',
                mirrors
                    .Select(NormalizeHttpsBase)
                    .Where(static mirror => mirror.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase));

            if (value.Length > 0)
                Environment.SetEnvironmentVariable(MirrorEnvironmentVariable, value);
        }
        catch
        {
            // LoaderInstaller still has its built-in Maven/SourceForge/official fallback chain.
        }
    }

    private static string NormalizeHttpsBase(string? value)
    {
        var text = (value ?? "").Trim();
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            return "";

        var builder = new UriBuilder(uri) { Query = "", Fragment = "" };
        if (!builder.Path.EndsWith("/", StringComparison.Ordinal))
            builder.Path += "/";

        return builder.Uri.ToString();
    }
}
