using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace LegendBorn.Services;

/// <summary>
/// Loads the live server catalog without baking a mutable Minecraft IP into the launcher.
/// The website API is authoritative when reachable; static mirrors are revision-aware fallbacks.
/// A recent authoritative cache is preferred over an older static mirror, preventing rollback to
/// a stale server address after an IP migration.
/// </summary>
public static class ServerCatalogService
{
    public const string CanonicalCatalogUrl = "https://legendborn.xyz/api/launcher/servers";
    public const string SelectelCatalogUrl =
        "https://612cd759-4c9d-450e-bc91-a51d3c56e834.selstorage.ru/launcher/servers.json";
    public const string CloudBucketCatalogUrl = "https://pack.legendborn.ru/launcher/servers.json";
    public const string SourceForgeCatalogUrl =
        "https://master.dl.sourceforge.net/project/legendborn-pack/launcher/servers.json";

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(7);
    private static readonly TimeSpan LegacyCacheMaxAge = TimeSpan.FromHours(24);
    private const long MaxCatalogBytes = 2L * 1024 * 1024;

    private static readonly string CachePath = Path.Combine(LauncherPaths.CacheDir, "server_catalog_v2.json");
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        WriteIndented = true
    };

    private static readonly HttpClient Http = CreateHttp();
    private static readonly SemaphoreSlim Gate = new(1, 1);

    private sealed record SourceSpec(string Url, int Rank, bool Authoritative, string Name);

    private static readonly SourceSpec[] Sources =
    {
        new(CanonicalCatalogUrl, 0, true, "LegendBorn API"),
        new(SelectelCatalogUrl, 1, false, "Selectel"),
        new(CloudBucketCatalogUrl, 2, false, "LegendBorn CDN"),
        new(SourceForgeCatalogUrl, 3, false, "SourceForge")
    };

    private sealed class CatalogEnvelope
    {
        [JsonPropertyName("version")] public int Version { get; set; }
        [JsonPropertyName("generatedAtUnix")] public long GeneratedAtUnix { get; set; }
        [JsonPropertyName("validUntilUnix")] public long ValidUntilUnix { get; set; }
        [JsonPropertyName("servers")] public List<ServerListService.ServerInfo> Servers { get; set; } = new();
    }

    private sealed class CacheEnvelope
    {
        public long SavedAtUnix { get; set; }
        public bool Authoritative { get; set; }
        public string SourceUrl { get; set; } = "";
        public CatalogEnvelope Catalog { get; set; } = new();
    }

    private sealed record Candidate(
        CatalogEnvelope Catalog,
        SourceSpec Source,
        long ElapsedMs,
        bool FromCache = false);

    public static async Task<IReadOnlyList<ServerListService.ServerInfo>> GetServersAsync(
        Action<string>? log = null,
        CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var cached = LoadCache(log);

            var fetchTasks = Sources
                .Select(source => FetchAsync(source, log, ct))
                .ToArray();

            Candidate?[] fetched;
            try
            {
                fetched = await Task.WhenAll(fetchTasks).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }

            var online = fetched.Where(static c => c is not null).Select(static c => c!).ToList();

            // The dynamic website API owns the current server endpoint. Do not let an old static
            // mirror override it just because that mirror has a larger legacy version number.
            var authoritativeOnline = online
                .Where(static c => c.Source.Authoritative)
                .OrderByDescending(static c => c.Catalog.Version)
                .ThenByDescending(static c => c.Catalog.GeneratedAtUnix)
                .ThenBy(static c => c.ElapsedMs)
                .FirstOrDefault();

            Candidate? selected = authoritativeOnline;

            // If the canonical endpoint is temporarily unavailable, an unexpired canonical cache
            // is safer than a stale static copy containing the previous IP.
            if (selected is null && cached is not null && cached.Source.Authoritative && IsCacheUsable(cached.Catalog))
                selected = cached;

            // Static mirrors are revision-aware. Speed is only a tie-breaker after freshness.
            if (selected is null)
            {
                var fallbacks = online.Where(static c => !c.Source.Authoritative).ToList();
                if (cached is not null && IsCacheUsable(cached.Catalog))
                    fallbacks.Add(cached);

                selected = fallbacks
                    .OrderByDescending(static c => Math.Max(0, c.Catalog.Version))
                    .ThenByDescending(static c => c.Catalog.GeneratedAtUnix)
                    .ThenBy(static c => c.Source.Rank)
                    .ThenBy(static c => c.ElapsedMs)
                    .FirstOrDefault();
            }

            if (selected is null)
            {
                throw new InvalidOperationException(
                    "Не удалось получить актуальный каталог серверов. " +
                    "Лаунчер не будет использовать зашитый старый IP; проверь интернет и зеркала.");
            }

            var normalized = NormalizeServers(selected.Catalog.Servers, log);
            if (normalized.Count == 0)
                throw new InvalidOperationException("Каталог серверов не содержит валидных серверов.");

            if (!selected.FromCache)
                SaveCache(selected, log);

            log?.Invoke(
                $"server catalog: selected {selected.Source.Name}, revision={selected.Catalog.Version}, " +
                $"generated={selected.Catalog.GeneratedAtUnix}, servers={normalized.Count}" +
                (selected.FromCache ? " (cache)" : ""));

            return normalized;
        }
        finally
        {
            try { Gate.Release(); } catch { }
        }
    }

    private static async Task<Candidate?> FetchAsync(SourceSpec source, Action<string>? log, CancellationToken ct)
    {
        try
        {
            if (!Uri.TryCreate(source.Url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
                return null;

            using var reqCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            reqCts.CancelAfter(RequestTimeout);

            using var req = new HttpRequestMessage(HttpMethod.Get, uri);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            req.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
            TrySetUserAgent(req);

            var sw = Stopwatch.StartNew();
            using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, reqCts.Token)
                .ConfigureAwait(false);
            sw.Stop();

            if (!resp.IsSuccessStatusCode)
            {
                log?.Invoke($"server catalog: {source.Name} -> HTTP {(int)resp.StatusCode}");
                return null;
            }

            var media = resp.Content.Headers.ContentType?.MediaType ?? "";
            if (media.Contains("text/html", StringComparison.OrdinalIgnoreCase))
                return null;

            var length = resp.Content.Headers.ContentLength;
            if (length.HasValue && (length.Value <= 0 || length.Value > MaxCatalogBytes))
                return null;

            await using var stream = await resp.Content.ReadAsStreamAsync(reqCts.Token).ConfigureAwait(false);
            var catalog = await JsonSerializer.DeserializeAsync<CatalogEnvelope>(stream, JsonOptions, reqCts.Token)
                .ConfigureAwait(false);

            if (!IsCatalogUsable(catalog))
            {
                log?.Invoke($"server catalog: {source.Name} returned invalid/expired data");
                return null;
            }

            // Legacy static servers.json files may not have timestamps. Their revision can still
            // participate in fallback selection, but the canonical API always wins when available.
            return new Candidate(catalog!, source, Math.Max(1, sw.ElapsedMilliseconds));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            log?.Invoke($"server catalog: {source.Name} failed — {ex.Message}");
            return null;
        }
    }

    private static bool IsCatalogUsable(CatalogEnvelope? catalog)
    {
        if (catalog is null || catalog.Servers is null || catalog.Servers.Count == 0)
            return false;

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (catalog.ValidUntilUnix > 0 && catalog.ValidUntilUnix < now)
            return false;

        return true;
    }

    private static bool IsCacheUsable(CatalogEnvelope catalog)
    {
        if (!IsCatalogUsable(catalog)) return false;

        // Legacy catalogs have no explicit valid-until. Keep them short-lived so an old IP cannot
        // survive indefinitely on a machine that has not refreshed for weeks.
        if (catalog.ValidUntilUnix <= 0 && catalog.GeneratedAtUnix <= 0)
            return true;

        return true;
    }

    private static List<ServerListService.ServerInfo> NormalizeServers(
        IEnumerable<ServerListService.ServerInfo> servers,
        Action<string>? log)
    {
        var result = new List<ServerListService.ServerInfo>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var source in servers)
        {
            var id = (source.Id ?? "").Trim();
            var name = (source.Name ?? "").Trim();
            var address = NormalizeServerAddress(source.Address);
            var minecraftVersion = (source.MinecraftVersion ?? "").Trim();

            if (id.Length == 0 || name.Length == 0 || address.Length == 0 || minecraftVersion.Length == 0)
                continue;
            if (!ids.Add(id)) continue;

            var loader = source.Loader ?? new ServerListService.LoaderInfo
            {
                Type = source.LoaderName ?? "vanilla",
                Version = source.LoaderVersion ?? ""
            };

            var loaderType = (loader.Type ?? "vanilla").Trim().ToLowerInvariant();
            if (loaderType is not ("vanilla" or "neoforge"))
                continue;

            var loaderVersion = (loader.Version ?? "").Trim();
            var installerUrl = NormalizeHttpsUrl(loader.InstallerUrl);

            if (loaderType == "neoforge")
            {
                if (loaderVersion.Length == 0) continue;

                // Never synthesize an official NeoForged URL here. The exact versioned SourceForge
                // mirror works as a safe bootstrap while LoaderInstaller chooses Maven mirrors for
                // the installer's dependency graph.
                if (installerUrl.Length == 0)
                {
                    installerUrl =
                        $"https://downloads.sourceforge.net/project/legendborn-neoforge/neoforge/" +
                        $"neoforge-{Uri.EscapeDataString(loaderVersion)}-installer.jar";
                }
            }
            else
            {
                loaderVersion = "";
                installerUrl = "";
            }

            var packBase = NormalizeHttpsBaseUrl(source.PackBaseUrl);
            var packMirrors = (source.PackMirrors ?? Array.Empty<string>())
                .Append(packBase)
                .Select(NormalizeHttpsBaseUrl)
                .Where(static value => value.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(MirrorRank)
                .ToArray();

            if (source.SyncPack && packMirrors.Length == 0)
            {
                log?.Invoke($"server catalog: skipped {id}, syncPack=true but no HTTPS pack mirrors");
                continue;
            }

            result.Add(source with
            {
                Id = id,
                Name = name,
                Address = address,
                MinecraftVersion = minecraftVersion,
                Loader = new ServerListService.LoaderInfo
                {
                    Type = loaderType,
                    Version = loaderVersion,
                    InstallerUrl = installerUrl
                },
                PackBaseUrl = packMirrors.FirstOrDefault() ?? "",
                PackMirrors = packMirrors
            });
        }

        return result;
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

    private static string NormalizeServerAddress(string? value)
    {
        var address = (value ?? "").Trim();
        if (address.Length == 0 || address.Length > 255) return "";
        if (address.Contains('/') || address.Contains('\\') || address.Any(char.IsWhiteSpace)) return "";
        if (address.Contains("://", StringComparison.Ordinal)) return "";
        return address;
    }

    private static string NormalizeHttpsUrl(string? value)
    {
        var text = (value ?? "").Trim();
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            return "";
        return new UriBuilder(uri) { Fragment = "" }.Uri.ToString();
    }

    private static string NormalizeHttpsBaseUrl(string? value)
    {
        var url = NormalizeHttpsUrl(value);
        if (url.Length == 0) return "";
        return url.EndsWith("/", StringComparison.Ordinal) ? url : url + "/";
    }

    private static Candidate? LoadCache(Action<string>? log)
    {
        try
        {
            if (!File.Exists(CachePath)) return null;

            var json = File.ReadAllText(CachePath, Utf8NoBom);
            var cache = JsonSerializer.Deserialize<CacheEnvelope>(json, JsonOptions);
            if (cache?.Catalog is null || !IsCatalogUsable(cache.Catalog))
                return null;

            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (cache.Catalog.ValidUntilUnix <= 0 &&
                (cache.SavedAtUnix <= 0 || now - cache.SavedAtUnix > (long)LegacyCacheMaxAge.TotalSeconds))
                return null;

            var spec = new SourceSpec(
                cache.SourceUrl,
                cache.Authoritative ? 0 : 50,
                cache.Authoritative,
                cache.Authoritative ? "LegendBorn API cache" : "mirror cache");

            return new Candidate(cache.Catalog, spec, long.MaxValue, FromCache: true);
        }
        catch (Exception ex)
        {
            log?.Invoke("server catalog: cache ignored — " + ex.Message);
            return null;
        }
    }

    private static void SaveCache(Candidate candidate, Action<string>? log)
    {
        try
        {
            LauncherPaths.EnsureDir(LauncherPaths.CacheDir);
            var payload = new CacheEnvelope
            {
                SavedAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Authoritative = candidate.Source.Authoritative,
                SourceUrl = candidate.Source.Url,
                Catalog = candidate.Catalog
            };

            var json = JsonSerializer.Serialize(payload, JsonOptions);
            var tmp = CachePath + ".tmp";
            File.WriteAllText(tmp, json, Utf8NoBom);

            if (File.Exists(CachePath))
                File.Move(tmp, CachePath, overwrite: true);
            else
                File.Move(tmp, CachePath);
        }
        catch (Exception ex)
        {
            log?.Invoke("server catalog: cache save failed — " + ex.Message);
        }
        finally
        {
            try { if (File.Exists(CachePath + ".tmp")) File.Delete(CachePath + ".tmp"); } catch { }
        }
    }

    private static void TrySetUserAgent(HttpRequestMessage req)
    {
        try
        {
            req.Headers.UserAgent.Clear();
            req.Headers.UserAgent.ParseAdd(
                string.IsNullOrWhiteSpace(LauncherIdentity.UserAgent)
                    ? $"LegendBornLauncher/{LauncherIdentity.InformationalVersion}"
                    : LauncherIdentity.UserAgent);
        }
        catch { }
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
            AllowAutoRedirect = true,
            MaxConnectionsPerServer = 8
        };

        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }
}
