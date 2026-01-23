// File: Services/SiteAuthService.cs
using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
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

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private static readonly HttpClient Http = CreateHttp();

    // ===== DTO for Friends API (nested to avoid conflicts) =====
    public class OkResponse
    {
        public bool Ok { get; set; }
        public string? Error { get; set; }
        public string? Message { get; set; }
    }

    public sealed class FriendsListResponse : OkResponse
    {
        public FriendDto[] Friends { get; set; } = Array.Empty<FriendDto>();
    }

    public sealed class FriendRequestsResponse : OkResponse
    {
        public FriendDto[] Incoming { get; set; } = Array.Empty<FriendDto>();
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
            return;
        }

        // Если сервер не прислал ok, но HTTP 2xx — считаем ok=true (при отсутствии явной ошибки)
        if (!dto.Ok && string.IsNullOrWhiteSpace(dto.Error))
            dto.Ok = true;
    }

    // =========================
    // Auth / Profile / Economy
    // =========================

    // POST /api/launcher/login -> { deviceId, connectUrl, expiresAtUnix }
    public async Task<(string DeviceId, string ConnectUrl, long ExpiresAtUnix)> StartLauncherLoginAsync(CancellationToken ct)
    {
        using var resp = await SendAsyncWithRetry(
            factory: () =>
            {
                var req = new HttpRequestMessage(HttpMethod.Post, "api/launcher/login");
                return req;
            },
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

        var jsonBody = JsonSerializer.Serialize(reqModel, JsonOptions);

        using var resp = await SendAsyncWithRetry(
            factory: () =>
            {
                var req = new HttpRequestMessage(HttpMethod.Post, "api/launcher/events");
                EnsureBearer(req, accessToken);
                req.Content = new StringContent(jsonBody, Utf8NoBom, "application/json");
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

        var bodyJson = JsonSerializer.Serialize(new { query }, JsonOptions);

        using var resp = await SendAsyncWithRetry(
            factory: () =>
            {
                var req = new HttpRequestMessage(HttpMethod.Post, "api/launcher/friends/request");
                EnsureBearer(req, accessToken);
                req.Content = new StringContent(bodyJson, Utf8NoBom, "application/json");
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

    // POST /api/launcher/friends/accept body { fromUserId }
    public async Task<OkResponse> AcceptFriendRequestAsync(string accessToken, string fromUserId, CancellationToken ct)
    {
        fromUserId = (fromUserId ?? "").Trim();
        if (string.IsNullOrWhiteSpace(fromUserId))
            return new OkResponse { Ok = false, Error = "Не указан отправитель." };

        var bodyJson = JsonSerializer.Serialize(new { fromUserId }, JsonOptions);

        using var resp = await SendAsyncWithRetry(
            factory: () =>
            {
                var req = new HttpRequestMessage(HttpMethod.Post, "api/launcher/friends/accept");
                EnsureBearer(req, accessToken);
                req.Content = new StringContent(bodyJson, Utf8NoBom, "application/json");
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

    // POST /api/launcher/friends/decline body { fromUserId }
    public async Task<OkResponse> DeclineFriendRequestAsync(string accessToken, string fromUserId, CancellationToken ct)
    {
        fromUserId = (fromUserId ?? "").Trim();
        if (string.IsNullOrWhiteSpace(fromUserId))
            return new OkResponse { Ok = false, Error = "Не указан отправитель." };

        var bodyJson = JsonSerializer.Serialize(new { fromUserId }, JsonOptions);

        using var resp = await SendAsyncWithRetry(
            factory: () =>
            {
                var req = new HttpRequestMessage(HttpMethod.Post, "api/launcher/friends/decline");
                EnsureBearer(req, accessToken);
                req.Content = new StringContent(bodyJson, Utf8NoBom, "application/json");
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

    // POST /api/launcher/friends/remove body { userId }
    public async Task<OkResponse> RemoveFriendAsync(string accessToken, string userId, CancellationToken ct)
    {
        userId = (userId ?? "").Trim();
        if (string.IsNullOrWhiteSpace(userId))
            return new OkResponse { Ok = false, Error = "Не указан пользователь." };

        var bodyJson = JsonSerializer.Serialize(new { userId }, JsonOptions);

        using var resp = await SendAsyncWithRetry(
            factory: () =>
            {
                var req = new HttpRequestMessage(HttpMethod.Post, "api/launcher/friends/remove");
                EnsureBearer(req, accessToken);
                req.Content = new StringContent(bodyJson, Utf8NoBom, "application/json");
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
