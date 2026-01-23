using System;
using System.Collections.Generic;
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

public sealed class ServerListService
{
    // ==========================================================
    // Launcher mirrors (servers.json / pack)
    // ==========================================================

    // CloudBucket (custom domain) — PRIMARY
    public const string CloudBucketLauncherBaseUrl = "https://pack.legendborn.ru/launcher/";

    // Selectel S3 — FALLBACK (у нас он уже есть).
    // Дефолт берём из панели "Основной домен" бакета + "/launcher/".
    // Можно переопределить без пересборки:
    // 1) ENV: LEGENDBORN_SELECTEL_LAUNCHER_BASE_URL
    // 2) Файл: <CacheDir>/selectel_launcher_base.txt (одна строка - base url)
    //
    // Пример дефолта (как на твоём скрине):
    // https://612cd759-4c9d-450e-bc91-a51d3c56e834.selstorage.ru/launcher/
    public const string SelectelLauncherBaseUrlDefault =
        "https://612cd759-4c9d-450e-bc91-a51d3c56e834.selstorage.ru/launcher/";

    private const string EnvSelectelBase = "LEGENDBORN_SELECTEL_LAUNCHER_BASE_URL";

    // SourceForge — MIRROR (стабильный CDN)
    public const string SourceForgeLauncherBaseUrl =
        "https://master.dl.sourceforge.net/project/legendborn-pack/launcher/";

    // Чтобы не перечитывать файл/ENV каждый вызов
    private static readonly Lazy<string> _selectelBaseLazy = new(ResolveSelectelLauncherBaseUrl);
    public static string SelectelLauncherBaseUrl => _selectelBaseLazy.Value;

    public static string CloudBucketServersUrl => CombineUrl(CloudBucketLauncherBaseUrl, "servers.json");
    public static string SelectelServersUrl => CombineUrl(SelectelLauncherBaseUrl, "servers.json");
    public static string SourceForgeServersUrl => CombineUrl(SourceForgeLauncherBaseUrl, "servers.json");

    /// <summary>
    /// Базовые зеркала servers.json.
    /// Primary — CloudBucket.
    /// Далее: Selectel и SourceForge.
    /// Можно передавать свои зеркала в GetServersAsync — они добавятся к этому списку.
    /// </summary>
    public static string[] DefaultServersMirrors =>
        new[] { CloudBucketServersUrl, SelectelServersUrl, SourceForgeServersUrl }
            .Select(NormalizeAbsoluteUrl)
            .Where(u => !string.IsNullOrWhiteSpace(u))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    // ==========================================================
    // Timeouts / limits
    // ==========================================================

    private const int PrimaryTimeoutSec1 = 6;
    private const int PrimaryTimeoutSec2 = 12;
    private const int FallbackTimeoutSec = 16;

    private const long MaxServersJsonBytes = 2 * 1024 * 1024;

    // ==========================================================
    // HTTP + Cache
    // ==========================================================

    private static readonly HttpClient SharedHttp = CreateHttp();

    private readonly HttpClient _http;
    private readonly Action<string>? _log;

    private static readonly string CacheFilePath =
        Path.Combine(LauncherPaths.CacheDir, "servers_cache.json");

    private static readonly string CacheMetaPath =
        Path.Combine(LauncherPaths.CacheDir, "servers_cache.meta.json");

    private static readonly string SelectelBaseOverridePath =
        Path.Combine(LauncherPaths.CacheDir, "selectel_launcher_base.txt");

    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        WriteIndented = true
    };

    // Не даём параллельным вызовам одновременно качать servers.json (особенно при старте UI).
    private static readonly SemaphoreSlim _gate = new(1, 1);

    public ServerListService(HttpClient? http = null, Action<string>? log = null)
    {
        _http = http ?? SharedHttp;
        _log = log;
    }

    // ==========================================================
    // Public API
    // ==========================================================

    public async Task<IReadOnlyList<ServerInfo>> GetServersAsync(
        IEnumerable<string>? mirrors = null,
        CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var urls = MergeAndNormalizeUrls(mirrors);

            if (urls.Length == 0)
                throw new InvalidOperationException("Нет URL для servers.json");

            // Primary: CloudBucket (если есть), иначе первый из списка
            var primary =
                urls.FirstOrDefault(u => u.Contains("pack.legendborn.ru", StringComparison.OrdinalIgnoreCase))
                ?? urls[0];

            _log?.Invoke($"servers.json: primary = {primary}");

            // meta для conditional requests (ETag / Last-Modified)
            var cached = TryLoadCache(out var cacheMeta);

            // primary: 2 попытки (часто первый коннект может быть “холодным”)
            var primaryRes =
                await TryFetchServersFromUrlAsync(primary, cacheMeta, ct, TimeSpan.FromSeconds(PrimaryTimeoutSec1)).ConfigureAwait(false)
                ?? await TryFetchServersFromUrlAsync(primary, cacheMeta, ct, TimeSpan.FromSeconds(PrimaryTimeoutSec2)).ConfigureAwait(false);

            if (primaryRes is not null)
            {
                SaveCacheQuiet(primaryRes.Value.Json, primaryRes.Value.Meta);
                return primaryRes.Value.Servers;
            }

            var fallbacks = urls
                .Where(u => !u.Equals(primary, StringComparison.OrdinalIgnoreCase))
                .ToArray();

            if (fallbacks.Length == 0)
            {
                if (cached is not null && cached.Count > 0)
                    return cached;

                throw new InvalidOperationException("Primary зеркало упало, fallback'ов нет, кеш пуст.");
            }

            // Фолбэки — race по скорости: запускаем все и берём самый быстрый успешный.
            fallbacks = OrderFallbacks(fallbacks);

            _log?.Invoke("servers.json: fallbacks = " + string.Join(" | ", fallbacks));

            using var raceCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

            var tasks = new List<Task<(string Json, IReadOnlyList<ServerInfo> Servers, ServersCacheMeta? Meta)?>>(fallbacks.Length);
            foreach (var u in fallbacks)
                tasks.Add(TryFetchServersFromUrlAsync(u, cacheMeta, raceCts.Token, TimeSpan.FromSeconds(FallbackTimeoutSec)));

            while (tasks.Count > 0)
            {
                var finished = await Task.WhenAny(tasks).ConfigureAwait(false);
                tasks.Remove(finished);

                (string Json, IReadOnlyList<ServerInfo> Servers, ServersCacheMeta? Meta)? res = null;
                try
                {
                    res = await finished.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (raceCts.IsCancellationRequested && !ct.IsCancellationRequested)
                {
                }
                catch
                {
                }

                if (res is not null)
                {
                    raceCts.Cancel();
                    try { await Task.WhenAll(tasks).ConfigureAwait(false); } catch { }

                    SaveCacheQuiet(res.Value.Json, res.Value.Meta);
                    return res.Value.Servers;
                }
            }

            // Онлайн не получилось — отдаём кеш
            if (cached is not null && cached.Count > 0)
                return cached;

            throw new InvalidOperationException("Не удалось загрузить servers.json ни с одного URL (cloudbucket + fallbacks).");
        }
        finally
        {
            try { _gate.Release(); } catch { }
        }
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
            // максимально “живучий” дефолт на случай полного оффлайна
            var cloudPack = NormalizeAbsoluteBaseUrl(CombineUrl(CloudBucketLauncherBaseUrl, "pack/"));
            var selPack = NormalizeAbsoluteBaseUrl(CombineUrl(SelectelLauncherBaseUrl, "pack/"));
            var sfPack = NormalizeAbsoluteBaseUrl(CombineUrl(SourceForgeLauncherBaseUrl, "pack/"));

            var packMirrors = new[] { cloudPack, selPack, sfPack }
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return new[]
            {
                new ServerInfo
                {
                    Id = "legendCraft",
                    Name = "LegendCraft",
                    Address = "legendcraft.minerent.io",
                    MinecraftVersion = "1.21.1",
                    Loader = new LoaderInfo
                    {
                        Type = "neoforge",
                        Version = "21.1.216",
                        // Официальная ссылка (LoaderInstaller дальше может переписать на зеркало)
                        InstallerUrl = "https://maven.neoforged.net/releases/net/neoforged/neoforge/21.1.216/neoforge-21.1.216-installer.jar"
                    },
                    ClientVersionId = "LegendBorn",
                    PackBaseUrl = cloudPack,
                    PackMirrors = packMirrors,
                    SyncPack = true
                }
            };
        }
    }

    // ==========================================================
    // Fetch + parse
    // ==========================================================

    private async Task<(string Json, IReadOnlyList<ServerInfo> Servers, ServersCacheMeta? Meta)?> TryFetchServersFromUrlAsync(
        string url,
        ServersCacheMeta? cacheMeta,
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
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/plain")); // некоторые CDN отдают без json-типа
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));
            req.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
            TrySetUa(req);

            // Conditional GET: если кеш свежий — получим 304
            if (!string.IsNullOrWhiteSpace(cacheMeta?.ETag))
                req.Headers.TryAddWithoutValidation("If-None-Match", cacheMeta!.ETag);

            if (!string.IsNullOrWhiteSpace(cacheMeta?.LastModified))
                req.Headers.TryAddWithoutValidation("If-Modified-Since", cacheMeta!.LastModified);

            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, reqCts.Token)
                .ConfigureAwait(false);

            // 304 — возвращаем кеш, если он есть/валидный
            if (resp.StatusCode == HttpStatusCode.NotModified)
            {
                var cached = TryLoadCache(out _);
                if (cached is not null && cached.Count > 0)
                {
                    var meta304 = BuildCacheMetaFromResponse(resp, fallback: cacheMeta);
                    _log?.Invoke($"servers.json: 304 NotModified -> cache ({url})");
                    return ("", cached, meta304);
                }

                return null;
            }

            if (!resp.IsSuccessStatusCode)
                return null;

            var media = resp.Content.Headers.ContentType?.MediaType ?? "";
            if (media.Contains("text/html", StringComparison.OrdinalIgnoreCase))
                return null;

            var len = resp.Content.Headers.ContentLength;
            if (len.HasValue && len.Value > MaxServersJsonBytes)
                return null;

            await using var stream = await resp.Content.ReadAsStreamAsync(reqCts.Token).ConfigureAwait(false);

            var json = await ReadUtf8LimitedAsync(stream, MaxServersJsonBytes, reqCts.Token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(json))
                return null;

            var trimmed = json.TrimStart();

            // HTML/страница ошибки
            if (trimmed.StartsWith("<", StringComparison.Ordinal))
                return null;

            var root = JsonSerializer.Deserialize(json, ServerListJsonContext.Default.ServersRoot);
            if (root is null || root.Servers is null || root.Servers.Count == 0)
                return null;

            var result = new List<ServerInfo>(root.Servers.Count);
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var s in root.Servers)
            {
                var ns = NormalizeServer(s);
                if (ns is null)
                    continue;

                if (!ids.Add(ns.Id))
                    continue;

                result.Add(ns);
            }

            if (result.Count == 0)
                return null;

            var meta = BuildCacheMetaFromResponse(resp, fallback: null);

            _log?.Invoke($"servers.json: OK ({url}) servers={result.Count}");
            return (json, result, meta);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log?.Invoke($"servers.json: fail {url} — {ex.Message}");
            return null;
        }
    }

    private static ServersCacheMeta BuildCacheMetaFromResponse(HttpResponseMessage resp, ServersCacheMeta? fallback)
    {
        string? lastModified = resp.Content.Headers.LastModified?.ToString();

        if (string.IsNullOrWhiteSpace(lastModified) &&
            resp.Headers.TryGetValues("Last-Modified", out var lmVals))
        {
            lastModified = lmVals.FirstOrDefault();
        }

        return new ServersCacheMeta
        {
            SavedUtcUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ETag = resp.Headers.ETag?.ToString() ?? fallback?.ETag,
            LastModified = lastModified ?? fallback?.LastModified
        };
    }

    private static async Task<string> ReadUtf8LimitedAsync(Stream stream, long maxBytes, CancellationToken ct)
    {
        using var ms = new MemoryStream(capacity: (int)Math.Min(64 * 1024, maxBytes));
        var buffer = new byte[32 * 1024];

        int read;
        long total = 0;

        while ((read = await stream.ReadAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > maxBytes)
                return "";

            ms.Write(buffer, 0, read);
        }

        return Encoding.UTF8.GetString(ms.ToArray());
    }

    // ==========================================================
    // Normalization / ordering
    // ==========================================================

    private static string[] MergeAndNormalizeUrls(IEnumerable<string>? extraMirrors)
    {
        var urls = new List<string>(DefaultServersMirrors.Length + 8);
        urls.AddRange(DefaultServersMirrors);

        if (extraMirrors is not null)
            urls.AddRange(extraMirrors);

        return urls
            .Select(NormalizeAbsoluteUrl)
            .Where(u => !string.IsNullOrWhiteSpace(u))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool LooksLikeCloudBucket(string url)
    {
        url = (url ?? "").ToLowerInvariant();
        return url.Contains("pack.legendborn.ru");
    }

    private static bool LooksLikeSelectel(string url)
    {
        url = (url ?? "").ToLowerInvariant();
        return url.Contains("selstorage.ru") ||
               url.Contains("selcdn") ||
               url.Contains("s3.ru-") ||
               url.Contains("storage.selcloud.ru") ||
               url.Contains("selectel");
    }

    private static bool LooksLikeSourceForge(string url)
    {
        url = (url ?? "").ToLowerInvariant();
        return url.Contains("master.dl.sourceforge.net") ||
               url.Contains("sourceforge.net") ||
               url.Contains("downloads.sourceforge.net");
    }

    private static string[] OrderFallbacks(string[] fallbacks)
    {
        // Порядок тут вторичен (race по скорости), но сделаем детерминированно:
        // Selectel -> SourceForge CDN -> прочие.
        return fallbacks
            .Select(NormalizeAbsoluteUrl)
            .Where(u => !string.IsNullOrWhiteSpace(u))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(u =>
            {
                if (LooksLikeSelectel(u)) return 0;
                if (u.Contains("master.dl.sourceforge.net", StringComparison.OrdinalIgnoreCase)) return 1;
                if (LooksLikeSourceForge(u)) return 2;
                if (LooksLikeCloudBucket(u)) return 3; // на случай если CloudBucket попал в fallbacks
                return 4;
            })
            .ToArray();
    }

    private static ServerInfo? NormalizeServer(ServerInfo s)
    {
        var id = (s.Id ?? "").Trim();
        var name = (s.Name ?? "").Trim();
        var address = (s.Address ?? "").Trim();

        if (string.IsNullOrWhiteSpace(id)) return null;
        if (string.IsNullOrWhiteSpace(name)) return null;
        if (string.IsNullOrWhiteSpace(address)) return null;

        var mc = (s.MinecraftVersion ?? "").Trim();
        if (string.IsNullOrWhiteSpace(mc)) return null;

        var loader = s.Loader;

        // legacy fields fallback
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
        if (string.IsNullOrWhiteSpace(loaderType))
            loaderType = "vanilla";

        if (loaderType != "vanilla" && loaderType != "neoforge")
            return null;

        var loaderVer = (loader.Version ?? "").Trim();
        var installerUrl = (loader.InstallerUrl ?? "").Trim();

        if (loaderType == "neoforge")
        {
            if (string.IsNullOrWhiteSpace(loaderVer))
                return null;

            // дефолтная офф. ссылка, если не задана
            if (string.IsNullOrWhiteSpace(installerUrl))
                installerUrl = $"https://maven.neoforged.net/releases/net/neoforged/neoforge/{loaderVer}/neoforge-{loaderVer}-installer.jar";

            if (string.IsNullOrWhiteSpace(NormalizeAbsoluteUrl(installerUrl)))
                return null;
        }
        else
        {
            loaderVer = "";
            installerUrl = "";
        }

        // pack urls
        var packBase = NormalizeAbsoluteBaseUrl(s.PackBaseUrl);

        var mirrors = (s.PackMirrors ?? Array.Empty<string>())
            .Select(NormalizeAbsoluteBaseUrl)
            .Where(u => !string.IsNullOrWhiteSpace(u))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        // packBaseUrl всегда тоже считаем зеркалом
        var effective = new List<string>(mirrors.Length + 1);
        if (!string.IsNullOrWhiteSpace(packBase))
            effective.Add(packBase);
        effective.AddRange(mirrors);

        var finalMirrors = effective
            .Select(NormalizeAbsoluteBaseUrl)
            .Where(u => !string.IsNullOrWhiteSpace(u))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(u =>
            {
                // Для pack: CloudBucket (primary) -> Selectel -> SourceForge -> остальное
                if (LooksLikeCloudBucket(u)) return 0;
                if (LooksLikeSelectel(u)) return 1;
                if (u.Contains("master.dl.sourceforge.net", StringComparison.OrdinalIgnoreCase)) return 2;
                if (LooksLikeSourceForge(u)) return 3;
                return 4;
            })
            .ToArray();

        var clientVid = (s.ClientVersionId ?? "").Trim();

        return s with
        {
            Id = id,
            Name = name,
            Address = address,
            MinecraftVersion = mc,
            Loader = new LoaderInfo { Type = loaderType, Version = loaderVer, InstallerUrl = installerUrl },
            ClientVersionId = string.IsNullOrWhiteSpace(clientVid) ? null : clientVid,
            PackBaseUrl = packBase,
            PackMirrors = finalMirrors
        };
    }

    private static string CombineUrl(string baseUrl, string relative)
    {
        baseUrl = (baseUrl ?? "").Trim();
        if (string.IsNullOrWhiteSpace(baseUrl)) return "";

        relative = (relative ?? "").TrimStart('/');

        if (!baseUrl.EndsWith("/"))
            baseUrl += "/";

        return baseUrl + relative;
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

        // вычищаем query/fragment чтобы не плодить разные кеш-ключи
        var b = new UriBuilder(uri) { Query = "", Fragment = "" };
        return b.Uri.ToString();
    }

    private static string NormalizeAbsoluteBaseUrl(string? url)
    {
        url = NormalizeAbsoluteUrl(url);
        if (string.IsNullOrWhiteSpace(url)) return "";
        if (!url.EndsWith("/")) url += "/";
        return url;
    }

    private static void TrySetUa(HttpRequestMessage req)
    {
        try
        {
            var ua = LauncherIdentity.UserAgent;
            req.Headers.UserAgent.Clear();

            if (!string.IsNullOrWhiteSpace(ua))
                req.Headers.UserAgent.ParseAdd(ua);
            else
                req.Headers.UserAgent.ParseAdd($"LegendBornLauncher/{LauncherIdentity.InformationalVersion}");
        }
        catch
        {
            // ignore
        }
    }

    private static string ResolveSelectelLauncherBaseUrl()
    {
        // 1) ENV override
        try
        {
            var env = Environment.GetEnvironmentVariable(EnvSelectelBase);
            var envNorm = NormalizeAbsoluteBaseUrl(env);
            if (!string.IsNullOrWhiteSpace(envNorm))
                return envNorm;
        }
        catch { }

        // 2) file override
        try
        {
            if (File.Exists(SelectelBaseOverridePath))
            {
                var txt = File.ReadAllText(SelectelBaseOverridePath, Utf8NoBom);
                var fileNorm = NormalizeAbsoluteBaseUrl(txt);
                if (!string.IsNullOrWhiteSpace(fileNorm))
                    return fileNorm;
            }
        }
        catch { }

        // 3) default (настроен)
        return NormalizeAbsoluteBaseUrl(SelectelLauncherBaseUrlDefault);
    }

    // ==========================================================
    // Cache
    // ==========================================================

    private sealed class ServersCacheMeta
    {
        [JsonPropertyName("savedUtcUnix")] public long SavedUtcUnix { get; set; }
        [JsonPropertyName("etag")] public string? ETag { get; set; }
        [JsonPropertyName("lastModified")] public string? LastModified { get; set; }
    }

    private static void SaveCacheQuiet(string json, ServersCacheMeta? meta)
    {
        // Если пришёл 304 и json пустой — просто обновим мету (если есть),
        // а сам кеш оставим как есть.
        try
        {
            LauncherPaths.EnsureDir(LauncherPaths.CacheDir);

            if (!string.IsNullOrWhiteSpace(json))
            {
                var tmp = CacheFilePath + ".tmp";
                File.WriteAllText(tmp, json, Utf8NoBom);
                ReplaceOrMoveAtomic(tmp, CacheFilePath);
                TryDeleteQuiet(tmp);
            }

            if (meta is not null)
            {
                var metaJson = JsonSerializer.Serialize(meta, JsonOptions);
                var tmpMeta = CacheMetaPath + ".tmp";
                File.WriteAllText(tmpMeta, metaJson, Utf8NoBom);
                ReplaceOrMoveAtomic(tmpMeta, CacheMetaPath);
                TryDeleteQuiet(tmpMeta);
            }
        }
        catch { }
    }

    private static IReadOnlyList<ServerInfo>? TryLoadCache(out ServersCacheMeta? meta)
    {
        meta = null;

        try
        {
            if (File.Exists(CacheMetaPath))
            {
                try
                {
                    var mj = File.ReadAllText(CacheMetaPath, Utf8NoBom);
                    meta = JsonSerializer.Deserialize<ServersCacheMeta>(mj, JsonOptions);
                }
                catch { meta = null; }
            }

            if (!File.Exists(CacheFilePath))
                return null;

            var json = File.ReadAllText(CacheFilePath, Utf8NoBom);
            if (string.IsNullOrWhiteSpace(json))
                return null;

            var root = JsonSerializer.Deserialize(json, ServerListJsonContext.Default.ServersRoot);
            if (root is null || root.Servers is null || root.Servers.Count == 0)
                return null;

            var result = new List<ServerInfo>(root.Servers.Count);
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var s in root.Servers)
            {
                var ns = NormalizeServer(s);
                if (ns is null)
                    continue;

                if (!ids.Add(ns.Id))
                    continue;

                result.Add(ns);
            }

            return result.Count > 0 ? result : null;
        }
        catch
        {
            return null;
        }
    }

    // ==========================================================
    // HTTP
    // ==========================================================

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
            Timeout = Timeout.InfiniteTimeSpan
        };

        try
        {
            var ua = LauncherIdentity.UserAgent;
            if (!string.IsNullOrWhiteSpace(ua))
                http.DefaultRequestHeaders.UserAgent.ParseAdd(ua);
            else
                http.DefaultRequestHeaders.UserAgent.ParseAdd($"LegendBornLauncher/{LauncherIdentity.InformationalVersion}");
        }
        catch
        {
            try
            {
                http.DefaultRequestHeaders.UserAgent.Clear();
                http.DefaultRequestHeaders.UserAgent.ParseAdd($"LegendBornLauncher/{LauncherIdentity.InformationalVersion}");
            }
            catch { }
        }

        return http;
    }

    // ==========================================================
    // Atomic file ops
    // ==========================================================

    private static void ReplaceOrMoveAtomic(string sourceTmp, string destPath)
    {
        try
        {
            if (OperatingSystem.IsWindows() && File.Exists(destPath))
            {
                var backup = destPath + ".bak";
                try
                {
                    TryDeleteQuiet(backup);
                    File.Replace(sourceTmp, destPath, backup, ignoreMetadataErrors: true);
                }
                finally
                {
                    TryDeleteQuiet(backup);
                }
                return;
            }

            File.Move(sourceTmp, destPath, overwrite: true);
        }
        catch
        {
            // fallback
            try
            {
                if (File.Exists(destPath))
                    File.Delete(destPath);

                File.Move(sourceTmp, destPath);
            }
            catch { }
        }
    }

    private static void TryDeleteQuiet(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    // ==========================================================
    // DTOs
    // ==========================================================

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
