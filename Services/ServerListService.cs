using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LegendBorn.Services;

/// <summary>
/// Compatibility container for server catalog DTOs.
///
/// Runtime server discovery is intentionally owned only by <see cref="ServerCatalogService"/>.
/// The old ServerListService network/cache/default-server implementation was retired because it
/// could re-introduce stale infrastructure, legacy loader versions, permissive HTTP endpoints and
/// a fail-open default server path. Keep this type only while existing ViewModel/catalog code still
/// references the nested DTO names.
/// </summary>
public sealed class ServerListService
{
    public sealed record ServersRoot(
        [property: JsonPropertyName("version")] int Version,
        [property: JsonPropertyName("servers")] List<ServerInfo> Servers);

    public sealed record LoaderInfo
    {
        [JsonPropertyName("type")]
        public string Type { get; init; } = "vanilla";

        [JsonPropertyName("version")]
        public string Version { get; init; } = "";

        [JsonPropertyName("installerUrl")]
        public string InstallerUrl { get; init; } = "";
    }

    public sealed record ServerInfo
    {
        [JsonPropertyName("id")]
        public string Id { get; init; } = "";

        [JsonPropertyName("name")]
        public string Name { get; init; } = "";

        [JsonPropertyName("address")]
        public string Address { get; init; } = "";

        [JsonPropertyName("minecraftVersion")]
        public string MinecraftVersion { get; init; } = "1.21.1";

        [JsonPropertyName("loader")]
        public LoaderInfo? Loader { get; init; }

        // Legacy response fields remain parseable during the API transition, but they are never
        // populated from a local fallback/default server anymore.
        [JsonPropertyName("loaderName")]
        public string? LoaderName { get; init; }

        [JsonPropertyName("loaderVersion")]
        public string? LoaderVersion { get; init; }

        [JsonPropertyName("clientVersionId")]
        public string? ClientVersionId { get; init; }

        [JsonPropertyName("packBaseUrl")]
        public string PackBaseUrl { get; init; } = "";

        [JsonPropertyName("packMirrors")]
        public string[]? PackMirrors { get; init; } = Array.Empty<string>();

        [JsonPropertyName("syncPack")]
        public bool SyncPack { get; init; }
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
