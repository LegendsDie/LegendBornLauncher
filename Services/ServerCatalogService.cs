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
/// Loads the live server catalog without baking mutable infrastructure into the launcher.
/// The canonical website API is authoritative. Cached/static data is accepted only while it is
/// temporally fresh enough to avoid surviving an endpoint migration.
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
    private static readonly TimeSpan CacheMaxAge = TimeSpan.FromHours(24);
    private static readonly TimeSpan CatalogMaxAgeWithoutExpiry = TimeSpan.FromHours(24);
    private static readonly TimeSpan MaxExplicitCatalogLifetime = TimeSpan.FromHours(48);
    private static readonly TimeSpan FutureClockSkew = TimeSpan.FromMinutes(5);
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
        [JsonPropertyName("servers")] public List<CatalogServer> Servers { get; set; } = new();
    }

    private sealed class CatalogServer
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("address")] public string Address { get; set; } = "";
        [JsonPropertyName("minecraftVersion")] public string MinecraftVersion { get; set; } = "";
        [JsonPropertyName("loader")] public CatalogLoader? Loader { get; set; }
        [JsonPropertyName("loaderName")] public string? LoaderName { get; set; }
        [JsonPropertyName("loaderVersion")] public string? LoaderVersion { get; set; }
        [JsonPropertyName("clientVersionId")] public string? ClientVersionId { get; set; }
        [JsonPropertyName("packBaseUrl")] public string PackBaseUrl { get; set; } = "";
        [JsonPropertyName("packMirrors")] public string[] PackMirrors { get; set; } = Array.Empty<string>();
        [JsonPropertyName("syncPack")] public bool SyncPack { get; set; }
    }

    private sealed class CatalogLoader
    {
        [JsonPropertyName("type")] public string Type { get; set; } = "vanilla";
        [JsonPropertyName("version")] public string Version { get; set; } = "";
        [JsonPropertyName("installerUrl")] public string InstallerUrl { get; set; } = "";
        [JsonPropertyName("installerMirrors")] public string[] InstallerMirrors { get; set; } = Array.Empty<string>();
        [JsonPropertyName("installerSha256")] public string InstallerSha256 { get; set; } = "";
        [JsonPropertyName("mavenMirrors")] public string[] MavenMirrors { get; set; } = Array.Empty<string>();
        [JsonPropertyName("installerMirrorArgument")] public string InstallerMirrorArgument { get; set; } = "";
    }

    private sealed class CacheEnvelope
    {
        public long SavedAtUnix { get; set; }
        public bool Authoritative { get; set; }
        public string SourceUrl { get; set; } = "";
        public CatalogEnvelope Catalog { get; set; } = new();
    }

    private sealed record Candidate(CatalogEnvelope Catalog, SourceSpec Source, long ElapsedMs, bool FromCache = false);

    public static async Task<IReadOnlyList<ServerListService.ServerInfo>> GetServersAsync(Action<string>? log = null, CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var cached = LoadCache(log);
            var fetchTasks = Sources.Select(source => FetchAsync(source, log, ct)).ToArray();
            Candidate?[] fetched;
            try { fetched = await Task.WhenAll(fetchTasks).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }

            var online = fetched.Where(static candidate => candidate is not null).Select(static candidate => candidate!).ToList();
            var authoritativeOnline = online.Where(static candidate => candidate.Source.Authoritative)
                .OrderByDescending(static candidate => candidate.Catalog.Version)
                .ThenByDescending(static candidate => candidate.Catalog.GeneratedAtUnix)
                .ThenBy(static candidate => candidate.ElapsedMs).FirstOrDefault();

            Candidate? selected = authoritativeOnline;
            if (selected is null && cached is not null && cached.Source.Authoritative && IsCacheUsable(cached.Catalog)) selected = cached;
            if (selected is null)
            {
                var fallbacks = online.Where(static candidate => !candidate.Source.Authoritative).ToList();
                if (cached is not null && IsCacheUsable(cached.Catalog)) fallbacks.Add(cached);
                selected = fallbacks.OrderByDescending(static candidate => Math.Max(0, candidate.Catalog.Version))
                    .ThenByDescending(static candidate => candidate.Catalog.GeneratedAtUnix)
                    .ThenBy(static candidate => candidate.Source.Rank)
                    .ThenBy(static candidate => candidate.ElapsedMs).FirstOrDefault();
            }

            if (selected is null)
                throw new InvalidOperationException("Не удалось получить актуальный каталог серверов. Лаунчер не будет использовать старый IP или протухший distribution contract.");

            NeoForgeDistributionBootstrap.Reset();
            var normalized = NormalizeServers(selected.Catalog.Servers, log);
            if (normalized.Count == 0) throw new InvalidOperationException("Каталог серверов не содержит валидных серверов.");
            if (!selected.FromCache) SaveCache(selected, log);

            log?.Invoke($"server catalog: selected {selected.Source.Name}, revision={selected.Catalog.Version}, generated={selected.Catalog.GeneratedAtUnix}, validUntil={selected.Catalog.ValidUntilUnix}, servers={normalized.Count}" + (selected.FromCache ? " (cache)" : ""));
            return normalized;
        }
        finally { try { Gate.Release(); } catch { } }
    }

    private static async Task<Candidate?> FetchAsync(SourceSpec source, Action<string>? log, CancellationToken ct)
    {
        try
        {
            if (!Uri.TryCreate(source.Url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return null;
            using var reqCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            reqCts.CancelAfter(RequestTimeout);
            using var req = new HttpRequestMessage(HttpMethod.Get, uri);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            req.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
            TrySetUserAgent(req);
            var sw = Stopwatch.StartNew();
            using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, reqCts.Token).ConfigureAwait(false);
            sw.Stop();
            if (!resp.IsSuccessStatusCode) { log?.Invoke($"server catalog: {source.Name} -> HTTP {(int)resp.StatusCode}"); return null; }
            var media = resp.Content.Headers.ContentType?.MediaType ?? "";
            if (media.Contains("text/html", StringComparison.OrdinalIgnoreCase)) return null;
            var length = resp.Content.Headers.ContentLength;
            if (length.HasValue && (length.Value <= 0 || length.Value > MaxCatalogBytes)) return null;
            await using var stream = await resp.Content.ReadAsStreamAsync(reqCts.Token).ConfigureAwait(false);
            var catalog = await JsonSerializer.DeserializeAsync<CatalogEnvelope>(stream, JsonOptions, reqCts.Token).ConfigureAwait(false);
            if (!IsCatalogUsable(catalog, out var reason)) { log?.Invoke($"server catalog: {source.Name} rejected — {reason}"); return null; }
            return new Candidate(catalog!, source, Math.Max(1, sw.ElapsedMilliseconds));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { log?.Invoke($"server catalog: {source.Name} failed — {ex.Message}"); return null; }
    }

    private static bool IsCatalogUsable(CatalogEnvelope? catalog, out string reason)
    {
        reason = "";
        if (catalog is null || catalog.Servers is null || catalog.Servers.Count == 0) { reason = "empty catalog"; return false; }
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var futureSkew = (long)FutureClockSkew.TotalSeconds;
        if (catalog.GeneratedAtUnix <= 0) { reason = "generatedAtUnix missing"; return false; }
        if (catalog.GeneratedAtUnix > now + futureSkew) { reason = "generatedAtUnix too far in the future"; return false; }
        if (catalog.ValidUntilUnix > 0)
        {
            if (catalog.ValidUntilUnix <= now) { reason = "expired"; return false; }
            if (catalog.ValidUntilUnix < catalog.GeneratedAtUnix) { reason = "validUntilUnix precedes generatedAtUnix"; return false; }
            if (catalog.ValidUntilUnix - catalog.GeneratedAtUnix > (long)MaxExplicitCatalogLifetime.TotalSeconds) { reason = "explicit lifetime exceeds safety bound"; return false; }
        }
        else if (now - catalog.GeneratedAtUnix > (long)CatalogMaxAgeWithoutExpiry.TotalSeconds) { reason = "implicit-expiry catalog is too old"; return false; }
        return true;
    }

    private static bool IsCacheUsable(CatalogEnvelope catalog) => IsCatalogUsable(catalog, out _);

    private static List<ServerListService.ServerInfo> NormalizeServers(IEnumerable<CatalogServer> servers, Action<string>? log)
    {
        var result = new List<ServerListService.ServerInfo>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in servers)
        {
            var id = (source.Id ?? "").Trim();
            var name = (source.Name ?? "").Trim();
            var address = NormalizeServerAddress(source.Address);
            var minecraftVersion = (source.MinecraftVersion ?? "").Trim();
            if (id.Length == 0 || name.Length == 0 || address.Length == 0 || minecraftVersion.Length == 0 || !ids.Add(id)) continue;

            var loader = source.Loader ?? new CatalogLoader { Type = source.LoaderName ?? "vanilla", Version = source.LoaderVersion ?? "" };
            var loaderType = (loader.Type ?? "vanilla").Trim().ToLowerInvariant();
            if (loaderType is not ("vanilla" or "neoforge")) continue;
            var loaderVersion = (loader.Version ?? "").Trim();
            var installerUrl = NormalizeHttpsUrl(loader.InstallerUrl);

            if (loaderType == "neoforge")
            {
                if (loaderVersion.Length == 0) continue;
                var installerMirrors = (loader.InstallerMirrors ?? Array.Empty<string>()).Prepend(installerUrl)
                    .Select(NormalizeHttpsUrl).Where(static value => value.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                if (installerMirrors.Length == 0) { log?.Invoke($"server catalog: skipped {id}, NeoForge installerMirrors is empty"); continue; }
                installerUrl = installerUrl.Length > 0 ? installerUrl : installerMirrors[0];
                if (!NeoForgeDistributionBootstrap.TryRegister(loaderVersion, installerUrl, installerMirrors, loader.InstallerSha256, loader.MavenMirrors, loader.InstallerMirrorArgument, out var contractError))
                { log?.Invoke($"server catalog: skipped {id}, NeoForge contract invalid — {contractError}"); continue; }
            }
            else { loaderVersion = ""; installerUrl = ""; }

            var packBase = NormalizeHttpsBaseUrl(source.PackBaseUrl);
            var packMirrors = (source.PackMirrors ?? Array.Empty<string>()).Append(packBase).Select(NormalizeHttpsBaseUrl)
                .Where(static value => value.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(MirrorRank).ToArray();
            if (source.SyncPack && packMirrors.Length == 0) { log?.Invoke($"server catalog: skipped {id}, syncPack=true but no HTTPS pack mirrors"); continue; }

            result.Add(new ServerListService.ServerInfo
            {
                Id = id,
                Name = name,
                Address = address,
                MinecraftVersion = minecraftVersion,
                Loader = new ServerListService.LoaderInfo { Type = loaderType, Version = loaderVersion, InstallerUrl = installerUrl },
                LoaderName = source.LoaderName,
                LoaderVersion = source.LoaderVersion,
                ClientVersionId = string.IsNullOrWhiteSpace(source.ClientVersionId) ? null : source.ClientVersionId.Trim(),
                PackBaseUrl = packMirrors.FirstOrDefault() ?? "",
                PackMirrors = packMirrors,
                SyncPack = source.SyncPack
            });
        }
        return result;
    }

    private static int MirrorRank(string url)
    {
        if (url.Contains("selstorage.ru", StringComparison.OrdinalIgnoreCase) || url.Contains("selcloud.ru", StringComparison.OrdinalIgnoreCase)) return 0;
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
        if (address.Contains('/') || address.Contains('\\') || address.Any(char.IsWhiteSpace) || address.Contains("://", StringComparison.Ordinal)) return "";
        return address;
    }

    private static string NormalizeHttpsUrl(string? value) => NeoForgeDistributionBootstrap.NormalizeHttpsUrl(value);
    private static string NormalizeHttpsBaseUrl(string? value) => NeoForgeDistributionBootstrap.NormalizeHttpsBase(value);

    private static Candidate? LoadCache(Action<string>? log)
    {
        try
        {
            if (!File.Exists(CachePath)) return null;
            var json = File.ReadAllText(CachePath, Utf8NoBom);
            var cache = JsonSerializer.Deserialize<CacheEnvelope>(json, JsonOptions);
            if (cache?.Catalog is null || !IsCacheUsable(cache.Catalog)) return null;
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (cache.SavedAtUnix <= 0 || now < cache.SavedAtUnix - (long)FutureClockSkew.TotalSeconds || now - cache.SavedAtUnix > (long)CacheMaxAge.TotalSeconds) return null;
            var spec = new SourceSpec(cache.SourceUrl, cache.Authoritative ? 0 : 50, cache.Authoritative, cache.Authoritative ? "LegendBorn API cache" : "mirror cache");
            return new Candidate(cache.Catalog, spec, long.MaxValue, FromCache: true);
        }
        catch (Exception ex) { log?.Invoke("server catalog: cache ignored — " + ex.Message); return null; }
    }

    private static void SaveCache(Candidate candidate, Action<string>? log)
    {
        try
        {
            LauncherPaths.EnsureDir(LauncherPaths.CacheDir);
            var payload = new CacheEnvelope { SavedAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), Authoritative = candidate.Source.Authoritative, SourceUrl = candidate.Source.Url, Catalog = candidate.Catalog };
            var json = JsonSerializer.Serialize(payload, JsonOptions);
            var tmp = CachePath + ".tmp";
            File.WriteAllText(tmp, json, Utf8NoBom);
            File.Move(tmp, CachePath, overwrite: true);
        }
        catch (Exception ex) { log?.Invoke("server catalog: cache save failed — " + ex.Message); }
        finally { try { if (File.Exists(CachePath + ".tmp")) File.Delete(CachePath + ".tmp"); } catch { } }
    }

    private static void TrySetUserAgent(HttpRequestMessage req)
    {
        try { req.Headers.UserAgent.Clear(); req.Headers.UserAgent.ParseAdd(string.IsNullOrWhiteSpace(LauncherIdentity.UserAgent) ? $"LegendBornLauncher/{LauncherIdentity.InformationalVersion}" : LauncherIdentity.UserAgent); }
        catch { }
    }

    private static HttpClient CreateHttp()
    {
        var handler = new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All, Proxy = WebRequest.DefaultWebProxy, UseProxy = true, ConnectTimeout = TimeSpan.FromSeconds(7), PooledConnectionLifetime = TimeSpan.FromMinutes(2), AllowAutoRedirect = true, MaxConnectionsPerServer = 8 };
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }
}
