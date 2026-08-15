using System;
using System.Buffers;
using System.Collections.Generic;
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

/// <summary>
/// Typed client for profile-adjacent launcher APIs that are already exposed by legendbornweb.
/// Mutations intentionally use a single HTTP attempt so a transport retry can never duplicate
/// a state-changing action. Reads are bounded and may retry transient server failures.
/// </summary>
public sealed class LauncherProfileService
{
    private const string SiteBaseUrl = "https://legendborn.xyz/";
    private const int MaxResponseBytes = 512 * 1024;
    private static readonly HttpClient Http = CreateHttp();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    public class ApiResponse
    {
        [JsonPropertyName("ok")]
        public bool Ok { get; set; }

        [JsonPropertyName("error")]
        public string? Error { get; set; }

        [JsonPropertyName("message")]
        public string? Message { get; set; }
    }

    public sealed class ClanResponse : ApiResponse
    {
        [JsonPropertyName("clan")]
        public ClanDto? Clan { get; set; }
    }

    public sealed class ClanDto
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = "";

        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("tag")]
        public string? Tag { get; set; }

        [JsonPropertyName("avatarUrl")]
        public string? AvatarUrl { get; set; }

        [JsonPropertyName("treasury")]
        public long Treasury { get; set; }

        [JsonPropertyName("role")]
        public string? Role { get; set; }

        [JsonPropertyName("memberCount")]
        public int MemberCount { get; set; }

        [JsonPropertyName("createdAt")]
        public DateTimeOffset? CreatedAt { get; set; }
    }

    public sealed class ClanListResponse : ApiResponse
    {
        [JsonPropertyName("page")]
        public int Page { get; set; }

        [JsonPropertyName("pageSize")]
        public int PageSize { get; set; }

        [JsonPropertyName("total")]
        public int Total { get; set; }

        [JsonPropertyName("clans")]
        public ClanDto[] Clans { get; set; } = Array.Empty<ClanDto>();
    }

    public sealed class ClanMembersResponse : ApiResponse
    {
        [JsonPropertyName("clan")]
        public ClanDto? Clan { get; set; }

        [JsonPropertyName("members")]
        public ClanMemberDto[] Members { get; set; } = Array.Empty<ClanMemberDto>();
    }

    public sealed class ClanMemberDto
    {
        [JsonPropertyName("role")]
        public string Role { get; set; } = "MEMBER";

        [JsonPropertyName("joinedAt")]
        public DateTimeOffset? JoinedAt { get; set; }

        [JsonPropertyName("user")]
        public ClanMemberUserDto User { get; set; } = new();
    }

    public sealed class ClanMemberUserDto
    {
        [JsonPropertyName("publicId")]
        public int? PublicId { get; set; }

        [JsonPropertyName("nick")]
        public string? Nick { get; set; }

        [JsonPropertyName("avatarUrl")]
        public string? AvatarUrl { get; set; }

        [JsonPropertyName("presence")]
        public string? Presence { get; set; }

        [JsonPropertyName("presenceServerKey")]
        public string? PresenceServerKey { get; set; }

        [JsonPropertyName("presenceUpdatedAt")]
        public DateTimeOffset? PresenceUpdatedAt { get; set; }
    }

    public sealed class ProgressionResponse : ApiResponse
    {
        [JsonPropertyName("xpTotal")]
        public long XpTotal { get; set; }

        [JsonPropertyName("xpSeason")]
        public long XpSeason { get; set; }

        [JsonPropertyName("level")]
        public int Level { get; set; } = 1;

        [JsonPropertyName("xpIntoLevel")]
        public long XpIntoLevel { get; set; }

        [JsonPropertyName("xpForNext")]
        public long XpForNext { get; set; }

        [JsonPropertyName("xpProgress")]
        public double XpProgress { get; set; }

        [JsonPropertyName("balanceRzn")]
        public long BalanceRzn { get; set; }

        [JsonPropertyName("stats")]
        public Dictionary<string, JsonElement>? Stats { get; set; }
    }

    public Task<ClanResponse> GetClanAsync(string accessToken, CancellationToken ct)
        => GetAsync<ClanResponse>("api/launcher/clan", accessToken, ct);

    public Task<ClanMembersResponse> GetClanMembersAsync(string accessToken, CancellationToken ct)
        => GetAsync<ClanMembersResponse>("api/launcher/clan/members", accessToken, ct);

    public Task<ClanListResponse> SearchClansAsync(string accessToken, string? query, CancellationToken ct)
    {
        var q = (query ?? string.Empty).Trim();
        var path = "api/launcher/clan/list?page=1&take=40";
        if (q.Length > 0)
            path += "&q=" + Uri.EscapeDataString(q);
        return GetAsync<ClanListResponse>(path, accessToken, ct);
    }

    public Task<ApiResponse> JoinClanAsync(string accessToken, string clanId, CancellationToken ct)
    {
        clanId = (clanId ?? string.Empty).Trim();
        if (clanId.Length == 0)
            return Task.FromResult(new ApiResponse { Ok = false, Error = "Не выбран клан." });
        return PostAsync<ApiResponse>("api/launcher/clan/join", accessToken, new { clanId }, ct);
    }

    public Task<ApiResponse> LeaveClanAsync(string accessToken, CancellationToken ct)
        => PostAsync<ApiResponse>("api/launcher/clan/leave", accessToken, new { }, ct);

    public Task<ProgressionResponse> GetProgressionAsync(string accessToken, CancellationToken ct)
        => GetAsync<ProgressionResponse>("api/launcher/stats", accessToken, ct);

    private static async Task<T> GetAsync<T>(string path, string token, CancellationToken ct)
        where T : ApiResponse, new()
    {
        Exception? last = null;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(20));
                using var req = CreateRequest(HttpMethod.Get, path, token, null);
                using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);

                if (IsTransient(resp.StatusCode) && attempt < 3)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(200 * attempt), ct).ConfigureAwait(false);
                    continue;
                }

                return await ReadResponseAsync<T>(resp, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (attempt < 3)
            {
                last = ex;
                await Task.Delay(TimeSpan.FromMilliseconds(200 * attempt), ct).ConfigureAwait(false);
            }
        }

        return new T { Ok = false, Error = last?.Message ?? "Сетевой запрос не удался." };
    }

    private static async Task<T> PostAsync<T>(string path, string token, object payload, CancellationToken ct)
        where T : ApiResponse, new()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(25));
        using var req = CreateRequest(HttpMethod.Post, path, token, payload);
        using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        return await ReadResponseAsync<T>(resp, ct).ConfigureAwait(false);
    }

    private static HttpRequestMessage CreateRequest(HttpMethod method, string path, string token, object? payload)
    {
        var req = new HttpRequestMessage(method, path.TrimStart('/'));
        var normalizedToken = (token ?? string.Empty).Trim().Trim('"');
        if (normalizedToken.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            normalizedToken = normalizedToken["Bearer ".Length..].Trim();
        if (normalizedToken.Length == 0)
            throw new ArgumentException("accessToken is empty", nameof(token));

        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", normalizedToken);
        req.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
        req.Headers.TryAddWithoutValidation("X-Client", "LegendBornLauncher");
        req.Headers.TryAddWithoutValidation("X-Client-Version", LauncherIdentity.InformationalVersion);

        if (payload is not null)
        {
            var json = JsonSerializer.Serialize(payload, JsonOptions);
            req.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        return req;
    }

    private static async Task<T> ReadResponseAsync<T>(HttpResponseMessage resp, CancellationToken ct)
        where T : ApiResponse, new()
    {
        var body = await ReadBodyLimitedAsync(resp, ct).ConfigureAwait(false);
        T dto;
        try
        {
            dto = JsonSerializer.Deserialize<T>(body, JsonOptions) ?? new T();
        }
        catch
        {
            dto = new T { Ok = false, Error = "Сервер вернул некорректный JSON." };
        }

        if (!resp.IsSuccessStatusCode)
        {
            dto.Ok = false;
            dto.Error = string.IsNullOrWhiteSpace(dto.Error)
                ? $"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}"
                : dto.Error;
        }
        else if (!dto.Ok && string.IsNullOrWhiteSpace(dto.Error))
        {
            dto.Ok = true;
        }

        return dto;
    }

    private static async Task<string> ReadBodyLimitedAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        if (resp.Content.Headers.ContentLength is long length && length > MaxResponseBytes)
            throw new InvalidOperationException("Ответ сервера слишком большой.");

        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            using var output = new MemoryStream();
            while (true)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false);
                if (read == 0) break;
                if (output.Length + read > MaxResponseBytes)
                    throw new InvalidOperationException("Ответ сервера слишком большой.");
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
            ConnectTimeout = TimeSpan.FromSeconds(12),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            AllowAutoRedirect = false,
            MaxConnectionsPerServer = 6
        };

        var http = new HttpClient(handler)
        {
            BaseAddress = new Uri(SiteBaseUrl, UriKind.Absolute),
            Timeout = Timeout.InfiniteTimeSpan
        };
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        try { http.DefaultRequestHeaders.UserAgent.ParseAdd(LauncherIdentity.UserAgent); } catch { }
        return http;
    }
}
