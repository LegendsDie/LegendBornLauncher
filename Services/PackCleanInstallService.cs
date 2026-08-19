// File: Services/PackCleanInstallService.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace LegendBorn.Services;

/// <summary>
/// Implements the per-build clean-install contract published by legendbornweb.
/// A build marked cleanInstall=true is applied exactly once per local game directory.
/// The clean marker intentionally lives outside the game directory so it survives the reset itself.
/// Clean install resets pack-owned/user-mutable game content but never deletes the shared Minecraft runtime
/// directories used by CmlLib to resolve vanilla/NeoForge versions.
/// </summary>
public static class PackCleanInstallService
{
    private const int MaxManifestBytes = 5 * 1024 * 1024;
    private const int ManifestTimeoutSeconds = 18;
    private const int MarkerVersion = 1;

    private static readonly HttpClient Http = CreateHttp();

    private static readonly HashSet<string> PreservedDirectorySet = new(
        new[]
        {
            "resourcepacks",
            "shaderpacks",
            "screenshots",
            "saves",
            "logs"
        },
        StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> MinecraftRuntimeDirectorySet = new(
        new[]
        {
            "assets",
            "libraries",
            "versions"
        },
        StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<string> PreservedDirectoryNames { get; } =
        new[]
        {
            "resourcepacks",
            "shaderpacks",
            "screenshots",
            "saves",
            "logs"
        };

    public static IReadOnlyList<string> MinecraftRuntimeDirectoryNames { get; } =
        new[]
        {
            "assets",
            "libraries",
            "versions"
        };

    public sealed record ManifestSnapshot(
        bool CleanInstall,
        string ManifestSha256,
        string PackId,
        int? Build,
        string Version,
        string SourceBaseUrl)
    {
        public string DisplayIdentity
        {
            get
            {
                var version = (Version ?? string.Empty).Trim();
                if (Build is > 0 && version.Length > 0)
                    return $"{version}+{Build.Value}";
                if (Build is > 0)
                    return $"build {Build.Value}";
                return version.Length > 0 ? version : ManifestSha256[..Math.Min(12, ManifestSha256.Length)];
            }
        }
    }

    private sealed class AppliedCleanInstallState
    {
        public int Version { get; set; } = MarkerVersion;
        public string ManifestSha256 { get; set; } = string.Empty;
        public string PackId { get; set; } = string.Empty;
        public int? Build { get; set; }
        public DateTimeOffset AppliedAtUtc { get; set; }
    }

    private static readonly JsonSerializerOptions StateJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static async Task<ManifestSnapshot> InspectAsync(
        string[] mirrors,
        CancellationToken ct)
    {
        if (mirrors is null || mirrors.Length == 0)
            throw new InvalidOperationException("Clean install: список pack-зеркал пуст.");

        var normalized = mirrors
            .Select(NormalizeHttpsBaseUrl)
            .Where(static value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (normalized.Length == 0)
            throw new InvalidOperationException("Clean install: нет допустимых HTTPS pack-зеркал.");

        var failures = new List<string>();

        foreach (var baseUrl in normalized)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(ManifestTimeoutSeconds));

                var manifestUrl = new Uri(new Uri(baseUrl), "manifest.json");
                using var request = new HttpRequestMessage(HttpMethod.Get, manifestUrl);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                request.Headers.CacheControl = new CacheControlHeaderValue
                {
                    NoCache = true,
                    NoStore = true,
                    MaxAge = TimeSpan.Zero
                };

                using var response = await Http.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    timeoutCts.Token).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    failures.Add($"{baseUrl}: HTTP {(int)response.StatusCode}");
                    continue;
                }

                var mediaType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
                if (mediaType.Contains("text/html", StringComparison.OrdinalIgnoreCase))
                {
                    failures.Add($"{baseUrl}: HTML вместо manifest.json");
                    continue;
                }

                var bytes = await ReadBoundedAsync(
                    response.Content,
                    MaxManifestBytes,
                    timeoutCts.Token).ConfigureAwait(false);

                if (bytes.Length == 0)
                {
                    failures.Add($"{baseUrl}: пустой manifest.json");
                    continue;
                }

                var sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                using var document = JsonDocument.Parse(
                    bytes,
                    new JsonDocumentOptions
                    {
                        AllowTrailingCommas = true,
                        CommentHandling = JsonCommentHandling.Skip
                    });

                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    failures.Add($"{baseUrl}: корень manifest не является объектом");
                    continue;
                }

                var root = document.RootElement;
                var cleanInstall =
                    root.TryGetProperty("cleanInstall", out var cleanElement) &&
                    cleanElement.ValueKind == JsonValueKind.True;

                var packId = GetString(root, "packId");
                var version = GetString(root, "packVersion");
                if (version.Length == 0)
                    version = GetString(root, "version");

                int? build = null;
                if (root.TryGetProperty("build", out var buildElement) &&
                    buildElement.ValueKind == JsonValueKind.Number &&
                    buildElement.TryGetInt32(out var parsedBuild) &&
                    parsedBuild > 0)
                {
                    build = parsedBuild;
                }

                return new ManifestSnapshot(
                    CleanInstall: cleanInstall,
                    ManifestSha256: sha256,
                    PackId: packId,
                    Build: build,
                    Version: version,
                    SourceBaseUrl: baseUrl);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                failures.Add($"{baseUrl}: таймаут manifest.json");
            }
            catch (HttpRequestException ex)
            {
                failures.Add($"{baseUrl}: {ex.Message}");
            }
            catch (JsonException ex)
            {
                failures.Add($"{baseUrl}: некорректный JSON ({ex.Message})");
            }
            catch (IOException ex)
            {
                failures.Add($"{baseUrl}: {ex.Message}");
            }
        }

        ct.ThrowIfCancellationRequested();

        var detail = failures.Count == 0
            ? "неизвестная ошибка"
            : string.Join(" | ", failures.Take(4));

        throw new InvalidOperationException(
            $"Clean install: не удалось получить актуальный manifest ни с одного зеркала: {detail}");
    }

    public static bool IsApplied(string gameDir, ManifestSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (!snapshot.CleanInstall || string.IsNullOrWhiteSpace(snapshot.ManifestSha256))
            return false;

        try
        {
            var markerPath = GetMarkerPath(gameDir);
            if (!File.Exists(markerPath))
                return false;

            var json = File.ReadAllText(markerPath, Encoding.UTF8);
            var state = JsonSerializer.Deserialize<AppliedCleanInstallState>(json, StateJsonOptions);
            return state is not null &&
                   state.Version == MarkerVersion &&
                   string.Equals(
                       state.ManifestSha256,
                       snapshot.ManifestSha256,
                       StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public static Task CleanInstanceAsync(
        string gameDir,
        ManifestSnapshot snapshot,
        Action<string>? log,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (!snapshot.CleanInstall)
            return Task.CompletedTask;

        return CleanGameDirectoryAsync(gameDir, log, ct);
    }

    /// <summary>
    /// Testable filesystem primitive. It preserves the five user-owned top-level directories from
    /// the public clean-install contract plus Minecraft runtime infrastructure required by CmlLib.
    /// Everything else is treated as stale instance/pack state and removed before the current build
    /// is synchronized.
    /// </summary>
    internal static Task CleanGameDirectoryAsync(
        string gameDir,
        Action<string>? log,
        CancellationToken ct)
    {
        var root = NormalizeGameDir(gameDir);

        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            Directory.CreateDirectory(root);

            log?.Invoke(
                "Чистая установка: сохраняю resourcepacks/, shaderpacks/, screenshots/, saves/, logs/ и Minecraft runtime assets/, libraries/, versions/.");

            foreach (var entry in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.TopDirectoryOnly))
            {
                ct.ThrowIfCancellationRequested();

                var name = Path.GetFileName(entry);
                FileAttributes attributes;
                try
                {
                    attributes = File.GetAttributes(entry);
                }
                catch (Exception ex)
                {
                    throw new IOException(
                        $"Чистая установка: не удалось прочитать атрибуты '{name}'.",
                        ex);
                }

                var isDirectory = (attributes & FileAttributes.Directory) != 0;
                if (isDirectory && PreservedDirectorySet.Contains(name))
                {
                    log?.Invoke($"Чистая установка: сохранено пользовательское {name}/");
                    continue;
                }

                if (isDirectory && MinecraftRuntimeDirectorySet.Contains(name))
                {
                    log?.Invoke($"Чистая установка: сохранена инфраструктура Minecraft {name}/");
                    continue;
                }

                try
                {
                    DeleteEntry(entry, attributes, ct);
                    log?.Invoke($"Чистая установка: удалено {name}{(isDirectory ? "/" : string.Empty)}");
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    throw new IOException(
                        $"Чистая установка остановлена: не удалось удалить '{name}'. Закрой программы, использующие файлы сборки, и повтори запуск.",
                        ex);
                }
            }

            ct.ThrowIfCancellationRequested();
        }, ct);
    }

    public static void MarkApplied(string gameDir, ManifestSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (!snapshot.CleanInstall || string.IsNullOrWhiteSpace(snapshot.ManifestSha256))
            return;

        var markerPath = GetMarkerPath(gameDir);
        var directory = Path.GetDirectoryName(markerPath)
            ?? throw new InvalidOperationException("Clean install marker directory is unavailable.");
        Directory.CreateDirectory(directory);

        var state = new AppliedCleanInstallState
        {
            Version = MarkerVersion,
            ManifestSha256 = snapshot.ManifestSha256.Trim().ToLowerInvariant(),
            PackId = (snapshot.PackId ?? string.Empty).Trim(),
            Build = snapshot.Build,
            AppliedAtUtc = DateTimeOffset.UtcNow
        };

        var json = JsonSerializer.Serialize(state, StateJsonOptions);
        var temp = markerPath + ".tmp";
        File.WriteAllText(temp, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(temp, markerPath, overwrite: true);
    }

    private static void DeleteEntry(string path, FileAttributes attributes, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var isDirectory = (attributes & FileAttributes.Directory) != 0;
        var isReparsePoint = (attributes & FileAttributes.ReparsePoint) != 0;

        if (!isDirectory)
        {
            ClearReadOnly(path, attributes);
            File.Delete(path);
            return;
        }

        // Never recurse through junctions/symlinks. Delete only the link itself.
        if (isReparsePoint)
        {
            ClearReadOnly(path, attributes);
            Directory.Delete(path, recursive: false);
            return;
        }

        foreach (var child in Directory.EnumerateFileSystemEntries(path, "*", SearchOption.TopDirectoryOnly))
        {
            ct.ThrowIfCancellationRequested();
            var childAttributes = File.GetAttributes(child);
            DeleteEntry(child, childAttributes, ct);
        }

        ct.ThrowIfCancellationRequested();
        ClearReadOnly(path, File.GetAttributes(path));
        Directory.Delete(path, recursive: false);
    }

    private static void ClearReadOnly(string path, FileAttributes attributes)
    {
        if ((attributes & FileAttributes.ReadOnly) == 0)
            return;

        File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
    }

    private static string GetMarkerPath(string gameDir)
    {
        var root = NormalizeGameDir(gameDir);
        var identity = root.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        if (OperatingSystem.IsWindows())
            identity = identity.ToUpperInvariant();

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))
            .ToLowerInvariant();

        return Path.Combine(
            LauncherPaths.LocalDir,
            "clean-install",
            $"{hash}.json");
    }

    private static string NormalizeGameDir(string gameDir)
    {
        if (string.IsNullOrWhiteSpace(gameDir))
            throw new ArgumentException("Game directory is empty.", nameof(gameDir));

        return Path.GetFullPath(gameDir)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static string NormalizeHttpsBaseUrl(string? value)
    {
        var text = (value ?? string.Empty).Trim();
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        var builder = new UriBuilder(uri)
        {
            Query = string.Empty,
            Fragment = string.Empty
        };

        if (!builder.Path.EndsWith("/", StringComparison.Ordinal))
            builder.Path += "/";

        return builder.Uri.ToString();
    }

    private static string GetString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var element) ||
            element.ValueKind != JsonValueKind.String)
        {
            return string.Empty;
        }

        return (element.GetString() ?? string.Empty).Trim();
    }

    private static async Task<byte[]> ReadBoundedAsync(
        HttpContent content,
        int maxBytes,
        CancellationToken ct)
    {
        if (content.Headers.ContentLength is > MaxManifestBytes)
            throw new IOException($"manifest.json превышает {MaxManifestBytes} байт.");

        await using var input = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[64 * 1024];

        for (;;)
        {
            var read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false);
            if (read <= 0)
                break;

            if (output.Length + read > maxBytes)
                throw new IOException($"manifest.json превышает {maxBytes} байт.");

            output.Write(buffer, 0, read);
        }

        return output.ToArray();
    }

    private static HttpClient CreateHttp()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true,
            UseProxy = true,
            Proxy = WebRequest.DefaultWebProxy
        };

        return new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
    }
}
