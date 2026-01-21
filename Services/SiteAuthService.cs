using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LegendBorn.Models;

namespace LegendBorn.Services;

public sealed class SiteAuthService
{
    private const string SiteBaseUrl = "https://legendborn.ru/";

    // safety: ответы API не должны быть большими
    private const int MaxResponseBytes = 512 * 1024; // 512 KB

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    private static readonly HttpClient Http = CreateHttp();

    private static HttpClient CreateHttp()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            AllowAutoRedirect = true,
            MaxConnectionsPerServer = 8
        };

        var http = new HttpClient(handler)
        {
            BaseAddress = new Uri(SiteBaseUrl),
            Timeout = Timeout.InfiniteTimeSpan // таймауты per-request
        };

        http.DefaultRequestHeaders.UserAgent.ParseAdd(LauncherIdentity.UserAgent);
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return http;
    }

    private static string NormalizeToken(string token)
        => (token ?? string.Empty).Trim().Trim('"');

    private static bool IsRetryableStatus(HttpStatusCode code)
        => (int)code >= 500
           || code == HttpStatusCode.RequestTimeout
           || code == HttpStatusCode.TooManyRequests;

    private static async Task<string> ReadBodyAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);

        var buffer = new byte[16 * 1024];
        var total = 0;

        using var ms = new MemoryStream();

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

        for (int i = 1; i <= attempts; i++)
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

    // POST /api/launcher/login -> { deviceId, connectUrl, expiresAtUnix }
    public async Task<(string DeviceId, string ConnectUrl, long ExpiresAtUnix)> StartLauncherLoginAsync(CancellationToken ct)
    {
        using var resp = await SendAsyncWithRetry(
            factory: () => new HttpRequestMessage(HttpMethod.Post, "api/launcher/login"),
            ct: ct,
            attempts: 3,
            perTryTimeout: TimeSpan.FromSeconds(25));

        resp.EnsureSuccessStatusCode();

        var json = await ReadBodyAsync(resp, ct).ConfigureAwait(false);
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
        if (string.IsNullOrWhiteSpace(deviceId))
            return null;

        using var resp = await SendAsyncWithRetry(
            factory: () => new HttpRequestMessage(HttpMethod.Get, $"api/launcher/login?deviceId={Uri.EscapeDataString(deviceId)}"),
            ct: ct,
            attempts: 3,
            perTryTimeout: TimeSpan.FromSeconds(20));

        if (!resp.IsSuccessStatusCode)
            return null;

        var json = await ReadBodyAsync(resp, ct).ConfigureAwait(false);
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
        accessToken = NormalizeToken(accessToken);

        using var resp = await SendAsyncWithRetry(
            factory: () =>
            {
                var req = new HttpRequestMessage(HttpMethod.Get, "api/launcher/me");
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                return req;
            },
            ct: ct,
            attempts: 3,
            perTryTimeout: TimeSpan.FromSeconds(25));

        // ✅ Важно: даём наверх HttpRequestException со StatusCode — так легче правильно ловить unauthorized
        if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new HttpRequestException("Unauthorized (launcher token invalid/expired).", null, resp.StatusCode);

        resp.EnsureSuccessStatusCode();

        var json = await ReadBodyAsync(resp, ct).ConfigureAwait(false);

        return JsonSerializer.Deserialize<UserProfile>(json, JsonOptions)
               ?? new UserProfile { UserName = "Unknown", MinecraftName = "Player" };
    }

    // GET /api/launcher/economy/balance -> { currency:"RZN", balance:123 }
    public async Task<long> GetRezoniteBalanceAsync(string accessToken, CancellationToken ct)
    {
        accessToken = NormalizeToken(accessToken);

        using var resp = await SendAsyncWithRetry(
            factory: () =>
            {
                var req = new HttpRequestMessage(HttpMethod.Get, "api/launcher/economy/balance");
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                return req;
            },
            ct: ct,
            attempts: 3,
            perTryTimeout: TimeSpan.FromSeconds(25));

        if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new HttpRequestException("Unauthorized (launcher token invalid/expired).", null, resp.StatusCode);

        resp.EnsureSuccessStatusCode();

        var json = await ReadBodyAsync(resp, ct).ConfigureAwait(false);
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
        accessToken = NormalizeToken(accessToken);

        var reqModel = new LauncherEventRequest
        {
            Key = key ?? "",
            IdempotencyKey = idempotencyKey ?? "",
            Payload = payload
        };

        if (!reqModel.IsValid)
            throw new ArgumentException("key/idempotencyKey are required");

        var jsonBody = JsonSerializer.Serialize(reqModel, JsonOptions);

        using var resp = await SendAsyncWithRetry(
            factory: () =>
            {
                var req = new HttpRequestMessage(HttpMethod.Post, "api/launcher/events");
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                req.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
                return req;
            },
            ct: ct,
            attempts: 3,
            perTryTimeout: TimeSpan.FromSeconds(30));

        if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new HttpRequestException("Unauthorized (launcher token invalid/expired).", null, resp.StatusCode);

        if (!resp.IsSuccessStatusCode)
            return null;

        var respJson = await ReadBodyAsync(resp, ct).ConfigureAwait(false);

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
            return JsonSerializer.Deserialize<LauncherEventResponse>(respJson, JsonOptions);
        }
    }
}
