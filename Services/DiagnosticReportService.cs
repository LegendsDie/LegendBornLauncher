using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using LegendBorn.Launching;

namespace LegendBorn.Services;

public static partial class DiagnosticReportService
{
    private const long MaxSourceFileBytes = 8L * 1024 * 1024;
    private const int MaxLauncherLogs = 4;
    private const int MaxCrashFiles = 5;
    private const int MaxGameCrashFiles = 3;
    private const int MaxInventoryFiles = 12_000;
    private const int MaxQuarantineInventoryFiles = 2_000;

    private static readonly string[] ManagedInventoryRoots = { "mods", "kubejs", "scripts" };
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    [GeneratedRegex("(?i)(authorization\\s*[:=]\\s*bearer\\s+)[A-Za-z0-9._~+\\-/=]+")]
    private static partial Regex BearerRegex();

    [GeneratedRegex("(?i)((?:access[_-]?token|refresh[_-]?token|join[_-]?ticket|ticket)\\s*[=:]\\s*[\\\"']?)[A-Za-z0-9._~+\\-/=]{16,}")]
    private static partial Regex NamedSecretRegex();

    [GeneratedRegex("(?i)(\\\"(?:accessToken|refreshToken|ticket)\\\"\\s*:\\s*\\\")[^\\\"]+(\\\")")]
    private static partial Regex JsonSecretRegex();

    [GeneratedRegex("\\b[a-fA-F0-9]{64}\\b")]
    private static partial Regex HexTokenRegex();

    public sealed record ReportContext(
        string GameDir,
        string? ServerId,
        string? ServerName,
        string? ServerAddress,
        string? MinecraftVersion,
        string? LoaderName,
        string? LoaderVersion,
        int RamMb,
        bool AutoConnect);

    public static async Task<string> CreateAsync(ReportContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var reportsDir = LauncherPaths.EnsureDir(Path.Combine(LauncherPaths.LocalDir, "reports"));
        var stamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss");
        var workDir = Path.Combine(reportsDir, $"report-{stamp}-{Guid.NewGuid():N}");
        var zipPath = Path.Combine(reportsDir, $"LegendBorn-diagnostics-{stamp}.zip");

        Directory.CreateDirectory(workDir);

        try
        {
            var secrets = LoadEphemeralSecrets(context.GameDir);

            await WriteMetadataAsync(workDir, context, ct).ConfigureAwait(false);

            CopyNewestTextFiles(
                LauncherPaths.LogsDir,
                Path.Combine(workDir, "launcher-logs"),
                MaxLauncherLogs,
                secrets,
                ct,
                allowedExtensions: new[] { ".log", ".txt" });

            CopyNewestTextFiles(
                LauncherPaths.CrashDir,
                Path.Combine(workDir, "launcher-crash"),
                MaxCrashFiles,
                secrets,
                ct,
                allowedExtensions: new[] { ".log", ".txt", ".json" });

            var gameDir = NormalizeDirectory(context.GameDir);
            if (gameDir is not null)
            {
                CopySpecificTextFile(
                    Path.Combine(gameDir, "logs", "latest.log"),
                    Path.Combine(workDir, "game", "latest.log"),
                    secrets,
                    ct);

                CopyNewestTextFiles(
                    Path.Combine(gameDir, "crash-reports"),
                    Path.Combine(workDir, "game", "crash-reports"),
                    MaxGameCrashFiles,
                    secrets,
                    ct,
                    allowedExtensions: new[] { ".txt", ".log" });

                // Pack state and path-only inventories make stale-file upgrade reports actionable
                // without copying the user's configs, saves, resource packs or shader packs.
                CopySpecificTextFile(
                    Path.Combine(gameDir, "launcher", "pack_state.json"),
                    Path.Combine(workDir, "pack", "pack_state.json"),
                    secrets,
                    ct);

                WriteFileInventory(
                    gameDir,
                    ManagedInventoryRoots,
                    Path.Combine(workDir, "pack", "managed-files.txt"),
                    MaxInventoryFiles,
                    ct);

                WriteFileInventory(
                    gameDir,
                    new[] { ".trash/pack-cleanup" },
                    Path.Combine(workDir, "pack", "quarantine-files.txt"),
                    MaxQuarantineInventoryFiles,
                    ct);
            }

            await File.WriteAllTextAsync(
                Path.Combine(workDir, "PRIVACY.txt"),
                "This diagnostic bundle intentionally excludes launcher.tokens.dat, auth.token, auth.json and .legendcore/session.json.\n" +
                "Bearer/access/refresh/join-ticket shaped values found in copied text logs are redacted before archiving.\n" +
                "The Windows machine/computer name is intentionally not collected.\n" +
                "Pack inventories contain relative file names, sizes and timestamps only; user configs/saves are not copied.\n",
                Utf8NoBom,
                ct).ConfigureAwait(false);

            WriteContentsIndex(workDir, ct);
            ct.ThrowIfCancellationRequested();

            if (File.Exists(zipPath))
                File.Delete(zipPath);

            ZipFile.CreateFromDirectory(
                workDir,
                zipPath,
                CompressionLevel.Optimal,
                includeBaseDirectory: false,
                entryNameEncoding: Encoding.UTF8);

            return zipPath;
        }
        finally
        {
            TryDeleteDirectory(workDir);
        }
    }

    private static async Task WriteMetadataAsync(string workDir, ReportContext context, CancellationToken ct)
    {
        var metadata = new
        {
            generatedAtUtc = DateTimeOffset.UtcNow,
            launcher = new
            {
                version = LauncherIdentity.InformationalVersion,
                framework = RuntimeInformation.FrameworkDescription,
                processArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
                osArchitecture = RuntimeInformation.OSArchitecture.ToString(),
            },
            system = new
            {
                os = RuntimeInformation.OSDescription,
                processorCount = Environment.ProcessorCount,
            },
            game = new
            {
                serverId = context.ServerId,
                serverName = context.ServerName,
                serverAddress = context.ServerAddress,
                minecraftVersion = context.MinecraftVersion,
                loader = context.LoaderName,
                loaderVersion = context.LoaderVersion,
                ramMb = context.RamMb,
                autoConnect = context.AutoConnect,
                gameDirectoryExists = NormalizeDirectory(context.GameDir) is not null,
            },
            diagnostics = new
            {
                managedPackInventory = true,
                quarantineInventory = true,
                packStateIncludedWhenPresent = true,
            },
            security = new
            {
                tokenStoreIncluded = false,
                legendCoreSessionIncluded = false,
                legacyAuthFilesIncluded = false,
                machineNameIncluded = false,
                copiedTextLogsRedacted = true,
            }
        };

        var json = JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(
            Path.Combine(workDir, "metadata.json"),
            json,
            Utf8NoBom,
            ct).ConfigureAwait(false);
    }

    private static void WriteContentsIndex(string workDir, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            var root = Path.GetFullPath(workDir);
            var lines = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Where(path => !Path.GetFileName(path).Equals("CONTENTS.txt", StringComparison.OrdinalIgnoreCase))
                .Select(path => new FileInfo(path))
                .OrderBy(file => file.FullName, StringComparer.OrdinalIgnoreCase)
                .Select(file => $"{file.Length,10}  {Path.GetRelativePath(root, file.FullName).Replace('\\', '/')}")
                .ToArray();

            File.WriteAllLines(Path.Combine(root, "CONTENTS.txt"), lines, Utf8NoBom);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // The index is convenience metadata only; diagnostics still remain useful without it.
        }
    }

    private static void WriteFileInventory(
        string gameDir,
        IReadOnlyCollection<string> relativeRoots,
        string destinationPath,
        int maxFiles,
        CancellationToken ct)
    {
        try
        {
            var normalizedGame = NormalizeDirectory(gameDir);
            if (normalizedGame is null) return;

            var lines = new List<string>();
            var truncated = false;

            foreach (var relativeRoot in relativeRoots)
            {
                ct.ThrowIfCancellationRequested();

                var normalizedRelRoot = (relativeRoot ?? string.Empty)
                    .Trim()
                    .Replace('\\', '/')
                    .Trim('/');
                if (normalizedRelRoot.Length == 0 || normalizedRelRoot.Split('/').Any(static x => x is "." or ".."))
                    continue;

                var rootPath = Path.GetFullPath(Path.Combine(
                    normalizedGame,
                    normalizedRelRoot.Replace('/', Path.DirectorySeparatorChar)));
                var gamePrefix = normalizedGame.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                                 + Path.DirectorySeparatorChar;
                if (!rootPath.StartsWith(gamePrefix, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(rootPath))
                    continue;

                foreach (var path in Directory.EnumerateFiles(rootPath, "*", SearchOption.AllDirectories)
                             .OrderBy(static x => x, StringComparer.OrdinalIgnoreCase))
                {
                    ct.ThrowIfCancellationRequested();

                    if (lines.Count >= Math.Max(0, maxFiles))
                    {
                        truncated = true;
                        break;
                    }

                    try
                    {
                        var info = new FileInfo(path);
                        var rel = Path.GetRelativePath(normalizedGame, info.FullName).Replace('\\', '/');
                        lines.Add($"{info.Length,12}  {info.LastWriteTimeUtc:O}  {rel}");
                    }
                    catch
                    {
                        // A single disappearing/locked file should not abort the report.
                    }
                }

                if (truncated) break;
            }

            if (lines.Count == 0 && !truncated)
                lines.Add("<empty>");
            if (truncated)
                lines.Add($"<truncated after {maxFiles} files>");

            var parent = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrWhiteSpace(parent))
                Directory.CreateDirectory(parent);

            File.WriteAllLines(destinationPath, lines, Utf8NoBom);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Inventory is supplementary diagnostics only.
        }
    }

    private static HashSet<string> LoadEphemeralSecrets(string gameDir)
    {
        var secrets = new HashSet<string>(StringComparer.Ordinal);

        try
        {
            var normalized = NormalizeDirectory(gameDir);
            if (normalized is null) return secrets;

            var sessionPath = Path.Combine(normalized, ".legendcore", "session.json");
            if (!File.Exists(sessionPath)) return secrets;

            var info = new FileInfo(sessionPath);
            if (info.Length <= 0 || info.Length > 64 * 1024) return secrets;

            using var doc = JsonDocument.Parse(File.ReadAllText(sessionPath, Encoding.UTF8));
            AddJsonSecret(doc.RootElement, "ticket", secrets);
            AddJsonSecret(doc.RootElement, "accessToken", secrets);
            AddJsonSecret(doc.RootElement, "refreshToken", secrets);
        }
        catch
        {
            // Session content is never copied; failure to read it only reduces targeted redaction.
        }

        return secrets;
    }

    private static void AddJsonSecret(JsonElement root, string property, HashSet<string> secrets)
    {
        if (!root.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String)
            return;

        var secret = (value.GetString() ?? string.Empty).Trim();
        if (secret.Length >= 8)
            secrets.Add(secret);
    }

    private static void CopyNewestTextFiles(
        string sourceDir,
        string destinationDir,
        int maxFiles,
        HashSet<string> secrets,
        CancellationToken ct,
        IReadOnlyCollection<string> allowedExtensions)
    {
        try
        {
            if (!Directory.Exists(sourceDir)) return;

            var files = Directory.EnumerateFiles(sourceDir, "*", SearchOption.TopDirectoryOnly)
                .Select(path => new FileInfo(path))
                .Where(file =>
                    file.Exists &&
                    file.Length >= 0 &&
                    file.Length <= MaxSourceFileBytes &&
                    allowedExtensions.Contains(file.Extension, StringComparer.OrdinalIgnoreCase))
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .Take(Math.Max(0, maxFiles))
                .ToArray();

            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();
                CopySpecificTextFile(
                    file.FullName,
                    Path.Combine(destinationDir, SanitizeFileName(file.Name)),
                    secrets,
                    ct);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Diagnostics are best-effort. A locked crash/log file must not abort the whole bundle.
        }
    }

    private static void CopySpecificTextFile(
        string sourcePath,
        string destinationPath,
        HashSet<string> secrets,
        CancellationToken ct)
    {
        try
        {
            if (!File.Exists(sourcePath)) return;

            var info = new FileInfo(sourcePath);
            if (info.Length < 0 || info.Length > MaxSourceFileBytes) return;

            ct.ThrowIfCancellationRequested();

            var text = File.ReadAllText(sourcePath, Encoding.UTF8);
            var redacted = Redact(text, secrets);

            var parent = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrWhiteSpace(parent))
                Directory.CreateDirectory(parent);

            File.WriteAllText(destinationPath, redacted, Utf8NoBom);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
        }
    }

    private static string Redact(string text, HashSet<string> secrets)
    {
        if (string.IsNullOrEmpty(text)) return text;

        var result = text;
        foreach (var secret in secrets.OrderByDescending(x => x.Length))
        {
            if (secret.Length >= 8)
                result = result.Replace(secret, "<REDACTED_SECRET>", StringComparison.Ordinal);
        }

        result = BearerRegex().Replace(result, "$1<REDACTED>");
        result = JsonSecretRegex().Replace(result, "$1<REDACTED>$2");
        result = NamedSecretRegex().Replace(result, "$1<REDACTED>");
        result = HexTokenRegex().Replace(result, "<REDACTED_64HEX>");
        return result;
    }

    private static string? NormalizeDirectory(string? path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            var full = Path.GetFullPath(path.Trim().Trim('"'));
            return Directory.Exists(full) ? full : null;
        }
        catch
        {
            return null;
        }
    }

    private static string SanitizeFileName(string fileName)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = fileName.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray();
        return new string(chars);
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
        }
    }
}
