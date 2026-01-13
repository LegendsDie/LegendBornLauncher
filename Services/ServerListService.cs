using System;
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

public sealed class ServerListService
{
    public const string DefaultServersUrl = "https://legendborn.ru/launcher/servers.json";

    // ✅ Bunny CDN (если положишь servers.json туда же, где у тебя pack на Bunny)
    // Рекомендуемый путь: https://legendborn-pack.b-cdn.net/launcher/servers.json
    public const string BunnyServersUrl = "https://legendborn-pack.b-cdn.net/launcher/servers.json";

    // ✅ SF master — запасной
    public const string SourceForgeMasterServersUrl = "https://master.dl.sourceforge.net/project/legendborn-pack/launcher/servers.json";

    // ✅ дефолтные зеркала servers.json:
    // 1) твой сайт
    // 2) Bunny CDN
    // 3) SF master
    public static readonly string[] DefaultServersMirrors =
    {
        DefaultServersUrl,
        BunnyServersUrl,
        SourceForgeMasterServersUrl
    };

    private const string LauncherUserAgent = "LegendBornLauncher/0.2.0";

    // Таймауты под РФ (быстрый фейл у primary, затем race на fallback)
    private const int PrimaryTimeoutSec1 = 6;
    private const int PrimaryTimeoutSec2 = 12;
    private const int FallbackTimeoutSec = 16;

    // safety: servers.json не должен быть огромным
    private const long MaxServersJsonBytes = 2 * 1024 * 1024; // 2 MB

    private static readonly HttpClient _http = CreateHttp();

    private static readonly string CacheFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "LegendBorn", "cache", "servers_cache.json");

    public async Task<IReadOnlyList<ServerInfo>> GetServersAsync(
        IEnumerable<string>? mirrors = null,
        CancellationToken ct = default)
    {
        var urls = (mirrors ?? DefaultServersMirrors)
            .Select(NormalizeAbsoluteUrl)
            .Where(u => !string.IsNullOrWhiteSpace(u))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (urls.Length == 0)
            throw new InvalidOperationException("Нет URL для servers.json");

        // primary = legendborn.ru если есть
        var primary = urls.FirstOrDefault(u => u.Contains("legendborn.ru", StringComparison.OrdinalIgnoreCase))
                      ?? urls[0];

        // 1) СНАЧАЛА — сайт (короткие таймауты)
        var primaryRes =
            await TryFetchServersFromUrlAsync(primary, ct, TimeSpan.FromSeconds(PrimaryTimeoutSec1)).ConfigureAwait(false)
            ?? await TryFetchServersFromUrlAsync(primary, ct, TimeSpan.FromSeconds(PrimaryTimeoutSec2)).ConfigureAwait(false);

        if (primaryRes is not null)
        {
            SaveCacheQuiet(primaryRes.Value.Json);
            return primaryRes.Value.Servers;
        }

        // 2) Если сайт не отдал — делаем race между fallback'ами (Bunny/SF/прочие)
        var fallbacks = urls
            .Where(u => !u.Equals(primary, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (fallbacks.Length == 0)
        {
            var cachedOnly = TryLoadCache();
            if (cachedOnly is not null && cachedOnly.Count > 0)
                return cachedOnly;

            throw new InvalidOperationException("Primary зеркало упало, fallback'ов нет, кеш пуст.");
        }

        // небольшой приоритет по типу (не “жёстко”, просто порядок старта)
        fallbacks = OrderFallbacks(fallbacks);

        using var raceCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var tasks = fallbacks
            .Select(u => TryFetchServersFromUrlAsync(u, raceCts.Token, TimeSpan.FromSeconds(FallbackTimeoutSec)))
            .ToList();

        while (tasks.Count > 0)
        {
            var finished = await Task.WhenAny(tasks).ConfigureAwait(false);
            tasks.Remove(finished);

            var res = await finished.ConfigureAwait(false);
            if (res is not null)
            {
                raceCts.Cancel();
                SaveCacheQuiet(res.Value.Json);
                return res.Value.Servers;
            }
        }

        // 3) если вообще всё плохо — пытаемся подняться с кеша
        var cached = TryLoadCache();
        if (cached is not null && cached.Count > 0)
            return cached;

        throw new InvalidOperationException("Не удалось загрузить servers.json ни с одного URL (site + fallbacks).");
    }

    public async Task<IReadOnlyList<ServerInfo>> GetServersOrDefaultAsync(
        IEnumerable<string>? mirrors = null,
        CancellationToken ct = default)
    {
        try
        {
            return await GetServersAsync(mirrors, ct).ConfigureAwait(false);
        }
        catch
        {
            // fallback, чтобы лаунчер не умер даже если всё лежит
            return new[]
            {
                new ServerInfo
                {
                    Id = "legendborn",
                    Name = "LegendBorn",
                    Address = "legendcraft.minerent.io",
                    MinecraftVersion = "1.21.1",
                    Loader = new LoaderInfo
                    {
                        Type = "neoforge",
                        Version = "21.1.216",
                        InstallerUrl = "https://maven.neoforged.net/releases/net/neoforged/neoforge/21.1.216/neoforge-21.1.216-installer.jar"
                    },
                    ClientVersionId = "LegendBorn",
                    PackBaseUrl = "https://legendborn.ru/launcher/pack/",
                    PackMirrors = new[]
                    {
                        "https://legendborn.ru/launcher/pack/",
                        "https://legendborn-pack.b-cdn.net/launcher/pack/",
                        "https://master.dl.sourceforge.net/project/legendborn-pack/launcher/pack/"
                    },
                    SyncPack = true
                }
            };
        }
    }

    // =========================
    // Fetch
    // =========================

    private async Task<(string Json, IReadOnlyList<ServerInfo> Servers)?> TryFetchServersFromUrlAsync(
        string url,
        CancellationToken ct,
        TimeSpan timeout)
    {
        url = NormalizeAbsoluteUrl(url);
        if (string.IsNullOrWhiteSpace(url))
            return null;

        try
        {
            using var reqCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            reqCts.CancelAfter(timeout);

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            req.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };

            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, reqCts.Token)
                .ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode)
                return null;

            var media = resp.Content.Headers.ContentType?.MediaType ?? "";
            if (media.Contains("text/html", StringComparison.OrdinalIgnoreCase))
                return null;

            var len = resp.Content.Headers.ContentLength;
            if (len.HasValue && len.Value > MaxServersJsonBytes)
                return null;

            // читаем безопаснее через stream (ReadAsStringAsync без ct)
            await using var stream = await resp.Content.ReadAsStreamAsync(reqCts.Token).ConfigureAwait(false);

            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms, reqCts.Token).ConfigureAwait(false);

            if (ms.Length > MaxServersJsonBytes)
                return null;

            var json = System.Text.Encoding.UTF8.GetString(ms.ToArray());
            reqCts.Token.ThrowIfCancellationRequested();

            var trimmed = (json ?? "").TrimStart();
            if (trimmed.StartsWith("<", StringComparison.Ordinal))
                return null;

            var root = JsonSerializer.Deserialize(json, ServerListJsonContext.Default.ServersRoot);
            if (root is null || root.Servers is null || root.Servers.Count == 0)
                return null;

            var result = root.Servers
                .Select(s =>
                {
                    try { return NormalizeServer(s); }
                    catch { return null; }
                })
                .Where(s => s is not null)
                .Cast<ServerInfo>()
                .ToList();

            if (result.Count == 0)
                return null;

            return (json, result);
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

    private static string[] OrderFallbacks(string[] fallbacks)
    {
        // лёгкий приоритет: Bunny -> SF master -> прочее
        return fallbacks
            .Select(NormalizeAbsoluteUrl)
            .Where(u => !string.IsNullOrWhiteSpace(u))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(u =>
            {
                var lu = u.ToLowerInvariant();
                if (lu.Contains("b-cdn.net") || lu.Contains("bunny")) return 0;
                if (lu.Contains("master.dl.sourceforge.net")) return 1;
                if (lu.Contains("sourceforge.net")) return 2;
                return 3;
            })
            .ToArray();
    }

    // =========================
    // Cache
    // =========================

    private static void SaveCacheQuiet(string json)
    {
        try
        {
            var dir = Path.GetDirectoryName(CacheFilePath);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);

            var tmp = CacheFilePath + ".tmp";
            File.WriteAllText(tmp, json);

            // максимально совместимо: без null backup
            if (File.Exists(CacheFilePath))
            {
                var bak = CacheFilePath + ".bak";
                try { if (File.Exists(bak)) File.Delete(bak); } catch { }

                File.Replace(tmp, CacheFilePath, bak, ignoreMetadataErrors: true);
                try { if (File.Exists(bak)) File.Delete(bak); } catch { }
            }
            else
            {
                File.Move(tmp, CacheFilePath);
            }

            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
        catch { }
    }

    private static IReadOnlyList<ServerInfo>? TryLoadCache()
    {
        try
        {
            if (!File.Exists(CacheFilePath))
                return null;

            var json = File.ReadAllText(CacheFilePath);
            var root = JsonSerializer.Deserialize(json, ServerListJsonContext.Default.ServersRoot);
            if (root is null || root.Servers is null || root.Servers.Count == 0)
                return null;

            var result = root.Servers
                .Select(s =>
                {
                    try { return NormalizeServer(s); }
                    catch { return null; }
                })
                .Where(s => s is not null)
                .Cast<ServerInfo>()
                .ToList();

            return result.Count > 0 ? result : null;
        }
        catch
        {
            return null;
        }
    }

    // =========================
    // Normalize / Validate
    // =========================

    private static ServerInfo? NormalizeServer(ServerInfo s)
    {
        if (string.IsNullOrWhiteSpace(s.Id)) return null;
        if (string.IsNullOrWhiteSpace(s.Name)) return null;
        if (string.IsNullOrWhiteSpace(s.Address)) return null;

        var mc = (s.MinecraftVersion ?? "").Trim();
        if (string.IsNullOrWhiteSpace(mc)) return null;

        var loader = s.Loader;

        // legacy support
        if (loader is null)
        {
            var legacyName = (s.LoaderName ?? "vanilla").Trim();
            var legacyVer = (s.LoaderVersion ?? "").Trim();

            loader = new LoaderInfo
            {
                Type = legacyName,
                Version = legacyVer,
                InstallerUrl = ""
            };
        }

        var loaderType = (loader.Type ?? "vanilla").Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(loaderType)) loaderType = "vanilla";

        var loaderVer = (loader.Version ?? "").Trim();
        var installerUrl = (loader.InstallerUrl ?? "").Trim();

        // если installerUrl пустой — подставим официальный
        if (loaderType != "vanilla" && string.IsNullOrWhiteSpace(installerUrl))
        {
            installerUrl = loaderType switch
            {
                "neoforge" when !string.IsNullOrWhiteSpace(loaderVer)
                    => $"https://maven.neoforged.net/releases/net/neoforged/neoforge/{loaderVer}/neoforge-{loaderVer}-installer.jar",

                "forge" when !string.IsNullOrWhiteSpace(loaderVer)
                    => $"https://maven.minecraftforge.net/net/minecraftforge/forge/{mc}-{loaderVer}/forge-{mc}-{loaderVer}-installer.jar",

                _ => ""
            };
        }

        if (loaderType != "vanilla" && string.IsNullOrWhiteSpace(installerUrl))
            return null;

        // ✅ ВАЖНО: валидируем как absolute URL
        var packBase = NormalizeAbsoluteBaseUrl(s.PackBaseUrl);

        var mirrors = (s.PackMirrors ?? Array.Empty<string>())
            .Select(NormalizeAbsoluteBaseUrl)
            .Where(u => !string.IsNullOrWhiteSpace(u))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        // ✅ КЛЮЧЕВОЕ УЛУЧШЕНИЕ:
        // packBaseUrl всегда добавляем в PackMirrors первым (если задан),
        // чтобы дальше любой код мог использовать только PackMirrors и порядок сохранялся.
        var effectiveMirrors = new List<string>();
        if (!string.IsNullOrWhiteSpace(packBase))
            effectiveMirrors.Add(packBase);

        effectiveMirrors.AddRange(mirrors);

        var finalMirrors = effectiveMirrors
            .Select(NormalizeAbsoluteBaseUrl)
            .Where(u => !string.IsNullOrWhiteSpace(u))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return s with
        {
            MinecraftVersion = mc,
            Loader = new LoaderInfo { Type = loaderType, Version = loaderVer, InstallerUrl = installerUrl },
            ClientVersionId = (s.ClientVersionId ?? "").Trim(),
            PackBaseUrl = packBase,
            PackMirrors = finalMirrors
        };
    }

    private static string NormalizeAbsoluteUrl(string? url)
    {
        url = (url ?? "").Trim();
        if (string.IsNullOrWhiteSpace(url)) return "";

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return "";

        if (!uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
            !uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
            return "";

        return uri.ToString();
    }

    private static string NormalizeAbsoluteBaseUrl(string? url)
    {
        url = NormalizeAbsoluteUrl(url);
        if (string.IsNullOrWhiteSpace(url)) return "";
        if (!url.EndsWith("/")) url += "/";
        return url;
    }

    private static HttpClient CreateHttp()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            Proxy = WebRequest.DefaultWebProxy,
            UseProxy = true,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            AllowAutoRedirect = true,
            MaxConnectionsPerServer = 8
        };

        var http = new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan // таймауты per-request
        };

        http.DefaultRequestHeaders.UserAgent.ParseAdd(LauncherIdentity.UserAgent);
        return http;
    }

    // =========================
    // DTO
    // =========================

    public sealed record ServersRoot(
        [property: JsonPropertyName("version")] int Version,
        [property: JsonPropertyName("servers")] List<ServerInfo> Servers);

    public sealed record LoaderInfo
    {
        [JsonPropertyName("type")] public string Type { get; init; } = "vanilla";
        [JsonPropertyName("version")] public string Version { get; init; } = "";
        [JsonPropertyName("installerUrl")] public string InstallerUrl { get; init; } = "";
    }

    public sealed record ServerInfo
    {
        [JsonPropertyName("id")] public string Id { get; init; } = "";
        [JsonPropertyName("name")] public string Name { get; init; } = "";
        [JsonPropertyName("address")] public string Address { get; init; } = "";

        [JsonPropertyName("minecraftVersion")] public string MinecraftVersion { get; init; } = "1.21.1";

        [JsonPropertyName("loader")] public LoaderInfo? Loader { get; init; } = null;

        // legacy
        [JsonPropertyName("loaderName")] public string? LoaderName { get; init; } = null;
        [JsonPropertyName("loaderVersion")] public string? LoaderVersion { get; init; } = null;

        [JsonPropertyName("clientVersionId")] public string? ClientVersionId { get; init; } = null;

        [JsonPropertyName("packBaseUrl")] public string PackBaseUrl { get; init; } = "";
        [JsonPropertyName("packMirrors")] public string[]? PackMirrors { get; init; } = Array.Empty<string>();
        [JsonPropertyName("syncPack")] public bool SyncPack { get; init; } = false;
    }
}

[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    AllowTrailingCommas = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    WriteIndented = true)]
[JsonSerializable(typeof(ServerListService.ServersRoot))]
[JsonSerializable(typeof(ServerListService.ServerInfo))]
[JsonSerializable(typeof(ServerListService.LoaderInfo))]
internal partial class ServerListJsonContext : JsonSerializerContext
{
}
