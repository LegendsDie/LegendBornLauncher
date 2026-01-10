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

    // ✅ 0.1.8: если позже зальёшь servers.json на SourceForge — лаунчер начнёт брать оттуда автоматически
    public static readonly string[] DefaultServersMirrors =
    {
        DefaultServersUrl,
        "https://master.dl.sourceforge.net/project/legendborn-pack/launcher/servers.json",
        "https://downloads.sourceforge.net/project/legendborn-pack/launcher/servers.json"
    };

    private const string LauncherUserAgent = "LegendBornLauncher/0.1.8";

    private static readonly HttpClient _http = CreateHttp();

    private static readonly string CacheFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "LegendBorn", "cache", "servers_cache.json");

    public async Task<IReadOnlyList<ServerInfo>> GetServersAsync(
        IEnumerable<string>? mirrors = null,
        CancellationToken ct = default)
    {
        var urls = (mirrors ?? DefaultServersMirrors)
            .Select(u => (u ?? "").Trim())
            .Where(u => !string.IsNullOrWhiteSpace(u))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (urls.Length == 0)
            throw new InvalidOperationException("Нет URL для servers.json");

        Exception? last = null;

        foreach (var url in urls)
        {
            // 0.1.8: мягкий ретрай на зеркало
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    // Для проблемных доменов не ждём вечность
                    var isLegendborn = url.Contains("legendborn.ru", StringComparison.OrdinalIgnoreCase);
                    var perTryTimeout = isLegendborn
                        ? TimeSpan.FromSeconds(attempt == 1 ? 12 : 18)
                        : TimeSpan.FromSeconds(attempt == 1 ? 25 : 40);

                    using var reqCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    reqCts.CancelAfter(perTryTimeout);

                    using var req = new HttpRequestMessage(HttpMethod.Get, url);
                    req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                    req.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };

                    using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, reqCts.Token)
                        .ConfigureAwait(false);

                    resp.EnsureSuccessStatusCode();

                    var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    reqCts.Token.ThrowIfCancellationRequested();

                    // 0.1.8: защита от HTML (иногда некоторые зеркала/прокси отдают страницу вместо файла)
                    var trimmed = (json ?? "").TrimStart();
                    if (trimmed.StartsWith("<", StringComparison.Ordinal))
                        throw new InvalidOperationException("servers.json: получен HTML вместо JSON (зеркало вернуло страницу).");

                    var root = JsonSerializer.Deserialize(json, ServerListJsonContext.Default.ServersRoot);
                    if (root is null || root.Servers is null || root.Servers.Count == 0)
                        throw new InvalidOperationException("servers.json: пустой список servers");

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
                        throw new InvalidOperationException("servers.json: после нормализации серверов не осталось");

                    SaveCacheQuiet(json);
                    return result;
                }
                catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
                {
                    last = ex;
                    await Task.Delay(250 * attempt + Random.Shared.Next(0, 120), ct).ConfigureAwait(false);
                }
                catch (HttpRequestException ex)
                {
                    last = ex;
                    await Task.Delay(250 * attempt + Random.Shared.Next(0, 120), ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    last = ex;
                    break; // парс/формат — ретрай не поможет
                }
            }
        }

        // если сеть умерла — пытаемся подняться с кеша
        var cached = TryLoadCache();
        if (cached is not null && cached.Count > 0)
            return cached;

        throw new InvalidOperationException("Не удалось загрузить servers.json ни с одного URL", last);
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
            // fallback чтобы лаунчер не умер даже если сайт лежит
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
                        "https://master.dl.sourceforge.net/project/legendborn-pack/launcher/pack/",
                        "https://downloads.sourceforge.net/project/legendborn-pack/launcher/pack/"
                    },
                    SyncPack = true
                }
            };
        }
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

            if (File.Exists(CacheFilePath))
                File.Replace(tmp, CacheFilePath, destinationBackupFileName: null, ignoreMetadataErrors: true);
            else
                File.Move(tmp, CacheFilePath);
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

        // 0.1.8: если installerUrl пустой — подставим официальный
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

        var packBase = NormalizeBaseUrl(s.PackBaseUrl);

        var mirrors = (s.PackMirrors ?? Array.Empty<string>())
            .Select(NormalizeBaseUrl)
            .Where(u => !string.IsNullOrWhiteSpace(u))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return s with
        {
            MinecraftVersion = mc,
            Loader = new LoaderInfo { Type = loaderType, Version = loaderVer, InstallerUrl = installerUrl },
            ClientVersionId = (s.ClientVersionId ?? "").Trim(),
            PackBaseUrl = packBase,
            PackMirrors = mirrors
        };
    }

    private static string NormalizeBaseUrl(string? url)
    {
        url = (url ?? "").Trim();
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
            ConnectTimeout = TimeSpan.FromSeconds(12),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            AllowAutoRedirect = true,
            MaxConnectionsPerServer = 8
        };

        var http = new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan // таймауты делаем per-request
        };

        http.DefaultRequestHeaders.UserAgent.ParseAdd(LauncherUserAgent);
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

        [JsonPropertyName("loaderName")] public string? LoaderName { get; init; } = null;
        [JsonPropertyName("loaderVersion")] public string? LoaderVersion { get; init; } = null;

        [JsonPropertyName("clientVersionId")] public string? ClientVersionId { get; init; } = null;

        [JsonPropertyName("packBaseUrl")] public string PackBaseUrl { get; init; } = "";
        [JsonPropertyName("packMirrors")] public string[]? PackMirrors { get; init; } = Array.Empty<string>();
        [JsonPropertyName("syncPack")] public bool SyncPack { get; init; } = false;
    }
}

[JsonSerializable(typeof(ServerListService.ServersRoot))]
[JsonSerializable(typeof(ServerListService.ServerInfo))]
[JsonSerializable(typeof(ServerListService.LoaderInfo))]
internal partial class ServerListJsonContext : JsonSerializerContext
{
}
