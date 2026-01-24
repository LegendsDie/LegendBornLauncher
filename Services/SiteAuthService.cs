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
        // опционально: сервер может вернуть серверное время
        [JsonPropertyName("serverTimeUtc")]
        public DateTimeOffset? ServerTimeUtc { get; set; }

        // опционально: сервер может вернуть то, что записал
        [JsonPropertyName("launcherLastSeenUtc")]
        public DateTimeOffset? LauncherLastSeenUtc { get; set; }

        [JsonPropertyName("siteLastSeenUtc")]
        public DateTimeOffset? SiteLastSeenUtc { get; set; }
    }

    /// <summary>
    /// Friend DTO, совместимый с сайтом:
    /// - Новый формат: { id, name, status, source, note, image? }
    /// - Легаси формат (если где-то ещё есть): { userId, publicId, name, image }
    ///
    /// ✅ Добавлены поля presence (не ломают старый сайт):
    /// onlinePlace / launcherOnline / siteOnline / launcherLastSeenUtc / siteLastSeenUtc / lastSeenUtc / lastActivityUtc
    /// </summary>
    public sealed class FriendDto
    {
        // ✅ новый формат сайта: id = publicId строкой или cuid
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        // ✅ легаси
        [JsonPropertyName("userId")]
        public string? UserId { get; set; }

        // ✅ легаси (User.publicId = Int)
        [JsonPropertyName("publicId")]
        public int? PublicId { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        // опционально (если сервер отдаёт, или в будущем)
        [JsonPropertyName("image")]
        public string? Image { get; set; }

        // ✅ новый формат сайта
        [JsonPropertyName("status")]
        public string? Status { get; set; } // "online" | "offline"

        [JsonPropertyName("source")]
        public string? Source { get; set; } // "site" | "twitch" | "minecraft" | "telegram"

        [JsonPropertyName("note")]
        public string? Note { get; set; }

        // =========================
        // ✅ PRESENCE (новое)
        // =========================

        /// <summary>
        /// "launcher" | "site" | "offline" (лучший вариант для UI, приоритет выбирается на сервере)
        /// </summary>
        [JsonPropertyName("onlinePlace")]
        public string? OnlinePlace { get; set; }

        /// <summary>
        /// true, если друг онлайн именно в лаунчере (сервер вычисляет по heartbeat/TTL)
        /// </summary>
        [JsonPropertyName("launcherOnline")]
        public bool? LauncherOnline { get; set; }

        /// <summary>
        /// true, если друг онлайн на сайте (сервер вычисляет по активности/TTL)
        /// </summary>
        [JsonPropertyName("siteOnline")]
        public bool? SiteOnline { get; set; }

        /// <summary>
        /// ISO 8601 UTC (рекомендуется), когда последний раз виделся лаунчер
        /// </summary>
        [JsonPropertyName("launcherLastSeenUtc")]
        public DateTimeOffset? LauncherLastSeenUtc { get; set; }

        /// <summary>
        /// ISO 8601 UTC (рекомендуется), когда последний раз была активность на сайте
        /// </summary>
        [JsonPropertyName("siteLastSeenUtc")]
        public DateTimeOffset? SiteLastSeenUtc { get; set; }

        // запасные/легаси варианты, если на сервере будут другие имена:
        [JsonPropertyName("lastSeenUtc")]
        public DateTimeOffset? LastSeenUtc { get; set; }

        [JsonPropertyName("lastActivityUtc")]
        public DateTimeOffset? LastActivityUtc { get; set; }
    }

    public sealed class FriendsListResponse : OkResponse
    {
        [JsonPropertyName("friends")]
        public FriendDto[] Friends { get; set; } = Array.Empty<FriendDto>();

        // на всякий (если когда-то вернёшь, как на section endpoint)
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
        => (token ?? string.Empty).Trim().Trim('"');

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
            req.Headers.TryAddWithoutValidation("X-Client", "LegendBornLauncher");
            req.Headers.TryAddWithoutValidation("X-Client-Version", (LauncherIdentity.InformationalVersion ?? "").Trim());
        }
        catch { /* ignore */ }
    }

    private static T? TryDeserialize<T>(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return default;
        try { return JsonSerializer.Deserialize<T>(json, JsonOptions); }
        catch { return default; }
    }

    private static string ExtractErrorFallback(string json)
    {
        try
        {
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

    // =========================
    // Auth / Profile / Economy
    // =========================

    // POST /api/launcher/login -> { deviceId, connectUrl, expiresAtUnix }
    public async Task<(string DeviceId, string ConnectUrl, long ExpiresAtUnix)> StartLauncherLoginAsync(CancellationToken ct)
    {
        using var resp = await SendAsyncWithRetry(
            factory: () => new HttpRequestMessage(HttpMethod.Post, "api/launcher/login"),
            ct: ct,
            attempts: DefaultAttempts,
            perTryTimeout: TimeSpan.FromSeconds(25)).ConfigureAwait(false);

        resp.EnsureSuccessStatusCode();

        var json = await ReadBodyUtf8LimitedAsync(resp, ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);

        var root = doc.RootElement;
        var deviceId = TryGetString(root, "deviceId");
        var connectUrl = TryGetString(root, "connectUrl");
        var expiresAtUnix = TryGetInt64(root, "expiresAtUnix");

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
                var url = $"api/launcher/login?deviceId={Uri.EscapeDataString(deviceId)}";
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
        var expiresAtUnix = TryGetInt64(root, "expiresAtUnix");

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
                var req = new HttpRequestMessage(HttpMethod.Get, "api/launcher/me");
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
                var req = new HttpRequestMessage(HttpMethod.Get, "api/launcher/economy/balance");
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
                var req = new HttpRequestMessage(HttpMethod.Post, "api/launcher/events");
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

    /// <summary>
    /// ✅ Heartbeat "я онлайн в лаунчере".
    /// Сервер должен сохранять launcherLastSeenUtc = now и возвращать ok.
    /// </summary>
    public async Task<OkResponse> SendLauncherHeartbeatAsync(string accessToken, CancellationToken ct)
    {
        // body можно расширять, сервер может игнорировать
        var bodyModel = new
        {
            state = "online",
            client = "LegendBornLauncher",
            version = (LauncherIdentity.InformationalVersion ?? "").Trim(),
            sentAtUtc = DateTimeOffset.UtcNow
        };

        using var resp = await SendAsyncWithRetry(
            factory: () =>
            {
                var req = new HttpRequestMessage(HttpMethod.Post, "api/launcher/presence/launcher");
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

    /// <summary>
    /// Опционально: при закрытии лаунчера (можно не делать — TTL тоже решает).
    /// </summary>
    public async Task<OkResponse> SendLauncherOfflineAsync(string accessToken, CancellationToken ct)
    {
        var bodyModel = new
        {
            state = "offline",
            client = "LegendBornLauncher",
            version = (LauncherIdentity.InformationalVersion ?? "").Trim(),
            sentAtUtc = DateTimeOffset.UtcNow
        };

        using var resp = await SendAsyncWithRetry(
            factory: () =>
            {
                var req = new HttpRequestMessage(HttpMethod.Post, "api/launcher/presence/launcher/offline");
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

    // GET /api/launcher/friends -> { ok, friends:[...] }
    public async Task<FriendsListResponse> GetFriendsAsync(string accessToken, CancellationToken ct)
    {
        using var resp = await SendAsyncWithRetry(
            factory: () =>
            {
                var req = new HttpRequestMessage(HttpMethod.Get, "api/launcher/friends");
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

    // GET /api/launcher/friends/requests -> { ok, incoming:[...], outgoing:[...] }
    public async Task<FriendRequestsResponse> GetFriendRequestsAsync(string accessToken, CancellationToken ct)
    {
        using var resp = await SendAsyncWithRetry(
            factory: () =>
            {
                var req = new HttpRequestMessage(HttpMethod.Get, "api/launcher/friends/requests");
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

    // POST /api/launcher/friends/request body { query }
    public async Task<OkResponse> SendFriendRequestAsync(string accessToken, string query, CancellationToken ct)
    {
        query = (query ?? "").Trim();
        if (string.IsNullOrWhiteSpace(query))
            return new OkResponse { Ok = false, Error = "Пустой запрос." };

        using var resp = await SendAsyncWithRetry(
            factory: () =>
            {
                var req = new HttpRequestMessage(HttpMethod.Post, "api/launcher/friends/request");
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

    // POST /api/launcher/friends/accept body { fromUserId } (и на всякий продублируем ключи)
    public async Task<OkResponse> AcceptFriendRequestAsync(string accessToken, string fromUserId, CancellationToken ct)
    {
        fromUserId = (fromUserId ?? "").Trim();
        if (string.IsNullOrWhiteSpace(fromUserId))
            return new OkResponse { Ok = false, Error = "Не указан отправитель." };

        using var resp = await SendAsyncWithRetry(
            factory: () =>
            {
                var req = new HttpRequestMessage(HttpMethod.Post, "api/launcher/friends/accept");
                EnsureBearer(req, accessToken);

                // ✅ совместимость: сервер может ждать fromUserId или id
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

    // POST /api/launcher/friends/decline body { fromUserId } (и на всякий продублируем ключи)
    public async Task<OkResponse> DeclineFriendRequestAsync(string accessToken, string fromUserId, CancellationToken ct)
    {
        fromUserId = (fromUserId ?? "").Trim();
        if (string.IsNullOrWhiteSpace(fromUserId))
            return new OkResponse { Ok = false, Error = "Не указан отправитель." };

        using var resp = await SendAsyncWithRetry(
            factory: () =>
            {
                var req = new HttpRequestMessage(HttpMethod.Post, "api/launcher/friends/decline");
                EnsureBearer(req, accessToken);

                // ✅ совместимость: сервер может ждать fromUserId или id
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

    // POST /api/launcher/friends/remove body { userId } (и на всякий продублируем ключи)
    public async Task<OkResponse> RemoveFriendAsync(string accessToken, string userId, CancellationToken ct)
    {
        userId = (userId ?? "").Trim();
        if (string.IsNullOrWhiteSpace(userId))
            return new OkResponse { Ok = false, Error = "Не указан пользователь." };

        using var resp = await SendAsyncWithRetry(
            factory: () =>
            {
                var req = new HttpRequestMessage(HttpMethod.Post, "api/launcher/friends/remove");
                EnsureBearer(req, accessToken);

                // ✅ совместимость: сервер может ждать userId или id
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
}
