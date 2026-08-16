using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace LegendBorn.Services;

/// <summary>
/// Keeps NeoForge's technical config from carrying dependency overrides for mods that no longer
/// exist in the installed pack. Normal user configs remain untouched. This specifically handles
/// config/fml.toml because NeoForge itself owns dependencyOverrides there and stale entries can
/// survive pack upgrades even when mods/ was reconciled perfectly.
/// </summary>
public static partial class FmlConfigHygieneService
{
    private const long MaxFmlConfigBytes = 2L * 1024 * 1024;
    private const long MaxModMetadataBytes = 1024L * 1024;

    private static readonly string[] MetadataEntries =
    {
        "META-INF/neoforge.mods.toml",
        "META-INF/mods.toml",
        "META-INF/neoforge.mods.json"
    };

    private static readonly HashSet<string> BuiltInModIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "minecraft",
        "neoforge",
        "forge",
        "fml",
        "java"
    };

    public sealed record HygieneResult(int RemovedOverrides, IReadOnlyList<string> RemovedModIds);

    public static Task<HygieneResult> SanitizeAsync(
        string gameDir,
        Action<string>? log = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(gameDir))
            throw new ArgumentException("gameDir is empty", nameof(gameDir));

        ct.ThrowIfCancellationRequested();

        var configPath = Path.Combine(gameDir, "config", "fml.toml");
        if (!File.Exists(configPath))
            return Task.FromResult(new HygieneResult(0, Array.Empty<string>()));

        var info = new FileInfo(configPath);
        if (info.Length <= 0 || info.Length > MaxFmlConfigBytes)
            return Task.FromResult(new HygieneResult(0, Array.Empty<string>()));

        var installed = ReadInstalledModIds(gameDir, ct);
        if (installed.Count <= BuiltInModIds.Count)
        {
            log?.Invoke("Сборка: fml.toml не очищался — не удалось надёжно определить установленные mod id.");
            return Task.FromResult(new HygieneResult(0, Array.Empty<string>()));
        }

        var original = File.ReadAllLines(configPath, Encoding.UTF8);
        var output = new List<string>(original.Length);
        var removed = new List<string>();
        var inDependencyOverrides = false;

        foreach (var line in original)
        {
            ct.ThrowIfCancellationRequested();
            var trimmed = line.Trim();

            if (TryReadSection(trimmed, out var section))
            {
                inDependencyOverrides = section.Equals("dependencyOverrides", StringComparison.OrdinalIgnoreCase);
                output.Add(line);
                continue;
            }

            if (TryReadDottedOverrideTarget(trimmed, out var dottedTarget))
            {
                if (!installed.Contains(dottedTarget))
                {
                    removed.Add(dottedTarget);
                    continue;
                }

                output.Add(line);
                continue;
            }

            if (inDependencyOverrides && TryReadAssignmentKey(trimmed, out var tableTarget))
            {
                if (!installed.Contains(tableTarget))
                {
                    removed.Add(tableTarget);
                    continue;
                }
            }

            output.Add(line);
        }

        var uniqueRemoved = removed
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static id => id, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (uniqueRemoved.Length == 0)
            return Task.FromResult(new HygieneResult(0, uniqueRemoved));

        BackupTechnicalConfig(gameDir, configPath);
        WriteAtomic(configPath, output);

        log?.Invoke(
            "Сборка: из config/fml.toml убраны устаревшие dependencyOverrides: " +
            string.Join(", ", uniqueRemoved.Take(8)) +
            (uniqueRemoved.Length > 8 ? "…" : string.Empty));

        return Task.FromResult(new HygieneResult(uniqueRemoved.Length, uniqueRemoved));
    }

    private static HashSet<string> ReadInstalledModIds(string gameDir, CancellationToken ct)
    {
        var result = new HashSet<string>(BuiltInModIds, StringComparer.OrdinalIgnoreCase);
        var modsDir = Path.Combine(gameDir, "mods");
        if (!Directory.Exists(modsDir))
            return result;

        IEnumerable<string> jars;
        try
        {
            jars = Directory.EnumerateFiles(modsDir, "*.jar", SearchOption.TopDirectoryOnly).ToArray();
        }
        catch
        {
            return result;
        }

        foreach (var jar in jars)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var file = new FileStream(jar, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var zip = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: false);

                foreach (var name in MetadataEntries)
                {
                    var entry = zip.GetEntry(name);
                    if (entry is null || entry.Length <= 0 || entry.Length > MaxModMetadataBytes)
                        continue;

                    using var stream = entry.Open();
                    using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                    var text = reader.ReadToEnd();

                    foreach (Match match in ModIdTomlRegex().Matches(text))
                        AddModId(result, match.Groups[1].Value);
                    foreach (Match match in ModIdJsonRegex().Matches(text))
                        AddModId(result, match.Groups[1].Value);
                }
            }
            catch
            {
                // A broken/non-zip jar will be handled by NeoForge itself. Do not turn config hygiene
                // into another reason the launcher refuses to start.
            }
        }

        return result;
    }

    private static void AddModId(HashSet<string> target, string? raw)
    {
        var value = (raw ?? string.Empty).Trim();
        if (value.Length > 0)
            target.Add(value);
    }

    private static bool TryReadSection(string trimmed, out string section)
    {
        section = string.Empty;
        if (trimmed.Length < 3 || trimmed[0] != '[' || trimmed[^1] != ']')
            return false;

        var inner = trimmed.Trim('[', ']').Trim();
        if (inner.Length == 0)
            return false;

        section = Unquote(inner);
        return true;
    }

    private static bool TryReadDottedOverrideTarget(string trimmed, out string target)
    {
        target = string.Empty;
        if (trimmed.Length == 0 || trimmed.StartsWith('#'))
            return false;

        var eq = trimmed.IndexOf('=');
        if (eq <= 0)
            return false;

        var key = trimmed[..eq].Trim();
        const string prefix = "dependencyOverrides.";
        if (!key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;

        target = Unquote(key[prefix.Length..].Trim());
        return target.Length > 0;
    }

    private static bool TryReadAssignmentKey(string trimmed, out string key)
    {
        key = string.Empty;
        if (trimmed.Length == 0 || trimmed.StartsWith('#'))
            return false;

        var eq = trimmed.IndexOf('=');
        if (eq <= 0)
            return false;

        key = Unquote(trimmed[..eq].Trim());
        return key.Length > 0;
    }

    private static string Unquote(string value)
    {
        var result = value.Trim();
        if (result.Length >= 2 &&
            ((result[0] == '"' && result[^1] == '"') ||
             (result[0] == '\'' && result[^1] == '\'')))
        {
            result = result[1..^1].Trim();
        }

        return result;
    }

    private static void BackupTechnicalConfig(string gameDir, string source)
    {
        try
        {
            var root = Path.Combine(
                gameDir,
                ".trash",
                "pack-cleanup",
                $"fml-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}",
                "config");
            Directory.CreateDirectory(root);
            File.Copy(source, Path.Combine(root, "fml.toml"), overwrite: true);
        }
        catch
        {
            // Backup is best-effort; the rewrite below is atomic and only removes orphan overrides.
        }
    }

    private static void WriteAtomic(string destination, IReadOnlyCollection<string> lines)
    {
        var tmp = destination + ".legendborn.tmp";
        File.WriteAllLines(tmp, lines, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        try
        {
            File.Move(tmp, destination, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
    }

    [GeneratedRegex("(?mi)^\\s*modId\\s*=\\s*[\"']([A-Za-z0-9_.-]+)[\"']")]
    private static partial Regex ModIdTomlRegex();

    [GeneratedRegex("(?mi)[\"']modId[\"']\\s*:\\s*[\"']([A-Za-z0-9_.-]+)[\"']")]
    private static partial Regex ModIdJsonRegex();
}
