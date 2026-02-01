// File: Services/SiteAuthService.cs
using System;
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
using LegendBorn.Models;

namespace LegendBorn.Services;

public sealed class SiteAuthService
{
    // Важно: API у тебя завязан на ru-домен (как в MainViewModel).
    private const string SiteBaseUrl = "https://ru.legendborn.ru/";

    // Safety: ответы API не должны быть большими
    private const int MaxResponseBytes = 512 * 1024; // 512 KB

    private const int DefaultAttempts = 3;

    // Presence
    private const int PresenceAttempts = 2;

    // ✅ Канонический presence endpoint на сайте (Next app router):
    // app/api/presence/heartbeat/route.ts => /api/presence/heartbeat
    private const string PresenceHeartbeatPath = "api/presence/heartbeat";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private static readonly HttpClient Http = CreateHttp();

    // =========================
    // Friends API DTO (internal)
    // =========================

    /// <summary>
    /// Базовый ответ { ok, error?, message?, status? }.
    /// status пригодится для /friends/request (sent/auto_accepted/already_sent и т.п.)
    /// </summary>
    public class OkResponse
    {
        [JsonPropertyName("ok")]
        public bool Ok { get; set; }

        [JsonPropertyName("error")]
        public string? Error { get; set; }

        [JsonPropertyName("message")]
        public string? Message { get; set; }

        [JsonPropertyName("status")]
        public string? Status { get; set; }
    }

    /// <summary>
    /// Ответ presence (может расширяться на сайте).
    /// </summary>
    public sealed class PresenceResponse : OkResponse
    {
        [JsonPropertyName("serverTimeUtc")]
        public DateTimeOffset? ServerTimeUtc { get; set; }

        [JsonPropertyName("launcherLastSeenUtc")]
        public DateTimeOffset? LauncherLastSeenUtc { get; set; }

        [JsonPropertyName("siteLastSeenUtc")]
        public DateTimeOffset? SiteLastSeenUtc { get; set; }
    }

    public sealed class FriendDto
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("userId")]
        public string? UserId { get; set; }

        [JsonPropertyName("publicId")]
        public int? PublicId { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("image")]
        public string? Image { get; set; }

        [JsonPropertyName("status")]
        public string? Status { get; set; } // "online" | "offline"

        [JsonPropertyName("source")]
        public string? Source { get; set; } // "site" | "twitch" | "minecraft" | "telegram"

        [JsonPropertyName("note")]
        public string? Note { get; set; }

        // =========================
        // ✅ PRESENCE (новое)
        // =========================

        [JsonPropertyName("onlinePlace")]
        public string? OnlinePlace { get; set; } // "launcher" | "site" | "offline"

        [JsonPropertyName("launcherOnline")]
        public bool? LauncherOnline { get; set; }

        [JsonPropertyName("siteOnline")]
        public bool? SiteOnline { get; set; }

        [JsonPropertyName("launcherLastSeenUtc")]
        public DateTimeOffset? LauncherLastSeenUtc { get; set; }

        [JsonPropertyName("siteLastSeenUtc")]
        public DateTimeOffset? SiteLastSeenUtc { get; set; }

        [JsonPropertyName("lastSeenUtc")]
        public DateTimeOffset? LastSeenUtc { get; set; }

        [JsonPropertyName("lastActivityUtc")]
        public DateTimeOffset? LastActivityUtc { get; set; }
    }

    public sealed class FriendsListResponse : OkResponse
    {
        [JsonPropertyName("friends")]
        public FriendDto[] Friends { get; set; } = Array.Empty<FriendDto>();

        [JsonPropertyName("relationshipStatus")]
        public string? RelationshipStatus { get; set; }
    }

    public sealed class FriendRequestsResponse : OkResponse
    {
        [JsonPropertyName("incoming")]
        public FriendDto[] Incoming { get; set; } = Array.Empty<FriendDto>();

        [JsonPropertyName("outgoing")]
        public FriendDto[] Outgoing { get; set; } = Array.Empty<FriendDto>();
    }

    private static HttpClient CreateHttp()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            Proxy = WebRequest.DefaultWebProxy,
            UseProxy = true,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            AllowAutoRedirect = true,
            MaxConnectionsPerServer = 8
        };

        var http = new HttpClient(handler)
        {
            BaseAddress = new Uri(SiteBaseUrl, UriKind.Absolute),
            Timeout = Timeout.InfiniteTimeSpan // таймауты per-request
        };

        try
        {
            var ua = LauncherIdentity.UserAgent;
            if (!string.IsNullOrWhiteSpace(ua))
                http.DefaultRequestHeaders.UserAgent.ParseAdd(ua);
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

        http.DefaultRequestHeaders.Accept.Clear();
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        return http;
    }

    private static string NormalizeToken(string token)
    {
        token = (token ?? string.Empty).Trim().Trim('"');

        // Иногда кто-то может передать "Bearer xxx" вместо чистого токена
        if (token.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            token = token.Substring("Bearer ".Length).Trim();

        return token;
    }

    private static long NormalizeUnixSeconds(long unix)
    {
        if (unix <= 0) return 0;
        if (unix >= 10_000_000_000L) return unix / 1000; // ms -> sec
        return unix;
    }

    private static bool IsRetryableStatus(HttpStatusCode code)
        => (int)code >= 500
           || code == HttpStatusCode.RequestTimeout
           || code == HttpStatusCode.TooManyRequests;

    private static bool IsAuthError(HttpStatusCode code)
        => code is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;

    private static async Task<string> ReadBodyUtf8LimitedAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);

        var buffer = new byte[16 * 1024];
        var total = 0;

        using var ms = new MemoryStream(capacity: Math.Min(64 * 1024, MaxResponseBytes));

        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false);
            if (read <= 0) break;

            total += read;
            if (total > MaxResponseBytes)
                throw new InvalidOperationException("Ответ сервера слишком большой.");

            ms.Write(buffer, 0, read);
        }

        if (ms.TryGetBuffer(out var seg) && seg.Array is not null)
            return Encoding.UTF8.GetString(seg.Array, seg.Offset, seg.Count);

        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static bool LooksLikeHtml(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return false;
        var t = body.TrimStart();
        return t.StartsWith("<!doctype", StringComparison.OrdinalIgnoreCase)
               || t.StartsWith("<html", StringComparison.OrdinalIgnoreCase)
               || t.StartsWith("<", StringComparison.Ordinal);
    }

    private static string TryGetString(JsonElement root, string name)
        => root.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String
            ? (p.GetString() ?? "")
            : "";

    private static long TryGetInt64(JsonElement root, string name)
        => root.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt64(out var v)
            ? v
            : 0;

    private static bool TryGetBool(JsonElement root, string name, out bool value)
    {
        value = false;
        if (!root.TryGetProperty(name, out var p)) return false;

        if (p.ValueKind == JsonValueKind.True) { value = true; return true; }
        if (p.ValueKind == JsonValueKind.False) { value = false; return true; }
        return false;
    }

    private static TimeSpan GetRetryDelay(HttpResponseMessage resp, int attemptIndex)
    {
        try
        {
            var ra = resp.Headers.RetryAfter;
            if (ra is not null)
            {
                if (ra.Delta is { } d)
                    return Clamp(d, TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(4));

                if (ra.Date is { } dt)
                {
                    var delta = dt - DateTimeOffset.UtcNow;
                    return Clamp(delta, TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(4));
                }
            }
        }
        catch { }

        // небольшой линейный backoff + кап
        var ms = 250 * Math.Max(1, attemptIndex);
        return TimeSpan.FromMilliseconds(Math.Min(ms, 1500));
    }

    private static TimeSpan Clamp(TimeSpan v, TimeSpan min, TimeSpan max)
    {
        if (v < min) return min;
        if (v > max) return max;
        return v;
    }

    private static async Task<HttpResponseMessage> SendAsyncWithRetry(
        Func<HttpRequestMessage> factory,
        CancellationToken ct,
        int attempts,
        TimeSpan perTryTimeout)
    {
        Exception? last = null;

        for (int i = 1; i <= Math.Max(1, attempts); i++)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                using var tcs = CancellationTokenSource.CreateLinkedTokenSource(ct);
                tcs.CancelAfter(perTryTimeout);

                using var req = factory();

                var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, tcs.Token)
                    .ConfigureAwait(false);

                if (IsRetryableStatus(resp.StatusCode))
                {
                    last = new HttpRequestException("Retryable status: " + (int)resp.StatusCode, null, resp.StatusCode);

                    var delay = GetRetryDelay(resp, i);
                    resp.Dispose();

                    await Task.Delay(delay, ct).ConfigureAwait(false);
                    continue;
                }

                return resp; // caller disposes
            }
            catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
            {
                last = ex;
                await Task.Delay(TimeSpan.FromMilliseconds(200 * i), ct).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                last = ex;
                await Task.Delay(TimeSpan.FromMilliseconds(200 * i), ct).ConfigureAwait(false);
            }
        }

        throw new InvalidOperationException("Сетевой запрос не удался после ретраев.", last);
    }

    private static void EnsureBearer(HttpRequestMessage req, string accessToken)
    {
        accessToken = NormalizeToken(accessToken);
        if (string.IsNullOrWhiteSpace(accessToken))
            throw new ArgumentException("accessToken is empty", nameof(accessToken));

        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        // Без кэша (иногда помогает с CDN)
        req.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };

        // ✅ Полезно для сервера: явно помечаем клиент как Launcher
        try
        {
            req.Headers.TryAddWithoutValidation("X-Presence-Client", "launcher");
            req.Headers.TryAddWithoutValidation("X-Client", "LegendBornLauncher");
            req.Headers.TryAddWithoutValidation("X-Client-Version", (LauncherIdentity.InformationalVersion ?? "").Trim());
        }
        catch { /* ignore */ }
    }

    private static void TryAttachDeviceId(HttpRequestMessage req, string? deviceId)
    {
        try
        {
            deviceId = (deviceId ?? "").Trim();
            if (deviceId.Length == 0) return;

            // сервер у тебя читает x-device-id
            req.Headers.Remove("X-Device-Id");
            req.Headers.TryAddWithoutValidation("X-Device-Id", deviceId);
        }
        catch { /* ignore */ }
    }

    private static T? TryDeserialize<T>(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return default;

        // если внезапно HTML — не пытаемся десериализовать
        if (LooksLikeHtml(json)) return default;

        try { return JsonSerializer.Deserialize<T>(json, JsonOptions); }
        catch { return default; }
    }

    private static string ExtractErrorFallback(string json)
    {
        try
        {
            if (LooksLikeHtml(json))
                return "Сервер вернул HTML вместо JSON.";

            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            var root = doc.RootElement;

            var err = TryGetString(root, "error");
            if (!string.IsNullOrWhiteSpace(err)) return err;

            var msg = TryGetString(root, "message");
            if (!string.IsNullOrWhiteSpace(msg)) return msg;

            return "Ошибка запроса.";
        }
        catch
        {
            return "Ошибка запроса.";
        }
    }

    private static void NormalizeOkFromHttp<T>(T dto, HttpResponseMessage resp, string body)
        where T : OkResponse
    {
        if (!resp.IsSuccessStatusCode)
        {
            dto.Ok = false;

            if (string.IsNullOrWhiteSpace(dto.Error))
                dto.Error = ExtractErrorFallback(body);

            if (!string.IsNullOrWhiteSpace(dto.Error))
                dto.Error = $"{dto.Error} (HTTP {(int)resp.StatusCode})";

            return;
        }

        // Если сервер не прислал ok, но HTTP 2xx — считаем ok=true (при отсутствии явной ошибки)
        if (!dto.Ok && string.IsNullOrWhiteSpace(dto.Error))
            dto.Ok = true;
    }

    private static StringContent JsonBody(object model)
        => new StringContent(JsonSerializer.Serialize(model, JsonOptions), Utf8NoBom, "application/json");

    private static string BuildApiPath(string relative)
    {
        relative = (relative ?? "").Trim();
        if (relative.Length == 0) return relative;
        return relative.StartsWith("/") ? relative[1..] : relative;
    }

    // =========================
    // Auth / Profile / Economy
    // =========================

    // POST /api/launcher/login -> { deviceId, connectUrl, expiresAtUnix }
    public async Task<(string DeviceId, string ConnectUrl, long ExpiresAtUnix)> StartLauncherLoginAsync(CancellationToken ct)
    {
        using var resp = await SendAsyncWithRetry(
            factory: () => new HttpRequestMessage(HttpMethod.Post, BuildApiPath("api/launcher/login")),
            ct: ct,
            attempts: DefaultAttempts,
            perTryTimeout: TimeSpan.FromSeconds(25)).ConfigureAwait(false);

        resp.EnsureSuccessStatusCode();

        var json = await ReadBodyUtf8LimitedAsync(resp, ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);

        var root = doc.RootElement;
        var deviceId = TryGetString(root, "deviceId");
        var connectUrl = TryGetString(root, "connectUrl");
        var expiresAtUnix = NormalizeUnixSeconds(TryGetInt64(root, "expiresAtUnix"));

        if (string.IsNullOrWhiteSpace(deviceId) || string.IsNullOrWhiteSpace(connectUrl))
            throw new InvalidOperationException("Сайт не вернул deviceId/connectUrl.");

        return (deviceId, connectUrl, expiresAtUnix);
    }

    // GET /api/launcher/login?deviceId=... -> { status:"PENDING" } OR { status:"OK", accessToken, expiresAtUnix }
    public async Task<AuthTokens?> PollLauncherLoginAsync(string deviceId, CancellationToken ct)
    {
        deviceId = (deviceId ?? "").Trim();
        if (string.IsNullOrWhiteSpace(deviceId))
            return null;

        using var resp = await SendAsyncWithRetry(
            factory: () =>
            {
                var url = BuildApiPath($"api/launcher/login?deviceId={Uri.EscapeDataString(deviceId)}");
                return new HttpRequestMessage(HttpMethod.Get, url);
            },
            ct: ct,
            attempts: DefaultAttempts,
            perTryTimeout: TimeSpan.FromSeconds(20)).ConfigureAwait(false);

        if (!resp.IsSuccessStatusCode)
            return null;

        var json = await ReadBodyUtf8LimitedAsync(resp, ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);

        var root = doc.RootElement;
        var status = TryGetString(root, "status");

        if (!string.Equals(status, "OK", StringComparison.OrdinalIgnoreCase))
            return null;

        var accessToken = NormalizeToken(TryGetString(root, "accessToken"));
        var expiresAtUnix = NormalizeUnixSeconds(TryGetInt64(root, "expiresAtUnix"));

        if (string.IsNullOrWhiteSpace(accessToken))
            return null;

        return new AuthTokens { AccessToken = accessToken, ExpiresAtUnix = expiresAtUnix };
    }

    // GET /api/launcher/me
    public async Task<UserProfile> GetMeAsync(string accessToken, CancellationToken ct)
    {
        using var resp = await SendAsyncWithRetry(
            factory: () =>
            {
                var req = new HttpRequestMessage(HttpMethod.Get, BuildApiPath("api/launcher/me"));
                EnsureBearer(req, accessToken);
                return req;
            },
            ct: ct,
            attempts: DefaultAttempts,
            perTryTimeout: TimeSpan.FromSeconds(25)).ConfigureAwait(false);

        if (IsAuthError(resp.StatusCode))
            throw new HttpRequestException("Unauthorized (launcher token invalid/expired).", null, resp.StatusCode);

        resp.EnsureSuccessStatusCode();

        var json = await ReadBodyUtf8LimitedAsync(resp, ct).ConfigureAwait(false);

        return JsonSerializer.Deserialize<UserProfile>(json, JsonOptions)
               ?? new UserProfile { UserName = "Unknown", MinecraftName = "Player" };
    }

    // GET /api/launcher/economy/balance -> { currency:"RZN", balance:123 }
    public async Task<long> GetRezoniteBalanceAsync(string accessToken, CancellationToken ct)
    {
        using var resp = await SendAsyncWithRetry(
            factory: () =>
            {
                var req = new HttpRequestMessage(HttpMethod.Get, BuildApiPath("api/launcher/economy/balance"));
                EnsureBearer(req, accessToken);
                return req;
            },
            ct: ct,
            attempts: DefaultAttempts,
            perTryTimeout: TimeSpan.FromSeconds(25)).ConfigureAwait(false);

        if (IsAuthError(resp.StatusCode))
            throw new HttpRequestException("Unauthorized (launcher token invalid/expired).", null, resp.StatusCode);

        resp.EnsureSuccessStatusCode();

        var json = await ReadBodyUtf8LimitedAsync(resp, ct).ConfigureAwait(false);
        var dto = JsonSerializer.Deserialize<RezoniteBalanceResponse>(json, JsonOptions);
        return dto?.Balance ?? 0;
    }

    // POST /api/launcher/events
    public async Task<LauncherEventResponse?> SendLauncherEventAsync(
        string accessToken,
        string key,
        string idempotencyKey,
        object? payload,
        CancellationToken ct)
    {
        key = (key ?? "").Trim();
        idempotencyKey = (idempotencyKey ?? "").Trim();

        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(idempotencyKey))
            throw new ArgumentException("key/idempotencyKey are required");

        var reqModel = new LauncherEventRequest
        {
            Key = key,
            IdempotencyKey = idempotencyKey,
            Payload = payload
        };

        using var resp = await SendAsyncWithRetry(
            factory: () =>
            {
                var req = new HttpRequestMessage(HttpMethod.Post, BuildApiPath("api/launcher/events"));
                EnsureBearer(req, accessToken);
                req.Content = new StringContent(JsonSerializer.Serialize(reqModel, JsonOptions), Utf8NoBom, "application/json");
                return req;
            },
            ct: ct,
            attempts: DefaultAttempts,
            perTryTimeout: TimeSpan.FromSeconds(30)).ConfigureAwait(false);

        if (IsAuthError(resp.StatusCode))
            throw new HttpRequestException("Unauthorized (launcher token invalid/expired).", null, resp.StatusCode);

        if (!resp.IsSuccessStatusCode)
            return null;

        var respJson = await ReadBodyUtf8LimitedAsync(resp, ct).ConfigureAwait(false);

        try
        {
            if (LooksLikeHtml(respJson))
                return new LauncherEventResponse { Ok = false, Rewarded = false, Balance = 0, Message = "Сервер вернул HTML вместо JSON." };

            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(respJson) ? "{}" : respJson);
            var root = doc.RootElement;

            var ok = false;
            TryGetBool(root, "ok", out ok);

            var rewarded = false;
            if (!TryGetBool(root, "rewarded", out rewarded))
            {
                if (TryGetBool(root, "duplicated", out var duplicated))
                    rewarded = !duplicated && ok;
                else
                    rewarded = ok;
            }

            var balance = TryGetInt64(root, "balance");
            var msg = TryGetString(root, "message");
            if (string.IsNullOrWhiteSpace(msg))
                msg = TryGetString(root, "error");

            return new LauncherEventResponse
            {
                Ok = ok,
                Rewarded = rewarded,
                Balance = balance < 0 ? 0 : balance,
                Message = string.IsNullOrWhiteSpace(msg) ? null : msg
            };
        }
        catch
        {
            try { return JsonSerializer.Deserialize<LauncherEventResponse>(respJson, JsonOptions); }
            catch { return null; }
        }
    }

    // =========================
    // Presence API (Launcher)
    // =========================

    public async Task<OkResponse> SendLauncherHeartbeatAsync(string accessToken, CancellationToken ct)
    {
        var bodyModel = new
        {
            state = "online",
            client = "launcher",
            version = (LauncherIdentity.InformationalVersion ?? "").Trim(),
            sentAtUtc = DateTimeOffset.UtcNow
        };

        using var resp = await SendAsyncWithRetry(
            factory: () =>
            {
                var req = new HttpRequestMessage(HttpMethod.Post, BuildApiPath(PresenceHeartbeatPath));
                EnsureBearer(req, accessToken);
                req.Content = JsonBody(bodyModel);
                return req;
            },
            ct: ct,
            attempts: PresenceAttempts,
            perTryTimeout: TimeSpan.FromSeconds(12)).ConfigureAwait(false);

        if (IsAuthError(resp.StatusCode))
            return new OkResponse { Ok = false, Error = "Требуется авторизация." };

        var body = await ReadBodyUtf8LimitedAsync(resp, ct).ConfigureAwait(false);
        var dto = TryDeserialize<OkResponse>(body) ?? new OkResponse();
        NormalizeOkFromHttp(dto, resp, body);
        return dto;
    }

    public async Task<OkResponse> SendLauncherOfflineAsync(string accessToken, CancellationToken ct)
    {
        var bodyModel = new
        {
            state = "offline",
            client = "launcher",
            version = (LauncherIdentity.InformationalVersion ?? "").Trim(),
            sentAtUtc = DateTimeOffset.UtcNow
        };

        using var resp = await SendAsyncWithRetry(
            factory: () =>
            {
                var req = new HttpRequestMessage(HttpMethod.Post, BuildApiPath(PresenceHeartbeatPath));
                EnsureBearer(req, accessToken);
                req.Content = JsonBody(bodyModel);
                return req;
            },
            ct: ct,
            attempts: 1,
            perTryTimeout: TimeSpan.FromSeconds(10)).ConfigureAwait(false);

        if (IsAuthError(resp.StatusCode))
            return new OkResponse { Ok = false, Error = "Требуется авторизация." };

        var body = await ReadBodyUtf8LimitedAsync(resp, ct).ConfigureAwait(false);
        var dto = TryDeserialize<OkResponse>(body) ?? new OkResponse();
        NormalizeOkFromHttp(dto, resp, body);
        return dto;
    }

    // =========================
    // Friends API
    // =========================

    public async Task<FriendsListResponse> GetFriendsAsync(string accessToken, CancellationToken ct)
    {
        using var resp = await SendAsyncWithRetry(
            factory: () =>
            {
                var req = new HttpRequestMessage(HttpMethod.Get, BuildApiPath("api/launcher/friends"));
                EnsureBearer(req, accessToken);
                return req;
            },
            ct: ct,
            attempts: DefaultAttempts,
            perTryTimeout: TimeSpan.FromSeconds(25)).ConfigureAwait(false);

        if (IsAuthError(resp.StatusCode))
            return new FriendsListResponse { Ok = false, Error = "Требуется авторизация." };

        var body = await ReadBodyUtf8LimitedAsync(resp, ct).ConfigureAwait(false);
        var dto = TryDeserialize<FriendsListResponse>(body) ?? new FriendsListResponse();

        dto.Friends ??= Array.Empty<FriendDto>();
        NormalizeOkFromHttp(dto, resp, body);

        return dto;
    }

    public async Task<FriendRequestsResponse> GetFriendRequestsAsync(string accessToken, CancellationToken ct)
    {
        using var resp = await SendAsyncWithRetry(
            factory: () =>
            {
                var req = new HttpRequestMessage(HttpMethod.Get, BuildApiPath("api/launcher/friends/requests"));
                EnsureBearer(req, accessToken);
                return req;
            },
            ct: ct,
            attempts: DefaultAttempts,
            perTryTimeout: TimeSpan.FromSeconds(25)).ConfigureAwait(false);

        if (IsAuthError(resp.StatusCode))
            return new FriendRequestsResponse { Ok = false, Error = "Требуется авторизация." };

        var body = await ReadBodyUtf8LimitedAsync(resp, ct).ConfigureAwait(false);
        var dto = TryDeserialize<FriendRequestsResponse>(body) ?? new FriendRequestsResponse();

        dto.Incoming ??= Array.Empty<FriendDto>();
        dto.Outgoing ??= Array.Empty<FriendDto>();
        NormalizeOkFromHttp(dto, resp, body);

        return dto;
    }

    public async Task<OkResponse> SendFriendRequestAsync(string accessToken, string query, CancellationToken ct)
    {
        query = (query ?? "").Trim();
        if (string.IsNullOrWhiteSpace(query))
            return new OkResponse { Ok = false, Error = "Пустой запрос." };

        using var resp = await SendAsyncWithRetry(
            factory: () =>
            {
                var req = new HttpRequestMessage(HttpMethod.Post, BuildApiPath("api/launcher/friends/request"));
                EnsureBearer(req, accessToken);
                req.Content = JsonBody(new { query });
                return req;
            },
            ct: ct,
            attempts: DefaultAttempts,
            perTryTimeout: TimeSpan.FromSeconds(25)).ConfigureAwait(false);

        if (IsAuthError(resp.StatusCode))
            return new OkResponse { Ok = false, Error = "Требуется авторизация." };

        var body = await ReadBodyUtf8LimitedAsync(resp, ct).ConfigureAwait(false);
        var dto = TryDeserialize<OkResponse>(body) ?? new OkResponse();
        NormalizeOkFromHttp(dto, resp, body);

        return dto;
    }

    public async Task<OkResponse> AcceptFriendRequestAsync(string accessToken, string fromUserId, CancellationToken ct)
    {
        fromUserId = (fromUserId ?? "").Trim();
        if (string.IsNullOrWhiteSpace(fromUserId))
            return new OkResponse { Ok = false, Error = "Не указан отправитель." };

        using var resp = await SendAsyncWithRetry(
            factory: () =>
            {
                var req = new HttpRequestMessage(HttpMethod.Post, BuildApiPath("api/launcher/friends/accept"));
                EnsureBearer(req, accessToken);
                req.Content = JsonBody(new { fromUserId, id = fromUserId, userId = fromUserId });
                return req;
            },
            ct: ct,
            attempts: DefaultAttempts,
            perTryTimeout: TimeSpan.FromSeconds(25)).ConfigureAwait(false);

        if (IsAuthError(resp.StatusCode))
            return new OkResponse { Ok = false, Error = "Требуется авторизация." };

        var body = await ReadBodyUtf8LimitedAsync(resp, ct).ConfigureAwait(false);
        var dto = TryDeserialize<OkResponse>(body) ?? new OkResponse();
        NormalizeOkFromHttp(dto, resp, body);

        return dto;
    }

    public async Task<OkResponse> DeclineFriendRequestAsync(string accessToken, string fromUserId, CancellationToken ct)
    {
        fromUserId = (fromUserId ?? "").Trim();
        if (string.IsNullOrWhiteSpace(fromUserId))
            return new OkResponse { Ok = false, Error = "Не указан отправитель." };

        using var resp = await SendAsyncWithRetry(
            factory: () =>
            {
                var req = new HttpRequestMessage(HttpMethod.Post, BuildApiPath("api/launcher/friends/decline"));
                EnsureBearer(req, accessToken);
                req.Content = JsonBody(new { fromUserId, id = fromUserId, userId = fromUserId });
                return req;
            },
            ct: ct,
            attempts: DefaultAttempts,
            perTryTimeout: TimeSpan.FromSeconds(25)).ConfigureAwait(false);

        if (IsAuthError(resp.StatusCode))
            return new OkResponse { Ok = false, Error = "Требуется авторизация." };

        var body = await ReadBodyUtf8LimitedAsync(resp, ct).ConfigureAwait(false);
        var dto = TryDeserialize<OkResponse>(body) ?? new OkResponse();
        NormalizeOkFromHttp(dto, resp, body);

        return dto;
    }

    public async Task<OkResponse> RemoveFriendAsync(string accessToken, string userId, CancellationToken ct)
    {
        userId = (userId ?? "").Trim();
        if (string.IsNullOrWhiteSpace(userId))
            return new OkResponse { Ok = false, Error = "Не указан пользователь." };

        using var resp = await SendAsyncWithRetry(
            factory: () =>
            {
                var req = new HttpRequestMessage(HttpMethod.Post, BuildApiPath("api/launcher/friends/remove"));
                EnsureBearer(req, accessToken);
                req.Content = JsonBody(new { userId, id = userId });
                return req;
            },
            ct: ct,
            attempts: DefaultAttempts,
            perTryTimeout: TimeSpan.FromSeconds(25)).ConfigureAwait(false);

        if (IsAuthError(resp.StatusCode))
            return new OkResponse { Ok = false, Error = "Требуется авторизация." };

        var body = await ReadBodyUtf8LimitedAsync(resp, ct).ConfigureAwait(false);
        var dto = TryDeserialize<OkResponse>(body) ?? new OkResponse();
        NormalizeOkFromHttp(dto, resp, body);

        return dto;
    }

    // =========================
    // ✅ Minecraft API (Launcher)
    // =========================

    public sealed class MinecraftLinkResponse : OkResponse
    {
        [JsonPropertyName("legendUuid")]
        public string? LegendUuid { get; set; }

        [JsonPropertyName("minecraft")]
        public MinecraftInfo? Minecraft { get; set; }

        public sealed class MinecraftInfo
        {
            [JsonPropertyName("uuid")]
            public string? Uuid { get; set; }

            [JsonPropertyName("username")]
            public string? Username { get; set; }

            [JsonPropertyName("selectedSkinKey")]
            public string? SelectedSkinKey { get; set; }

            [JsonPropertyName("skinUrl")]
            public string? SkinUrl { get; set; }

            [JsonPropertyName("isLinked")]
            public bool? IsLinked { get; set; }
        }
    }

    /// <summary>
    /// POST /api/minecraft/link
    /// Authorization: Bearer &lt;launcherAccessToken&gt;
    /// body: { username: "Player" }
    /// </summary>
    public async Task<MinecraftLinkResponse> LinkMinecraftAsync(
        string accessToken,
        string username,
        CancellationToken ct,
        string? deviceId = null)
    {
        username = (username ?? "").Trim();

        using var resp = await SendAsyncWithRetry(
            factory: () =>
            {
                var req = new HttpRequestMessage(HttpMethod.Post, BuildApiPath("api/minecraft/link"));
                EnsureBearer(req, accessToken);
                TryAttachDeviceId(req, deviceId);
                req.Content = JsonBody(new { username });
                return req;
            },
            ct: ct,
            attempts: DefaultAttempts,
            perTryTimeout: TimeSpan.FromSeconds(25)).ConfigureAwait(false);

        if (IsAuthError(resp.StatusCode))
            return new MinecraftLinkResponse { Ok = false, Error = "Требуется авторизация." };

        var body = await ReadBodyUtf8LimitedAsync(resp, ct).ConfigureAwait(false);
        var dto = TryDeserialize<MinecraftLinkResponse>(body) ?? new MinecraftLinkResponse();
        NormalizeOkFromHttp(dto, resp, body);
        return dto;
    }

    public sealed class MinecraftJoinTicketResponse : OkResponse
    {
        [JsonPropertyName("serverId")]
        public string? ServerId { get; set; }

        [JsonPropertyName("ticket")]
        public string? Ticket { get; set; }

        [JsonPropertyName("expiresAtUnix")]
        public long ExpiresAtUnix { get; set; }

        [JsonPropertyName("legendUuid")]
        public string? LegendUuid { get; set; }

        [JsonPropertyName("minecraft")]
        public MinecraftInfo? Minecraft { get; set; }

        public sealed class MinecraftInfo
        {
            [JsonPropertyName("uuid")]
            public string? Uuid { get; set; }

            [JsonPropertyName("username")]
            public string? Username { get; set; }

            [JsonPropertyName("selectedSkinKey")]
            public string? SelectedSkinKey { get; set; }

            [JsonPropertyName("skinUrl")]
            public string? SkinUrl { get; set; }
        }
    }

    /// <summary>
    /// POST /api/minecraft/join-ticket
    /// Authorization: Bearer &lt;launcherAccessToken&gt;
    /// body: { serverId: "...", mcName?: "Player" }
    /// </summary>
    public async Task<MinecraftJoinTicketResponse> CreateMinecraftJoinTicketAsync(
        string accessToken,
        string serverId,
        string? mcName,
        CancellationToken ct,
        string? deviceId = null)
    {
        serverId = (serverId ?? "").Trim();
        mcName = string.IsNullOrWhiteSpace(mcName) ? null : mcName.Trim();

        using var resp = await SendAsyncWithRetry(
            factory: () =>
            {
                var req = new HttpRequestMessage(HttpMethod.Post, BuildApiPath("api/minecraft/join-ticket"));
                EnsureBearer(req, accessToken);
                TryAttachDeviceId(req, deviceId);
                req.Content = JsonBody(new { serverId, mcName });
                return req;
            },
            ct: ct,
            attempts: DefaultAttempts,
            perTryTimeout: TimeSpan.FromSeconds(25)).ConfigureAwait(false);

        if (IsAuthError(resp.StatusCode))
            return new MinecraftJoinTicketResponse { Ok = false, Error = "Требуется авторизация." };

        var body = await ReadBodyUtf8LimitedAsync(resp, ct).ConfigureAwait(false);
        var dto = TryDeserialize<MinecraftJoinTicketResponse>(body) ?? new MinecraftJoinTicketResponse();

        // если сервер прислал ms — нормализуем (чтобы дальше было удобно)
        dto.ExpiresAtUnix = NormalizeUnixSeconds(dto.ExpiresAtUnix);

        NormalizeOkFromHttp(dto, resp, body);
        return dto;
    }
}
