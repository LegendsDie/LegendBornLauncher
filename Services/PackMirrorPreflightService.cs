using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
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
/// Preflights manifest.json on every configured pack mirror in parallel and moves the freshest
/// valid manifest to the front. The existing MinecraftService still performs SHA-256 validation,
/// download fallback, seed-only handling and pruning; this service only prevents a stale mirror
/// from winning because it responded first.
/// </summary>
public static class PackMirrorPreflightService
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(20);
    private const long MaxManifestBytes = 5L * 1024 * 1024;

    private static readonly HttpClient Http = CreateHttp();
    private static readonly object CacheLock = new();
    private static CacheEntry? _cache;

    private sealed class ManifestHead
    {
        [JsonPropertyName("packId")] public string? PackId { get; set; }
        [JsonPropertyName("packVersion")] public string? PackVersion { get; set; }
        [JsonPropertyName("version")] public string? Version { get; set; }
        [JsonPropertyName("build")] public int? Build { get; set; }
    }

    private sealed record ProbeResult(
        string BaseUrl,
        ManifestHead Manifest,
        long ElapsedMs,
        int OriginalIndex);

    private sealed record CacheEntry(string Key, long SavedAtUnixMs, string[] OrderedMirrors);

    public static async Task<string[]> OrderByFreshnessAsync(
        IEnumerable<string>? mirrors,
        Action<string>? log = null,
        CancellationToken ct = default)
    {
        var normalized = (mirrors ?? Array.Empty<string>())
            .Select(NormalizeBaseUrl)
            .Where(static value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (normalized.Length <= 1)
            return normalized;

        var cacheKey = string.Join("\n", normalized.Select(static value => value.ToLowerInvariant()));
        if (TryGetCached(cacheKey, out var cached))
        {
            log?.Invoke("Pack mirrors: использую свежий preflight cache.");
            return cached;
        }

        var probes = await Task.WhenAll(normalized.Select((mirror, index) => ProbeAsync(mirror, index, ct)))
            .ConfigureAwait(false);

        var successful = probes
            .Where(static result => result is not null)
            .Select(static result => result!)
            .ToList();
        if (successful.Count == 0)
        {
            log?.Invoke("Pack mirrors: preflight не получил ни одного manifest; использую обычный fallback pipeline.");
            return normalized;
        }

        var orderedSuccessful = successful
            .OrderByDescending(static result => result.Manifest.Build ?? int.MinValue)
            .ThenByDescending(static result => VersionScore(result.Manifest.PackVersion ?? result.Manifest.Version))
            .ThenBy(static result => MirrorRank(result.BaseUrl))
            .ThenBy(static result => result.ElapsedMs)
            .ThenBy(static result => result.OriginalIndex)
            .ToList();

        var selected = orderedSuccessful[0];
        log?.Invoke(
            $"Pack mirrors: freshest={selected.BaseUrl}, packId={selected.Manifest.PackId ?? "?"}, " +
            $"version={selected.Manifest.PackVersion ?? selected.Manifest.Version ?? "?"}, " +
            $"build={(selected.Manifest.Build?.ToString() ?? "?")}");

        // Successful mirrors are ordered by freshness first. Failed probes are retained afterwards:
        // they may still be useful for individual blobs if a manifest endpoint was temporarily slow.
        var ordered = orderedSuccessful
            .Select(static result => result.BaseUrl)
            .Concat(normalized.Where(url => successful.All(result =>
                !url.Equals(result.BaseUrl, StringComparison.OrdinalIgnoreCase))))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        StoreCache(cacheKey, ordered);
        return ordered;
    }

    private static async Task<ProbeResult?> ProbeAsync(
        string baseUrl,
        int originalIndex,
        CancellationToken ct)
    {
        try
        {
            using var requestCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            requestCts.CancelAfter(ProbeTimeout);

            var url = new Uri(new Uri(baseUrl), "manifest.json");
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
            TrySetUserAgent(request);

            var sw = Stopwatch.StartNew();
            using var response = await Http.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    requestCts.Token)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode) return null;
            if ((response.Content.Headers.ContentType?.MediaType ?? "")
                .Contains("text/html", StringComparison.OrdinalIgnoreCase)) return null;

            var length = response.Content.Headers.ContentLength;
            if (length.HasValue && (length.Value <= 0 || length.Value > MaxManifestBytes)) return null;

            var payload = await ReadBodyLimitedAsync(response.Content, requestCts.Token).ConfigureAwait(false);
            sw.Stop();

            ManifestHead? manifest;
            try
            {
                manifest = JsonSerializer.Deserialize<ManifestHead>(payload, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    AllowTrailingCommas = true,
                    ReadCommentHandling = JsonCommentHandling.Skip
                });
            }
            catch (JsonException)
            {
                return null;
            }

            if (manifest is null) return null;
            if (string.IsNullOrWhiteSpace(manifest.PackId) &&
                string.IsNullOrWhiteSpace(manifest.PackVersion) &&
                string.IsNullOrWhiteSpace(manifest.Version) &&
                !manifest.Build.HasValue)
                return null;

            var finalBase = baseUrl;
            if (response.RequestMessage?.RequestUri is { } finalUri)
            {
                if (finalUri.Scheme != Uri.UriSchemeHttps)
                    return null;
                finalBase = NormalizeBaseUrl(new Uri(finalUri, "./").ToString());
                if (finalBase.Length == 0)
                    return null;
            }

            return new ProbeResult(finalBase, manifest, Math.Max(1, sw.ElapsedMilliseconds), originalIndex);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<byte[]> ReadBodyLimitedAsync(HttpContent content, CancellationToken ct)
    {
        await using var input = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var memory = new MemoryStream(capacity: 16 * 1024);
        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        long total = 0;

        try
        {
            while (true)
            {
                var read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false);
                if (read == 0) break;

                total += read;
                if (total > MaxManifestBytes)
                    throw new InvalidOperationException("Pack manifest preflight response exceeded the safety bound.");

                memory.Write(buffer, 0, read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: false);
        }

        if (total <= 0)
            throw new InvalidOperationException("Pack manifest preflight response was empty.");

        return memory.ToArray();
    }

    private static bool TryGetCached(string key, out string[] ordered)
    {
        ordered = Array.Empty<string>();
        lock (CacheLock)
        {
            var cache = _cache;
            if (cache is null || !cache.Key.Equals(key, StringComparison.Ordinal))
                return false;

            var ageMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - cache.SavedAtUnixMs;
            if (ageMs < 0 || ageMs > CacheTtl.TotalMilliseconds)
            {
                _cache = null;
                return false;
            }

            ordered = cache.OrderedMirrors.ToArray();
            return true;
        }
    }

    private static void StoreCache(string key, string[] ordered)
    {
        lock (CacheLock)
        {
            _cache = new CacheEntry(
                key,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                ordered.ToArray());
        }
    }

    private static long VersionScore(string? version)
    {
        var text = (version ?? "").Trim();
        if (text.Length == 0) return 0;

        long score = 0;
        var count = 0;
        foreach (var token in text.Split(new[] { '.', '-', '_', '+' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!int.TryParse(token, out var part)) continue;
            part = Math.Clamp(part, 0, 9999);
            score = checked(score * 10_000 + part);
            if (++count >= 4) break;
        }

        return score;
    }

    private static int MirrorRank(string url)
    {
        if (url.Contains("selstorage.ru", StringComparison.OrdinalIgnoreCase) ||
            url.Contains("selcloud.ru", StringComparison.OrdinalIgnoreCase)) return 0;
        if (url.Contains("pack.legendborn.ru", StringComparison.OrdinalIgnoreCase)) return 1;
        if (url.Contains("master.dl.sourceforge.net", StringComparison.OrdinalIgnoreCase)) return 2;
        if (url.Contains("downloads.sourceforge.net", StringComparison.OrdinalIgnoreCase)) return 3;
        if (url.Contains("sourceforge.net", StringComparison.OrdinalIgnoreCase)) return 4;
        return 5;
    }

    private static string NormalizeBaseUrl(string? value)
    {
        var text = (value ?? "").Trim();
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            return "";

        var builder = new UriBuilder(uri) { Query = "", Fragment = "" };
        if (!builder.Path.EndsWith("/", StringComparison.Ordinal)) builder.Path += "/";
        return builder.Uri.ToString();
    }

    private static void TrySetUserAgent(HttpRequestMessage request)
    {
        try
        {
            request.Headers.UserAgent.Clear();
            request.Headers.UserAgent.ParseAdd(
                string.IsNullOrWhiteSpace(LauncherIdentity.UserAgent)
                    ? $"LegendBornLauncher/{LauncherIdentity.InformationalVersion}"
                    : LauncherIdentity.UserAgent);
        }
        catch
        {
        }
    }

    private static HttpClient CreateHttp()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            Proxy = WebRequest.DefaultWebProxy,
            UseProxy = true,
            ConnectTimeout = TimeSpan.FromSeconds(4),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            AllowAutoRedirect = true,
            MaxConnectionsPerServer = 8
        };

        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }
}
