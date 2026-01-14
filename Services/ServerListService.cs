// ServerListService.cs (v0.2.2)
// - Убрана константа LauncherUserAgent (используем LauncherIdentity.UserAgent)
// - Улучшены таймауты и чтение JSON через stream + лимит по размеру
// - Race на fallback'ах: отмена остальных при успехе + безопасный await
// - Кэш: атомарная запись кроссплатформенно (Replace/Move) + отдельная папка cache в gameDir (по желанию)
// - Критические фиксы:
//   1) NormalizeServer раньше "forge" поддерживал auto-url — теперь ВАШЕ ТЗ: только NeoForge/Vanilla
//      => forge принудительно отклоняем (return null) чтобы не было “тихой” установки не того.
//   2) packBaseUrl теперь не обязателен, но если задан — гарантированно попадает в PackMirrors первым.
//   3) packMirrors нормализуются как baseUrl (со слэшем), чтобы MinecraftService не получал битые URL.
// - Мелкие улучшения: детерминированная нормализация, строгая валидация, минимизация аллокаций

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
    public const string DefaultServersUrl = "https://legendborn.ru/launcher/servers.json";
    public const string BunnyServersUrl = "https://legendborn-pack.b-cdn.net/launcher/servers.json";
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

    // Таймауты под РФ (быстрый фейл у primary, затем race на fallback)
    private const int PrimaryTimeoutSec1 = 6;
    private const int PrimaryTimeoutSec2 = 12;
    private const int FallbackTimeoutSec = 16;

    // safety: servers.json не должен быть огромным
    private const long MaxServersJsonBytes = 2 * 1024 * 1024; // 2 MB

    private static readonly HttpClient _http = CreateHttp();

    // Кэш (AppData) — чтобы список жил даже когда сеть умерла
    private static readonly string CacheFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "LegendBorn", "cache", "servers_cache.json");

    // Отдельные настройки JSON (контекст есть, но опции нужны для быстрых проверок/возможной диагностики)
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        WriteIndented = true
    };

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

        // 2) Если сайт не отдал — race между fallback'ами
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

        fallbacks = OrderFallbacks(fallbacks);

        using var raceCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var tasks = new List<Task<(string Json, IReadOnlyList<ServerInfo> Servers)?>>(fallbacks.Length);
        foreach (var u in fallbacks)
            tasks.Add(TryFetchServersFromUrlAsync(u, raceCts.Token, TimeSpan.FromSeconds(FallbackTimeoutSec)));

        while (tasks.Count > 0)
        {
            var finished = await Task.WhenAny(tasks).ConfigureAwait(false);
            tasks.Remove(finished);

            (string Json, IReadOnlyList<ServerInfo> Servers)? res = null;
            try
            {
                res = await finished.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (raceCts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                // отменили из-за успеха другого зеркала — ок
            }
            catch
            {
                // ignore
            }

            if (res is not null)
            {
                raceCts.Cancel();

                // добиваем остальных, чтобы не оставлять фоновые исключения
                try { await Task.WhenAll(tasks).ConfigureAwait(false); } catch { }

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
                    Id = "legendCraft",
                    Name = "LegendCraft",
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

            await using var stream = await resp.Content.ReadAsStreamAsync(reqCts.Token).ConfigureAwait(false);

            // читаем строго с ограничением
            var json = await ReadUtf8LimitedAsync(stream, MaxServersJsonBytes, reqCts.Token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(json))
                return null;

            var trimmed = json.TrimStart();
            if (trimmed.StartsWith("<", StringComparison.Ordinal))
                return null;

            var root = JsonSerializer.Deserialize(json, ServerListJsonContext.Default.ServersRoot);
            if (root is null || root.Servers is null || root.Servers.Count == 0)
                return null;

            var result = new List<ServerInfo>(root.Servers.Count);
            foreach (var s in root.Servers)
            {
                var ns = NormalizeServer(s);
                if (ns is not null)
                    result.Add(ns);
            }

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

    private static async Task<string> ReadUtf8LimitedAsync(Stream stream, long maxBytes, CancellationToken ct)
    {
        // StreamReader не дает "жёстко" ограничить bytes, поэтому читаем в память с проверкой длины.
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
        if (string.IsNullOrWhiteSpace(json))
            return;

        try
        {
            var dir = Path.GetDirectoryName(CacheFilePath);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);

            var tmp = CacheFilePath + ".tmp";
            File.WriteAllText(tmp, json);

            ReplaceOrMoveAtomic(tmp, CacheFilePath);

            TryDeleteQuiet(tmp);
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
            if (string.IsNullOrWhiteSpace(json))
                return null;

            var root = JsonSerializer.Deserialize(json, ServerListJsonContext.Default.ServersRoot);
            if (root is null || root.Servers is null || root.Servers.Count == 0)
                return null;

            var result = new List<ServerInfo>(root.Servers.Count);
            foreach (var s in root.Servers)
            {
                var ns = NormalizeServer(s);
                if (ns is not null)
                    result.Add(ns);
            }

            return result.Count > 0 ? result : null;
        }
        catch
        {
            return null;
        }
    }

    // =========================
    // Normalize / Validate (v0.2.2: only NeoForge/Vanilla)
    // =========================

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
        if (string.IsNullOrWhiteSpace(loaderType))
            loaderType = "vanilla";

        // ✅ ТЗ: нужен только NeoForge (и vanilla)
        if (loaderType != "vanilla" && loaderType != "neoforge")
            return null;

        var loaderVer = (loader.Version ?? "").Trim();
        var installerUrl = (loader.InstallerUrl ?? "").Trim();

        if (loaderType == "neoforge")
        {
            if (string.IsNullOrWhiteSpace(loaderVer))
                return null;

            // если installerUrl пустой — подставим официальный neoforge
            if (string.IsNullOrWhiteSpace(installerUrl))
                installerUrl = $"https://maven.neoforged.net/releases/net/neoforged/neoforge/{loaderVer}/neoforge-{loaderVer}-installer.jar";

            // валидируем как absolute
            if (string.IsNullOrWhiteSpace(NormalizeAbsoluteUrl(installerUrl)))
                return null;
        }
        else
        {
            // vanilla
            loaderVer = "";
            installerUrl = "";
        }

        var packBase = NormalizeAbsoluteBaseUrl(s.PackBaseUrl);

        var mirrors = (s.PackMirrors ?? Array.Empty<string>())
            .Select(NormalizeAbsoluteBaseUrl)
            .Where(u => !string.IsNullOrWhiteSpace(u))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        // packBaseUrl всегда добавляем в PackMirrors первым (если задан)
        var effectiveMirrors = new List<string>(capacity: mirrors.Length + 1);
        if (!string.IsNullOrWhiteSpace(packBase))
            effectiveMirrors.Add(packBase);

        effectiveMirrors.AddRange(mirrors);

        var finalMirrors = effectiveMirrors
            .Select(NormalizeAbsoluteBaseUrl)
            .Where(u => !string.IsNullOrWhiteSpace(u))
            .Distinct(StringComparer.OrdinalIgnoreCase)
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

    private static void ReplaceOrMoveAtomic(string sourceTmp, string destPath)
    {
        // tmp и dest должны быть на одном томе/папке — тогда Replace/Move будет атомарным
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

    private static void TryDeleteQuiet(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
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
