// File: Services/SiteAuthService.cs
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

    // ✅ (1) Оптимизация: чтение UTF-8 лимитировано, без MemoryStream.ToArray() и с ArrayPool
    private static async Task<string> ReadBodyUtf8LimitedAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        // быстрый отсев по заголовку
        var len = resp.Content.Headers.ContentLength;
        if (len.HasValue && len.Value > MaxResponseBytes)
            throw new InvalidOperationException("Ответ сервера слишком большой.");

        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);

        byte[]? rented = null;
        try
        {
            var cap = (int)Math.Min(64 * 1024, MaxResponseBytes);
            rented = ArrayPool<byte>.Shared.Rent(cap);

            var total = 0;

            while (true)
            {
                if (total == rented.Length)
                {
                    var newSizeLong = Math.Min((long)rented.Length * 2, (long)MaxResponseBytes);
                    if (newSizeLong <= rented.Length)
                        throw new InvalidOperationException("Ответ сервера слишком большой.");

                    var newArr = ArrayPool<byte>.Shared.Rent((int)newSizeLong);
                    Buffer.BlockCopy(rented, 0, newArr, 0, total);
                    ArrayPool<byte>.Shared.Return(rented);
                    rented = newArr;
                }

                var toRead = rented.Length - total;
                var read = await stream.ReadAsync(rented, total, toRead, ct).ConfigureAwait(false);
                if (read <= 0)
                    break;

                total += read;

                if (total > MaxResponseBytes)
                    throw new InvalidOperationException("Ответ сервера слишком большой.");
            }

            if (total <= 0) return "";

            return Encoding.UTF8.GetString(rented, 0, total);
        }
        finally
        {
            if (rented is not null)
                ArrayPool<byte>.Shared.Return(rented);
        }
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

                    // ✅ не делаем лишний delay после последней попытки
                    if (i < attempts)
                        await Task.Delay(delay, ct).ConfigureAwait(false);

                    continue;
                }

                return resp; // caller disposes
            }
            catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
            {
                last = ex;
                if (i < attempts)
                    await Task.Delay(TimeSpan.FromMilliseconds(200 * i), ct).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                last = ex;
                if (i < attempts)
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

    // ✅ (2) Оптимизация: JSON body без промежуточной строки
    private static HttpContent JsonBody(object model)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(model, JsonOptions);
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        return content;
    }

    private static string BuildApiPath(string relative)
    {
        relative = (relative ?? "").Trim();
        if (relative.Length == 0) return relative;
        return relative.StartsWith("/") ? relative[1..] : relative;
    }

    // ✅ (5) Рефактор: общий helper для ответов OkResponse-типа (friends/presence/minecraft)
    private static async Task<T> SendAndReadOkDtoAsync<T>(
        Func<HttpRequestMessage> factory,
        CancellationToken ct,
        int attempts,
        TimeSpan perTryTimeout)
        where T : OkResponse, new()
    {
        using var resp = await SendAsyncWithRetry(factory, ct, attempts, perTryTimeout).ConfigureAwait(false);

        if (IsAuthError(resp.StatusCode))
            return new T { Ok = false, Error = "Требуется авторизация." };

        var body = await ReadBodyUtf8LimitedAsync(resp, ct).ConfigureAwait(false);
        var dto = TryDeserialize<T>(body) ?? new T();
        NormalizeOkFromHttp(dto, resp, body);
        return dto;
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
                req.Content = JsonBody(reqModel);
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

    public Task<OkResponse> SendLauncherHeartbeatAsync(string accessToken, CancellationToken ct)
    {
        var bodyModel = new
        {
            state = "online",
            client = "launcher",
            version = (LauncherIdentity.InformationalVersion ?? "").Trim(),
            sentAtUtc = DateTimeOffset.UtcNow
        };

        return SendAndReadOkDtoAsync<OkResponse>(
            factory: () =>
            {
                var req = new HttpRequestMessage(HttpMethod.Post, BuildApiPath(PresenceHeartbeatPath));
                EnsureBearer(req, accessToken);
                req.Content = JsonBody(bodyModel);
                return req;
            },
            ct: ct,
            attempts: PresenceAttempts,
            perTryTimeout: TimeSpan.FromSeconds(12));
    }

    public Task<OkResponse> SendLauncherOfflineAsync(string accessToken, CancellationToken ct)
    {
        var bodyModel = new
        {
            state = "offline",
            client = "launcher",
            version = (LauncherIdentity.InformationalVersion ?? "").Trim(),
            sentAtUtc = DateTimeOffset.UtcNow
        };

        return SendAndReadOkDtoAsync<OkResponse>(
            factory: () =>
            {
                var req = new HttpRequestMessage(HttpMethod.Post, BuildApiPath(PresenceHeartbeatPath));
                EnsureBearer(req, accessToken);
                req.Content = JsonBody(bodyModel);
                return req;
            },
            ct: ct,
            attempts: 1,
            perTryTimeout: TimeSpan.FromSeconds(10));
    }

    // =========================
    // Friends API
    // =========================

    public async Task<FriendsListResponse> GetFriendsAsync(string accessToken, CancellationToken ct)
    {
        var dto = await SendAndReadOkDtoAsync<FriendsListResponse>(
            factory: () =>
            {
                var req = new HttpRequestMessage(HttpMethod.Get, BuildApiPath("api/launcher/friends"));
                EnsureBearer(req, accessToken);
                return req;
            },
            ct: ct,
            attempts: DefaultAttempts,
            perTryTimeout: TimeSpan.FromSeconds(25)).ConfigureAwait(false);

        dto.Friends ??= Array.Empty<FriendDto>();
        return dto;
    }

    public async Task<FriendRequestsResponse> GetFriendRequestsAsync(string accessToken, CancellationToken ct)
    {
        var dto = await SendAndReadOkDtoAsync<FriendRequestsResponse>(
            factory: () =>
            {
                var req = new HttpRequestMessage(HttpMethod.Get, BuildApiPath("api/launcher/friends/requests"));
                EnsureBearer(req, accessToken);
                return req;
            },
            ct: ct,
            attempts: DefaultAttempts,
            perTryTimeout: TimeSpan.FromSeconds(25)).ConfigureAwait(false);

        dto.Incoming ??= Array.Empty<FriendDto>();
        dto.Outgoing ??= Array.Empty<FriendDto>();
        return dto;
    }

    public Task<OkResponse> SendFriendRequestAsync(string accessToken, string query, CancellationToken ct)
    {
        query = (query ?? "").Trim();
        if (string.IsNullOrWhiteSpace(query))
            return Task.FromResult(new OkResponse { Ok = false, Error = "Пустой запрос." });

        return SendAndReadOkDtoAsync<OkResponse>(
            factory: () =>
            {
                var req = new HttpRequestMessage(HttpMethod.Post, BuildApiPath("api/launcher/friends/request"));
                EnsureBearer(req, accessToken);
                req.Content = JsonBody(new { query });
                return req;
            },
            ct: ct,
            attempts: DefaultAttempts,
            perTryTimeout: TimeSpan.FromSeconds(25));
    }

    public Task<OkResponse> AcceptFriendRequestAsync(string accessToken, string fromUserId, CancellationToken ct)
    {
        fromUserId = (fromUserId ?? "").Trim();
        if (string.IsNullOrWhiteSpace(fromUserId))
            return Task.FromResult(new OkResponse { Ok = false, Error = "Не указан отправитель." });

        return SendAndReadOkDtoAsync<OkResponse>(
            factory: () =>
            {
                var req = new HttpRequestMessage(HttpMethod.Post, BuildApiPath("api/launcher/friends/accept"));
                EnsureBearer(req, accessToken);
                req.Content = JsonBody(new { fromUserId, id = fromUserId, userId = fromUserId });
                return req;
            },
            ct: ct,
            attempts: DefaultAttempts,
            perTryTimeout: TimeSpan.FromSeconds(25));
    }

    public Task<OkResponse> DeclineFriendRequestAsync(string accessToken, string fromUserId, CancellationToken ct)
    {
        fromUserId = (fromUserId ?? "").Trim();
        if (string.IsNullOrWhiteSpace(fromUserId))
            return Task.FromResult(new OkResponse { Ok = false, Error = "Не указан отправитель." });

        return SendAndReadOkDtoAsync<OkResponse>(
            factory: () =>
            {
                var req = new HttpRequestMessage(HttpMethod.Post, BuildApiPath("api/launcher/friends/decline"));
                EnsureBearer(req, accessToken);
                req.Content = JsonBody(new { fromUserId, id = fromUserId, userId = fromUserId });
                return req;
            },
            ct: ct,
            attempts: DefaultAttempts,
            perTryTimeout: TimeSpan.FromSeconds(25));
    }

    public Task<OkResponse> RemoveFriendAsync(string accessToken, string userId, CancellationToken ct)
    {
        userId = (userId ?? "").Trim();
        if (string.IsNullOrWhiteSpace(userId))
            return Task.FromResult(new OkResponse { Ok = false, Error = "Не указан пользователь." });

        return SendAndReadOkDtoAsync<OkResponse>(
            factory: () =>
            {
                var req = new HttpRequestMessage(HttpMethod.Post, BuildApiPath("api/launcher/friends/remove"));
                EnsureBearer(req, accessToken);
                req.Content = JsonBody(new { userId, id = userId });
                return req;
            },
            ct: ct,
            attempts: DefaultAttempts,
            perTryTimeout: TimeSpan.FromSeconds(25));
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
    public Task<MinecraftLinkResponse> LinkMinecraftAsync(
        string accessToken,
        string username,
        CancellationToken ct,
        string? deviceId = null)
    {
        username = (username ?? "").Trim();

        return SendAndReadOkDtoAsync<MinecraftLinkResponse>(
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
            perTryTimeout: TimeSpan.FromSeconds(25));
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

        var dto = await SendAndReadOkDtoAsync<MinecraftJoinTicketResponse>(
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

        // если сервер прислал ms — нормализуем (чтобы дальше было удобно)
        dto.ExpiresAtUnix = NormalizeUnixSeconds(dto.ExpiresAtUnix);

        return dto;
    }
}
