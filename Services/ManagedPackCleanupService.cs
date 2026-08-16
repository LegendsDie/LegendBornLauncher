using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace LegendBorn.Services;

/// <summary>
/// Reconciles pack-owned roots before the normal MinecraftService sync runs.
/// Only mods/, kubejs/ and scripts/ are destructive-managed here. Config/defaultconfigs/
/// resourcepacks/shaderpacks remain user-owned or seed-only and are never removed by this service.
///
/// Stale managed files are moved into .trash instead of being deleted immediately. This keeps
/// upgrades deterministic while preserving a recovery path if a pack publication was wrong.
/// If a stale file is locked and cannot be moved out of a managed root, launch fails closed instead
/// of starting a mixture of two pack revisions.
/// </summary>
public static class ManagedPackCleanupService
{
    private static readonly string[] ManagedRoots = { "mods/", "kubejs/", "scripts/" };
    private static readonly TimeSpan ManifestTimeout = TimeSpan.FromSeconds(8);
    private const long MaxManifestBytes = 8L * 1024 * 1024;
    private const int MoveAttempts = 6;

    private static readonly HttpClient Http = CreateHttp();

    private sealed class ManifestDto
    {
        [JsonPropertyName("files")]
        public List<FileDto>? Files { get; set; }
    }

    private sealed class FileDto
    {
        [JsonPropertyName("path")]
        public string? Path { get; set; }
    }

    public sealed record CleanupResult(int RemovedFiles, int RemovedDirectories, int WantedManagedFiles);

    public static async Task<CleanupResult> ReconcileAsync(
        string gameDir,
        IEnumerable<string> mirrors,
        Action<string>? log = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(gameDir))
            throw new ArgumentException("gameDir is empty", nameof(gameDir));

        var normalizedMirrors = mirrors
            .Select(NormalizeHttpsBase)
            .Where(static x => x.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (normalizedMirrors.Length == 0)
            throw new InvalidOperationException("Нет HTTPS-зеркала для проверки файлов сборки.");

        var wanted = await LoadManagedPathsAsync(normalizedMirrors, ct).ConfigureAwait(false);
        var result = await ReconcileLocalManagedRootsAsync(gameDir, wanted, ct).ConfigureAwait(false);

        if (result.RemovedFiles > 0 || result.RemovedDirectories > 0)
        {
            log?.Invoke(
                $"Сборка: устаревшие файлы перенесены в .trash: {result.RemovedFiles}; " +
                $"пустые папки убраны: {result.RemovedDirectories}.");
        }
        else
        {
            log?.Invoke("Сборка: лишних файлов прошлой версии не найдено.");
        }

        return result;
    }

    internal static async Task<CleanupResult> ReconcileLocalManagedRootsAsync(
        string gameDir,
        IReadOnlySet<string> wantedManaged,
        CancellationToken ct = default)
    {
        var gameFull = Path.GetFullPath(gameDir)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

        var removedFiles = 0;
        var removedDirs = 0;
        var failures = new List<string>();
        string? quarantineRoot = null;

        foreach (var root in ManagedRoots)
        {
            ct.ThrowIfCancellationRequested();

            var rootPath = Path.GetFullPath(Path.Combine(gameDir, root.TrimEnd('/')));
            if (!IsUnder(rootPath, gameFull) || !Directory.Exists(rootPath))
                continue;

            if ((File.GetAttributes(rootPath) & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"Папка сборки является reparse point: {root}");

            string[] files;
            try
            {
                files = Directory.EnumerateFiles(rootPath, "*", SearchOption.AllDirectories).ToArray();
            }
            catch (Exception ex)
            {
                throw new IOException($"Не удалось просканировать папку сборки {root}: {ex.Message}", ex);
            }

            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();

                var full = Path.GetFullPath(file);
                if (!IsUnder(full, gameFull))
                {
                    failures.Add(file);
                    continue;
                }

                var rel = NormalizeRel(Path.GetRelativePath(gameDir, full));
                if (rel.Length == 0 || ShouldKeep(rel, wantedManaged))
                    continue;

                quarantineRoot ??= CreateQuarantineRoot(gameDir);

                if (await TryMoveToTrashAsync(full, rel, quarantineRoot, ct).ConfigureAwait(false))
                    removedFiles++;
                else
                    failures.Add(rel);
            }

            try
            {
                var dirs = Directory.EnumerateDirectories(rootPath, "*", SearchOption.AllDirectories)
                    .OrderByDescending(static p => p.Length)
                    .ToArray();

                foreach (var dir in dirs)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        var info = new DirectoryInfo(dir);
                        if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                            continue;

                        if (!Directory.EnumerateFileSystemEntries(dir).Any())
                        {
                            Directory.Delete(dir, recursive: false);
                            removedDirs++;
                        }
                    }
                    catch
                    {
                        // Empty-directory cleanup is cosmetic. Remaining stale files are verified below.
                    }
                }
            }
            catch
            {
                // Remaining stale files are verified below.
            }
        }

        // Verify after quarantine. Silent stale managed content is not allowed.
        foreach (var root in ManagedRoots)
        {
            var rootPath = Path.Combine(gameDir, root.TrimEnd('/'));
            if (!Directory.Exists(rootPath))
                continue;

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(rootPath, "*", SearchOption.AllDirectories);
            }
            catch (Exception ex)
            {
                throw new IOException($"Не удалось проверить папку сборки {root}: {ex.Message}", ex);
            }

            foreach (var file in files)
            {
                var rel = NormalizeRel(Path.GetRelativePath(gameDir, file));
                if (rel.Length > 0 && !ShouldKeep(rel, wantedManaged))
                    failures.Add(rel);
            }
        }

        var unresolved = failures
            .Where(static x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(12)
            .ToArray();

        if (unresolved.Length > 0)
        {
            var sample = string.Join(", ", unresolved.Take(4));
            throw new IOException(
                $"Не удалось убрать устаревшие файлы сборки ({unresolved.Length}+): {sample}. " +
                "Закрой Minecraft/Java и повтори проверку. Смешанная версия сборки не будет запущена.");
        }

        return new CleanupResult(removedFiles, removedDirs, wantedManaged.Count);
    }

    private static string CreateQuarantineRoot(string gameDir)
    {
        var runName = $"{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}";
        return Path.Combine(gameDir, ".trash", "pack-cleanup", runName);
    }

    private static async Task<bool> TryMoveToTrashAsync(
        string sourcePath,
        string relativePath,
        string quarantineRoot,
        CancellationToken ct)
    {
        var quarantineFull = Path.GetFullPath(quarantineRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var destination = Path.GetFullPath(Path.Combine(
            quarantineRoot,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));

        if (!IsUnder(destination, quarantineFull))
            return false;

        for (var attempt = 0; attempt < MoveAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                if (!File.Exists(sourcePath))
                    return true;

                var attrs = File.GetAttributes(sourcePath);
                if ((attrs & FileAttributes.ReadOnly) != 0)
                    File.SetAttributes(sourcePath, attrs & ~FileAttributes.ReadOnly);

                var parent = Path.GetDirectoryName(destination);
                if (!string.IsNullOrWhiteSpace(parent))
                    Directory.CreateDirectory(parent);

                if (File.Exists(destination))
                    File.Delete(destination);

                File.Move(sourcePath, destination);
                if (!File.Exists(sourcePath) && File.Exists(destination))
                    return true;
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }

            await Task.Delay(100 + (attempt * 125), ct).ConfigureAwait(false);
        }

        return !File.Exists(sourcePath);
    }

    private static bool ShouldKeep(string rel, IReadOnlySet<string> wantedManaged)
    {
        if (wantedManaged.Contains(rel))
            return true;

        const string pending = ".pending";
        const string pendingMeta = ".pending.sha256";

        if (rel.EndsWith(pendingMeta, StringComparison.OrdinalIgnoreCase))
        {
            var baseRel = rel[..^pendingMeta.Length];
            return wantedManaged.Contains(baseRel);
        }

        if (rel.EndsWith(pending, StringComparison.OrdinalIgnoreCase))
        {
            var baseRel = rel[..^pending.Length];
            return wantedManaged.Contains(baseRel);
        }

        return false;
    }

    private static async Task<HashSet<string>> LoadManagedPathsAsync(string[] mirrors, CancellationToken ct)
    {
        Exception? last = null;

        foreach (var mirror in mirrors)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var manifest = await DownloadManifestAsync(mirror, ct).ConfigureAwait(false);
                if (manifest.Files is null || manifest.Files.Count == 0)
                    throw new InvalidDataException("manifest.files пуст");

                var validPaths = 0;
                var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var file in manifest.Files)
                {
                    var rel = NormalizeRel(file.Path);
                    if (!IsSafeRelativeFile(rel))
                        continue;

                    validPaths++;
                    if (IsManaged(rel))
                        wanted.Add(rel);
                }

                if (validPaths == 0)
                    throw new InvalidDataException("manifest не содержит валидных путей");

                return wanted;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                last = ex;
            }
        }

        throw new InvalidOperationException("Не удалось получить manifest для очистки файлов сборки.", last);
    }

    private static async Task<ManifestDto> DownloadManifestAsync(string mirror, CancellationToken ct)
    {
        var baseUri = new Uri(mirror, UriKind.Absolute);
        var uri = new Uri(baseUri, "manifest.json");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ManifestTimeout);

        using var request = new HttpRequestMessage(HttpMethod.Get, uri)
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };

        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"manifest HTTP {(int)response.StatusCode}");

        if (response.RequestMessage?.RequestUri is { Scheme: not "https" })
            throw new HttpRequestException("manifest redirect не HTTPS");

        if (response.Content.Headers.ContentLength is long declared && declared > MaxManifestBytes)
            throw new InvalidDataException("manifest слишком большой");

        var bytes = await ReadLimitedAsync(response.Content, timeout.Token).ConfigureAwait(false);
        return JsonSerializer.Deserialize<ManifestDto>(bytes, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            AllowTrailingCommas = true,
            ReadCommentHandling = JsonCommentHandling.Skip
        }) ?? throw new InvalidDataException("manifest JSON пуст");
    }

    private static async Task<byte[]> ReadLimitedAsync(HttpContent content, CancellationToken ct)
    {
        await using var input = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var output = new MemoryStream(64 * 1024);
        var buffer = ArrayPool<byte>.Shared.Rent(32 * 1024);
        long total = 0;

        try
        {
            while (true)
            {
                var read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false);
                if (read == 0) break;

                total += read;
                if (total > MaxManifestBytes)
                    throw new InvalidDataException("manifest превышает лимит размера");

                output.Write(buffer, 0, read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: false);
        }

        return output.ToArray();
    }

    private static bool IsManaged(string rel)
        => ManagedRoots.Any(root => rel.StartsWith(root, StringComparison.OrdinalIgnoreCase));

    private static bool IsSafeRelativeFile(string rel)
    {
        if (rel.Length == 0 || rel.EndsWith('/')) return false;
        if (rel.StartsWith('/') || Path.IsPathRooted(rel)) return false;
        if (rel.Contains(':')) return false;

        return !rel.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Any(static part => part is "." or "..");
    }

    private static string NormalizeRel(string? value)
        => (value ?? "").Trim().Replace('\\', '/').TrimStart('/');

    private static string NormalizeHttpsBase(string? value)
    {
        var text = (value ?? "").Trim();
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            return "";

        var builder = new UriBuilder(uri) { Query = "", Fragment = "" };
        if (!builder.Path.EndsWith('/')) builder.Path += "/";
        return builder.Uri.AbsoluteUri;
    }

    private static bool IsUnder(string path, string rootWithSeparator)
    {
        var full = Path.GetFullPath(path);
        return full.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(
                   full.TrimEnd(Path.DirectorySeparatorChar),
                   rootWithSeparator.TrimEnd(Path.DirectorySeparatorChar),
                   StringComparison.OrdinalIgnoreCase);
    }

    private static HttpClient CreateHttp()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            Proxy = WebRequest.DefaultWebProxy,
            UseProxy = true,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            MaxConnectionsPerServer = 4,
            AllowAutoRedirect = true
        };

        return new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan,
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower
        };
    }
}
