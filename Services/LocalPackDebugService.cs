#if DEBUG
using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace LegendBorn.Services;

/// <summary>
/// Debug-only one-mod smoke path. It never reads or writes the production live manifest.
/// A canonical manifest downloaded from the admin site points at an already verified immutable blob;
/// this service installs that blob into an isolated game directory before the normal Minecraft prepare.
/// </summary>
internal static class LocalPackDebugService
{
    internal const string ManifestPathEnvironmentVariable = "LEGENDBORN_DEV_PACK_MANIFEST_PATH";
    internal const string GameDirEnvironmentVariable = "LEGENDBORN_DEV_GAME_DIR";

    private const long MaxTestModBytes = 1024L * 1024 * 1024;
    private static readonly HttpClient Http = CreateHttp();

    internal static bool IsEnabled =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ManifestPathEnvironmentVariable));

    internal static string? ResolveGameDirOverride()
    {
        if (!IsEnabled)
            return null;

        var safeDefault = Path.Combine(LauncherPaths.LocalDir, "dev-pack-test");
        var requested = Environment.GetEnvironmentVariable(GameDirEnvironmentVariable);
        var resolved = LauncherPaths.NormalizePathOr(requested, safeDefault);

        try
        {
            var normal = Path.GetFullPath(LauncherPaths.DefaultGameDir)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var candidate = Path.GetFullPath(resolved)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            if (candidate.Equals(normal, StringComparison.OrdinalIgnoreCase))
                return safeDefault;
        }
        catch
        {
            return safeDefault;
        }

        return resolved;
    }

    internal static async Task ApplyAsync(
        string gameDir,
        IReadOnlyList<string> mirrors,
        Action<string>? log,
        CancellationToken ct)
    {
        var rawManifestPath = Environment.GetEnvironmentVariable(ManifestPathEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(rawManifestPath))
            return;

        var manifestPath = Path.GetFullPath(rawManifestPath.Trim().Trim('"'));
        if (!File.Exists(manifestPath))
            throw new FileNotFoundException("Debug pack manifest not found.", manifestPath);

        var json = await File.ReadAllTextAsync(manifestPath, ct).ConfigureAwait(false);
        var manifest = JsonSerializer.Deserialize<LocalManifest>(json, JsonOptions)
            ?? throw new InvalidOperationException("Debug pack manifest is empty or invalid.");

        if (manifest.Files is null || manifest.Files.Count != 1)
            throw new InvalidOperationException("Local pack smoke-test requires exactly one manifest file.");

        var file = manifest.Files[0];
        var rel = NormalizeRelPath(file.Path);
        if (!IsSingleModPath(rel))
            throw new InvalidOperationException("Local pack smoke-test accepts only mods/<name>.jar.");
        if (!IsSha256(file.Sha256))
            throw new InvalidOperationException("Local pack smoke-test manifest contains an invalid SHA-256.");
        if (file.Size <= 0 || file.Size > MaxTestModBytes)
            throw new InvalidOperationException("Local pack smoke-test mod size is outside the allowed range.");

        var blobRel = NormalizeRelPath(file.Blob);
        if (string.IsNullOrWhiteSpace(blobRel))
        {
            var sha = file.Sha256.Trim().ToLowerInvariant();
            blobRel = $"blobs/{sha[..2]}/{sha}";
        }
        if (!blobRel.StartsWith("blobs/", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Local pack smoke-test manifest contains an invalid blob path.");

        var normalizedMirrors = mirrors
            .Select(NormalizeHttpsBaseUrl)
            .Where(static x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (normalizedMirrors.Length == 0)
            throw new InvalidOperationException("Local pack smoke-test has no https pack mirror.");

        var gameRoot = Path.GetFullPath(gameDir);
        var modsDir = Path.Combine(gameRoot, "mods");
        Directory.CreateDirectory(modsDir);

        var destination = Path.GetFullPath(Path.Combine(gameRoot, rel.Replace('/', Path.DirectorySeparatorChar)));
        var rootPrefix = gameRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!destination.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Local pack smoke-test destination escaped the game directory.");

        Exception? last = null;
        foreach (var mirror in normalizedMirrors)
        {
            ct.ThrowIfCancellationRequested();
            var url = new Uri(new Uri(mirror), blobRel).ToString();
            try
            {
                log?.Invoke($"DEV pack: download {rel} <- {mirror}");
                await DownloadVerifiedAsync(url, destination, file.Size, file.Sha256, ct).ConfigureAwait(false);
                PruneOtherMods(modsDir, destination, log);
                log?.Invoke($"DEV pack: local manifest applied: {manifestPath}");
                log?.Invoke($"DEV pack: isolated game dir: {gameRoot}");
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                last = ex;
                log?.Invoke($"DEV pack: mirror failed ({mirror}) — {ex.Message}");
            }
        }

        throw new InvalidOperationException("Local pack smoke-test could not download the verified mod blob.", last);
    }

    private static async Task DownloadVerifiedAsync(
        string url,
        string destination,
        long expectedSize,
        string expectedSha256,
        CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(destination);
        if (string.IsNullOrWhiteSpace(dir))
            throw new InvalidOperationException("Invalid local mod destination.");
        Directory.CreateDirectory(dir);

        var tmp = destination + ".devtmp";
        TryDelete(tmp);

        try
        {
            using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");

            if (response.Content.Headers.ContentLength is long contentLength && contentLength != expectedSize)
                throw new InvalidOperationException($"Size mismatch in headers: expected {expectedSize}, got {contentLength}.");

            await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var output = new FileStream(
                tmp,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

            var buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
            long total = 0;
            try
            {
                while (true)
                {
                    var read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false);
                    if (read == 0) break;
                    total += read;
                    if (total > expectedSize)
                        throw new InvalidOperationException("Downloaded mod exceeded manifest size.");
                    sha.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer, clearArray: false);
            }

            await output.FlushAsync(ct).ConfigureAwait(false);
            if (total != expectedSize)
                throw new InvalidOperationException($"Size mismatch: expected {expectedSize}, got {total}.");

            var actual = Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
            if (!actual.Equals(expectedSha256.Trim(), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("SHA-256 mismatch for local test mod.");

            File.Move(tmp, destination, overwrite: true);
        }
        catch
        {
            TryDelete(tmp);
            throw;
        }
    }

    private static void PruneOtherMods(string modsDir, string wantedFile, Action<string>? log)
    {
        var wanted = Path.GetFullPath(wantedFile);
        foreach (var path in Directory.EnumerateFiles(modsDir, "*", SearchOption.AllDirectories))
        {
            var full = Path.GetFullPath(path);
            if (full.Equals(wanted, StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                File.Delete(full);
                log?.Invoke($"DEV pack: removed stale local-test mod {Path.GetFileName(full)}");
            }
            catch
            {
            }
        }
    }

    private static string NormalizeRelPath(string? value)
    {
        var text = (value ?? string.Empty).Trim().Replace('\\', '/').TrimStart('/');
        if (string.IsNullOrWhiteSpace(text) || text.Contains(':') || text.StartsWith('~')) return string.Empty;
        var parts = text.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts.Any(static part => part is "." or "..")) return string.Empty;
        return string.Join('/', parts);
    }

    private static bool IsSingleModPath(string path)
    {
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 2 &&
               parts[0].Equals("mods", StringComparison.OrdinalIgnoreCase) &&
               parts[1].EndsWith(".jar", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSha256(string? value)
    {
        var text = (value ?? string.Empty).Trim();
        return text.Length == 64 && text.All(static c =>
            c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');
    }

    private static string NormalizeHttpsBaseUrl(string? value)
    {
        if (!Uri.TryCreate((value ?? string.Empty).Trim(), UriKind.Absolute, out var uri) ||
            !uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
            return string.Empty;
        var builder = new UriBuilder(uri) { Query = string.Empty, Fragment = string.Empty };
        if (!builder.Path.EndsWith('/')) builder.Path += "/";
        return builder.Uri.ToString();
    }

    private static HttpClient CreateHttp()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            Proxy = WebRequest.DefaultWebProxy,
            UseProxy = true,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        };
        return new HttpClient(new MinecraftDistributionHttpHandler(handler))
        {
            Timeout = TimeSpan.FromMinutes(10)
        };
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    private sealed class LocalManifest
    {
        [JsonPropertyName("files")]
        public List<LocalFile> Files { get; set; } = new();
    }

    private sealed class LocalFile
    {
        [JsonPropertyName("path")]
        public string Path { get; set; } = string.Empty;

        [JsonPropertyName("sha256")]
        public string Sha256 { get; set; } = string.Empty;

        [JsonPropertyName("size")]
        public long Size { get; set; }

        [JsonPropertyName("blob")]
        public string? Blob { get; set; }
    }
}
#endif
