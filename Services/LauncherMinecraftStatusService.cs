using System;
using System.Buffers;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using LegendBorn.Launching;

namespace LegendBorn.Services;

public sealed class LauncherMinecraftStatusService
{
    private const string SiteBaseUrl = "https://legendborn.xyz/";
    private const string StatusPath = "api/launcher/minecraft/status";
    private const int MaxResponseBytes = 256 * 1024;
    private static readonly HttpClient Http = CreateHttp();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    public sealed class StatusResponse
    {
        [JsonPropertyName("ok")] public bool Ok { get; set; }
        [JsonPropertyName("error")] public string? Error { get; set; }
        [JsonPropertyName("linked")] public bool Linked { get; set; }
        [JsonPropertyName("minecraftName")] public string? MinecraftName { get; set; }
        [JsonPropertyName("selectedSkin")] public SkinDto? SelectedSkin { get; set; }
        [JsonPropertyName("snapshot")] public SnapshotDto? Snapshot { get; set; }
    }

    public sealed class SkinDto
    {
        [JsonPropertyName("key")] public string? Key { get; set; }
        [JsonPropertyName("title")] public string? Title { get; set; }
        [JsonPropertyName("previewUrl")] public string? PreviewUrl { get; set; }
        [JsonPropertyName("skinUrl")] public string? SkinUrl { get; set; }
    }

    public sealed class SnapshotDto
    {
        [JsonPropertyName("capturedAt")] public DateTimeOffset? CapturedAt { get; set; }
        [JsonPropertyName("health")] public double? Health { get; set; }
        [JsonPropertyName("food")] public double? Food { get; set; }
        [JsonPropertyName("level")] public double? Level { get; set; }
        [JsonPropertyName("xp")] public double? Xp { get; set; }
        [JsonPropertyName("dimension")] public string? Dimension { get; set; }
        [JsonPropertyName("position")] public PositionDto? Position { get; set; }
        [JsonPropertyName("playTimeSeconds")] public double? PlayTimeSeconds { get; set; }
        [JsonPropertyName("deaths")] public JsonElement Deaths { get; set; }
        [JsonPropertyName("playerKills")] public JsonElement PlayerKills { get; set; }
        [JsonPropertyName("mobKills")] public JsonElement MobKills { get; set; }
        [JsonPropertyName("totalKills")] public JsonElement TotalKills { get; set; }
        [JsonPropertyName("kd")] public double? Kd { get; set; }
        [JsonPropertyName("jumps")] public JsonElement Jumps { get; set; }
        [JsonPropertyName("walkMeters")] public double? WalkMeters { get; set; }
        [JsonPropertyName("flyMeters")] public double? FlyMeters { get; set; }
    }

    public sealed class PositionDto
    {
        [JsonPropertyName("x")] public double? X { get; set; }
        [JsonPropertyName("y")] public double? Y { get; set; }
        [JsonPropertyName("z")] public double? Z { get; set; }
    }

    public async Task<StatusResponse> GetAsync(string accessToken, CancellationToken ct)
    {
        Exception? last = null;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(12));
                using var request = new HttpRequestMessage(HttpMethod.Get, StatusPath)
                {
                    Version = HttpVersion.Version20,
                    VersionPolicy = HttpVersionPolicy.RequestVersionOrLower
                };
                var token = (accessToken ?? string.Empty).Trim().Trim('"');
                if (token.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) token = token[7..].Trim();
                if (token.Length == 0) return new StatusResponse { Error = "EMPTY_TOKEN" };

                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
                request.Headers.TryAddWithoutValidation("X-Client", "LegendBornLauncher");
                request.Headers.TryAddWithoutValidation("X-Client-Version", LauncherIdentity.InformationalVersion ?? string.Empty);

                using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                    .ConfigureAwait(false);

                if (IsTransient(response.StatusCode) && attempt < 3)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(220 * attempt), ct).ConfigureAwait(false);
                    continue;
                }

                var body = await ReadBodyLimitedAsync(response, timeout.Token).ConfigureAwait(false);
                var dto = JsonSerializer.Deserialize<StatusResponse>(body, JsonOptions) ?? new StatusResponse();
                if (!response.IsSuccessStatusCode)
                {
                    dto.Ok = false;
                    dto.Error ??= $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}";
                }
                return dto;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (attempt < 3)
            {
                last = ex;
                await Task.Delay(TimeSpan.FromMilliseconds(220 * attempt), ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                last = ex;
            }
        }

        return new StatusResponse { Ok = false, Error = last?.Message ?? "STATUS_UNAVAILABLE" };
    }

    private static async Task<string> ReadBodyLimitedAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength is long length && length > MaxResponseBytes)
            throw new InvalidOperationException("Ответ статуса Minecraft слишком большой.");

        await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var buffer = ArrayPool<byte>.Shared.Rent(32 * 1024);
        try
        {
            using var output = new MemoryStream();
            while (true)
            {
                var read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false);
                if (read == 0) break;
                if (output.Length + read > MaxResponseBytes)
                    throw new InvalidOperationException("Ответ статуса Minecraft слишком большой.");
                output.Write(buffer, 0, read);
            }
            return Encoding.UTF8.GetString(output.GetBuffer(), 0, checked((int)output.Length));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static bool IsTransient(HttpStatusCode code)
        => (int)code >= 500 || code is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests;

    private static HttpClient CreateHttp()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            Proxy = WebRequest.DefaultWebProxy,
            UseProxy = true,
            ConnectTimeout = TimeSpan.FromSeconds(7),
            PooledConnectionLifetime = TimeSpan.FromMinutes(3),
            AllowAutoRedirect = false,
            MaxConnectionsPerServer = 4
        };
        var http = new HttpClient(handler)
        {
            BaseAddress = new Uri(SiteBaseUrl),
            Timeout = System.Threading.Timeout.InfiniteTimeSpan
        };
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        try { http.DefaultRequestHeaders.UserAgent.ParseAdd(LauncherIdentity.UserAgent ?? "LegendBornLauncher"); } catch { }
        return http;
    }
}
