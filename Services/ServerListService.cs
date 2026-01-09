using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace LegendBorn.Services;

public sealed class ServerListService
{
    public const string DefaultServersUrl = "https://legendborn.ru/launcher/servers.json";

    private static readonly HttpClient _http = CreateHttp();

    public async Task<IReadOnlyList<ServerInfo>> GetServersAsync(
        IEnumerable<string>? mirrors = null,
        CancellationToken ct = default)
    {
        var urls = (mirrors ?? new[] { DefaultServersUrl })
            .Select(u => (u ?? "").Trim())
            .Where(u => !string.IsNullOrWhiteSpace(u))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (urls.Length == 0)
            throw new InvalidOperationException("Нет URL для servers.json");

        Exception? last = null;

        foreach (var url in urls)
        {
            try
            {
                using var reqCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                reqCts.CancelAfter(TimeSpan.FromSeconds(10));

                using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, reqCts.Token);
                resp.EnsureSuccessStatusCode();

                await using var stream = await resp.Content.ReadAsStreamAsync(reqCts.Token);
                var root = await JsonSerializer.DeserializeAsync(
                    stream,
                    ServerListJsonContext.Default.ServersRoot,
                    reqCts.Token);

                if (root is null || root.Servers is null || root.Servers.Count == 0)
                    throw new InvalidOperationException("servers.json: пустой список servers");

                // нормализуем и выкидываем мусор
                var result = root.Servers
                    .Select(s =>
                    {
                        try { return NormalizeServer(s); }
                        catch { return null; } // один битый сервер не должен убивать весь список
                    })
                    .Where(s => s is not null)
                    .Cast<ServerInfo>()
                    .ToList();

                if (result.Count == 0)
                    throw new InvalidOperationException("servers.json: после нормализации серверов не осталось");

                return result;
            }
            catch (Exception ex)
            {
                last = ex;
            }
        }

        throw new InvalidOperationException("Не удалось загрузить servers.json ни с одного URL", last);
    }

    public async Task<IReadOnlyList<ServerInfo>> GetServersOrDefaultAsync(
        IEnumerable<string>? mirrors = null,
        CancellationToken ct = default)
    {
        try
        {
            return await GetServersAsync(mirrors, ct);
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
                    PackMirrors = Array.Empty<string>(),
                    SyncPack = true
                }
            };
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

        // loader: новый формат (loader{}) или старый (loaderName/loaderVersion)
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

        // если installerUrl не задан (старый json) — подставим дефолт для forge/neoforge
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

        // если не vanilla — installerUrl обязателен
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
        var http = new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All
        });

        http.Timeout = TimeSpan.FromSeconds(30);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("LegendBornLauncher/1.0");
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

        // NEW format
        [JsonPropertyName("loader")] public LoaderInfo? Loader { get; init; } = null;

        // LEGACY format
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
