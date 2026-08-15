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
        [JsonPropertyName("ok")] public bool Ok { get; set; }
        [JsonPropertyName("error")] public string? Error { get; set; }
        [JsonPropertyName("message")] public string? Message { get; set; }
    }

    public sealed class ClanResponse : ApiResponse
    {
        [JsonPropertyName("hasClan")] public bool HasClan { get; set; }
        [JsonPropertyName("clan")] public ClanDto? Clan { get; set; }
        [JsonPropertyName("rank")] public ClanRankDto? Rank { get; set; }
        [JsonPropertyName("isLeader")] public bool IsLeader { get; set; }
        [JsonPropertyName("joinedAt")] public DateTimeOffset? JoinedAt { get; set; }
    }

    public sealed class ClanDto
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        [JsonPropertyName("key")] public string Key { get; set; } = "";
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("description")] public string? Description { get; set; }
        [JsonPropertyName("emblemUrl")] public string? EmblemUrl { get; set; }
        [JsonPropertyName("image")] public string? Image { get; set; }
        [JsonPropertyName("colorHex")] public string? ColorHex { get; set; }
        [JsonPropertyName("membersCount")] public int MembersCount { get; set; }

        [JsonIgnore] public string? Tag { get => Key; set => Key = value ?? ""; }
        [JsonIgnore] public string? AvatarUrl { get => EmblemUrl ?? Image; set => EmblemUrl = value; }
        [JsonIgnore] public long Treasury { get; set; }
        [JsonIgnore] public string? Role { get; set; }
        [JsonIgnore] public int MemberCount { get => MembersCount; set => MembersCount = value; }
    }

    public sealed class ClanRankDto
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("key")] public string? Key { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("level")] public int Level { get; set; }
        [JsonPropertyName("isLeader")] public bool IsLeader { get; set; }
    }

    public sealed class ClanListResponse : ApiResponse
    {
        [JsonPropertyName("clans")] public ClanDto[] Clans { get; set; } = Array.Empty<ClanDto>();
    }

    public sealed class ClanMembersResponse : ApiResponse
    {
        [JsonPropertyName("hasClan")] public bool HasClan { get; set; }
        [JsonPropertyName("members")] public ClanMemberDto[] Members { get; set; } = Array.Empty<ClanMemberDto>();
    }

    public sealed class ClanMemberDto
    {
        [JsonPropertyName("userId")] public string UserId { get; set; } = "";
        [JsonPropertyName("publicId")] public int? PublicId { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("image")] public string? Image { get; set; }
        [JsonPropertyName("rankName")] public string? RankName { get; set; }
        [JsonPropertyName("rankLevel")] public int? RankLevel { get; set; }
        [JsonPropertyName("isLeader")] public bool IsLeader { get; set; }
        [JsonPropertyName("joinedAt")] public DateTimeOffset? JoinedAt { get; set; }

        [JsonIgnore] public string Role => IsLeader ? "OWNER" : string.IsNullOrWhiteSpace(RankName) ? "MEMBER" : RankName!;
        [JsonIgnore] public ClanMemberUserDto User => new()
        {
            PublicId = PublicId,
            Nick = Name,
            AvatarUrl = Image,
            Presence = "offline"
        };
    }

    public sealed class ClanMemberUserDto
    {
        public int? PublicId { get; set; }
        public string? Nick { get; set; }
        public string? AvatarUrl { get; set; }
        public string? Presence { get; set; }
        public string? PresenceServerKey { get; set; }
        public DateTimeOffset? PresenceUpdatedAt { get; set; }
    }

    public sealed class ProgressionResponse : ApiResponse
    {
        [JsonPropertyName("xpTotal")] public long XpTotal { get; set; }
        [JsonPropertyName("xpSeason")] public long XpSeason { get; set; }
        [JsonPropertyName("level")] public int Level { get; set; } = 1;
        [JsonPropertyName("xpIntoLevel")] public long XpIntoLevel { get; set; }
        [JsonPropertyName("xpForNext")] public long XpForNext { get; set; }
        [JsonPropertyName("xpProgress")] public double XpProgress { get; set; }
        [JsonPropertyName("balanceRzn")] public long BalanceRzn { get; set; }
        [JsonPropertyName("stats")] public Dictionary<string, JsonElement>? Stats { get; set; }
    }

    public async Task<ClanResponse> GetClanAsync(string accessToken, CancellationToken ct)
    {
        var dto = await GetAsync<ClanResponse>("api/launcher/clan", accessToken, ct).ConfigureAwait(false);
        if (dto.Clan is not null)
            dto.Clan.Role = dto.IsLeader ? "OWNER" : dto.Rank?.Key ?? dto.Rank?.Name ?? "MEMBER";
        return dto;
    }

    public Task<ClanMembersResponse> GetClanMembersAsync(string accessToken, CancellationToken ct)
        => GetAsync<ClanMembersResponse>("api/launcher/clan/members", accessToken, ct);

    public async Task<ClanListResponse> SearchClansAsync(string accessToken, string? query, CancellationToken ct)
    {
        var dto = await GetAsync<ClanListResponse>("api/launcher/clan/list", accessToken, ct).ConfigureAwait(false);
        dto.Clans ??= Array.Empty<ClanDto>();
        var q = (query ?? string.Empty).Trim();
        if (dto.Ok && q.Length > 0)
        {
            dto.Clans = Array.FindAll(dto.Clans, clan =>
                clan.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase) ||
                clan.Key.Contains(q, StringComparison.OrdinalIgnoreCase));
        }

        // The join API accepts clanKey, while the list endpoint also exposes a database id.
        // Normalize the UI action identifier to the public clan key so it can never send the DB id by mistake.
        foreach (var clan in dto.Clans)
            clan.Id = clan.Key;
        return dto;
    }

    public Task<ApiResponse> JoinClanAsync(string accessToken, string clanKey, CancellationToken ct)
    {
        clanKey = (clanKey ?? string.Empty).Trim().ToUpperInvariant();
        if (clanKey.Length == 0)
            return Task.FromResult(new ApiResponse { Ok = false, Error = "Не выбран клан." });
        return PostAsync<ApiResponse>("api/launcher/clan/join", accessToken, new { clanKey }, ct);
    }

    public Task<ApiResponse> LeaveClanAsync(string accessToken, CancellationToken ct)
        => PostAsync<ApiResponse>("api/launcher/clan/leave", accessToken, new { }, ct);

    public Task<ProgressionResponse> GetProgressionAsync(string accessToken, CancellationToken ct)
        => GetAsync<ProgressionResponse>("api/launcher/stats", accessToken, ct);

    private static async Task<T> GetAsync<T>(string path, string token, CancellationToken ct) where T : ApiResponse, new()
    {
        Exception? last = null;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(20));
                using var request = CreateRequest(HttpMethod.Get, path, token, null);
                using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                if (IsTransient(response.StatusCode) && attempt < 3)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(200 * attempt), ct).ConfigureAwait(false);
                    continue;
                }
                return await ReadResponseAsync<T>(response, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (attempt < 3)
            {
                last = ex;
                await Task.Delay(TimeSpan.FromMilliseconds(200 * attempt), ct).ConfigureAwait(false);
            }
        }
        return new T { Ok = false, Error = last?.Message ?? "Сетевой запрос не удался." };
    }

    private static async Task<T> PostAsync<T>(string path, string token, object payload, CancellationToken ct) where T : ApiResponse, new()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(25));
        using var request = CreateRequest(HttpMethod.Post, path, token, payload);
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        return await ReadResponseAsync<T>(response, ct).ConfigureAwait(false);
    }

    private static HttpRequestMessage CreateRequest(HttpMethod method, string path, string token, object? payload)
    {
        var request = new HttpRequestMessage(method, path.TrimStart('/'));
        var normalized = (token ?? string.Empty).Trim().Trim('"');
        if (normalized.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) normalized = normalized[7..].Trim();
        if (normalized.Length == 0) throw new ArgumentException("accessToken is empty", nameof(token));

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", normalized);
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
        request.Headers.TryAddWithoutValidation("X-Client", "LegendBornLauncher");
        request.Headers.TryAddWithoutValidation("X-Client-Version", LauncherIdentity.InformationalVersion ?? string.Empty);
        if (payload is not null)
            request.Content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json");
        return request;
    }

    private static async Task<T> ReadResponseAsync<T>(HttpResponseMessage response, CancellationToken ct) where T : ApiResponse, new()
    {
        var body = await ReadBodyLimitedAsync(response, ct).ConfigureAwait(false);
        T dto;
        try { dto = JsonSerializer.Deserialize<T>(body, JsonOptions) ?? new T(); }
        catch { dto = new T { Ok = false, Error = "Сервер вернул некорректный JSON." }; }

        if (!response.IsSuccessStatusCode)
        {
            dto.Ok = false;
            if (string.IsNullOrWhiteSpace(dto.Error)) dto.Error = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}";
        }
        else if (!dto.Ok && string.IsNullOrWhiteSpace(dto.Error)) dto.Ok = true;
        return dto;
    }

    private static async Task<string> ReadBodyLimitedAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength is long length && length > MaxResponseBytes)
            throw new InvalidOperationException("Ответ сервера слишком большой.");

        await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            using var output = new MemoryStream();
            while (true)
            {
                var read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false);
                if (read == 0) break;
                if (output.Length + read > MaxResponseBytes) throw new InvalidOperationException("Ответ сервера слишком большой.");
                output.Write(buffer, 0, read);
            }
            return Encoding.UTF8.GetString(output.GetBuffer(), 0, checked((int)output.Length));
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
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
        var http = new HttpClient(handler) { BaseAddress = new Uri(SiteBaseUrl), Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        try { http.DefaultRequestHeaders.UserAgent.ParseAdd(LauncherIdentity.UserAgent ?? "LegendBornLauncher"); } catch { }
        return http;
    }
}
