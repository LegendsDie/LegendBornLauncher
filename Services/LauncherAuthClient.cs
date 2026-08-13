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

/// <summary>
/// Canonical client for the device-link protocol implemented by legendbornweb.
/// This class intentionally keeps auth semantics separate from the larger legacy
/// SiteAuthService so HTTP status/code handling cannot be silently flattened to null.
/// </summary>
public sealed class LauncherAuthClient
{
    public const string ProductionOrigin = "https://legendborn.xyz/";

    private const int MaxBodyBytes = 64 * 1024;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(25);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    private static readonly HttpClient Http = CreateHttpClient();

    public enum PollState
    {
        Pending = 0,
        Authorized = 1,
        TerminalError = 2,
        TransientError = 3
    }

    public sealed record StartResult(string DeviceId, string ConnectUrl, long ExpiresAtUnix);

    public sealed record PollResult(
        PollState State,
        AuthTokens? Tokens,
        string? Code,
        string? Message,
        int HttpStatus,
        long ExpiresAtUnix,
        TimeSpan? RetryAfter)
    {
        public static PollResult Pending(long expiresAtUnix) =>
            new(PollState.Pending, null, null, null, 200, expiresAtUnix, null);

        public static PollResult Authorized(AuthTokens tokens) =>
            new(PollState.Authorized, tokens, null, null, 200, tokens.ExpiresAtUnixSeconds, null);
    }

    public sealed class LauncherAuthException : Exception
    {
        public LauncherAuthException(string message, int httpStatus, string? code = null)
            : base(message)
        {
            HttpStatus = httpStatus;
            Code = code;
        }

        public int HttpStatus { get; }
        public string? Code { get; }
    }

    private static HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            Proxy = WebRequest.DefaultWebProxy,
            UseProxy = true,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            MaxConnectionsPerServer = 8,

            // API redirects are treated as configuration errors. In particular a POST
            // must never be silently redirected to another legacy/locale origin.
            AllowAutoRedirect = false
        };

        var http = new HttpClient(handler)
        {
            BaseAddress = new Uri(ProductionOrigin, UriKind.Absolute),
            Timeout = Timeout.InfiniteTimeSpan
        };

        http.DefaultRequestHeaders.Accept.Clear();
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        try
        {
            http.DefaultRequestHeaders.UserAgent.ParseAdd(LauncherIdentity.UserAgent);
        }
        catch
        {
            http.DefaultRequestHeaders.UserAgent.Clear();
            http.DefaultRequestHeaders.UserAgent.ParseAdd("LegendBornLauncher/0");
        }

        return http;
    }

    public async Task<StartResult> StartAsync(CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/launcher/login");
        using var response = await SendAsync(request, ct).ConfigureAwait(false);
        var body = await ReadBodyLimitedAsync(response, ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            throw BuildException(response, body, "Не удалось создать запрос авторизации Launcher.");

        using var json = ParseJson(body);
        var root = json.RootElement;

        var deviceId = GetString(root, "deviceId");
        var connectUrl = GetString(root, "connectUrl");
        var expiresAtUnix = NormalizeUnix(GetInt64(root, "expiresAtUnix"));

        if (!Guid.TryParse(deviceId, out _) || string.IsNullOrWhiteSpace(connectUrl))
            throw new LauncherAuthException("Сайт вернул некорректный ответ запуска авторизации.", 200, "INVALID_START_RESPONSE");

        return new StartResult(deviceId, connectUrl, expiresAtUnix);
    }

    public async Task<PollResult> PollAsync(string deviceId, CancellationToken ct)
    {
        deviceId = (deviceId ?? string.Empty).Trim();
        if (!Guid.TryParse(deviceId, out _))
        {
            return new PollResult(
                PollState.TerminalError,
                null,
                "INVALID_DEVICE_ID",
                "Некорректный идентификатор Launcher.",
                400,
                0,
                null);
        }

        var path = $"api/launcher/login?deviceId={Uri.EscapeDataString(deviceId)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        using var response = await SendAsync(request, ct).ConfigureAwait(false);
        var body = await ReadBodyLimitedAsync(response, ct).ConfigureAwait(false);

        if (response.IsSuccessStatusCode)
        {
            using var json = ParseJson(body);
            var root = json.RootElement;
            var status = GetString(root, "status");

            if (status.Equals("PENDING", StringComparison.OrdinalIgnoreCase))
                return PollResult.Pending(NormalizeUnix(GetInt64(root, "expiresAtUnix")));

            if (status.Equals("OK", StringComparison.OrdinalIgnoreCase))
            {
                var token = GetString(root, "accessToken").Trim().Trim('"');
                var expiresAtUnix = NormalizeUnix(GetInt64(root, "expiresAtUnix"));

                if (string.IsNullOrWhiteSpace(token))
                {
                    return new PollResult(
                        PollState.TerminalError,
                        null,
                        "INVALID_TOKEN_RESPONSE",
                        "Сайт подтвердил вход, но не вернул Launcher-токен.",
                        200,
                        expiresAtUnix,
                        null);
                }

                return PollResult.Authorized(new AuthTokens
                {
                    AccessToken = token,
                    ExpiresAtUnix = expiresAtUnix
                });
            }

            return new PollResult(
                PollState.TerminalError,
                null,
                "UNKNOWN_LOGIN_STATUS",
                "Сайт вернул неизвестный статус авторизации Launcher.",
                200,
                NormalizeUnix(GetInt64(root, "expiresAtUnix")),
                null);
        }

        var code = ExtractString(body, "code");
        var message = ExtractError(body);
        var statusCode = (int)response.StatusCode;
        var retryAfter = GetRetryAfter(response);

        if (response.StatusCode == HttpStatusCode.TooManyRequests || statusCode >= 500)
        {
            return new PollResult(
                PollState.TransientError,
                null,
                code,
                message,
                statusCode,
                0,
                retryAfter);
        }

        // legendbornweb uses 400/404/409/410 for invalid, missing, consumed and expired links.
        return new PollResult(
            PollState.TerminalError,
            null,
            code,
            message,
            statusCode,
            0,
            retryAfter);
    }

    public async Task<bool> RevokeAsync(string accessToken, CancellationToken ct)
    {
        accessToken = (accessToken ?? string.Empty).Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(accessToken))
            return true;

        using var request = new HttpRequestMessage(HttpMethod.Post, "api/launcher/logout");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await SendAsync(request, ct).ConfigureAwait(false);

        // An already expired/revoked token is equivalent to logged out from the client's perspective.
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            return true;

        return response.IsSuccessStatusCode;
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(RequestTimeout);

        try
        {
            return await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new LauncherAuthException("Истекло время ожидания ответа LegendBorn.", 0, "AUTH_TIMEOUT");
        }
        catch (HttpRequestException ex)
        {
            throw new LauncherAuthException($"Не удалось подключиться к LegendBorn: {ex.Message}", 0, "AUTH_NETWORK_ERROR");
        }
    }

    private static async Task<string> ReadBodyLimitedAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength is long length && length > MaxBodyBytes)
            throw new LauncherAuthException("Ответ auth API слишком большой.", (int)response.StatusCode, "AUTH_RESPONSE_TOO_LARGE");

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var memory = new MemoryStream(capacity: 4096);
        var buffer = new byte[8192];
        var total = 0;

        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false);
            if (read <= 0) break;

            total += read;
            if (total > MaxBodyBytes)
                throw new LauncherAuthException("Ответ auth API слишком большой.", (int)response.StatusCode, "AUTH_RESPONSE_TOO_LARGE");

            memory.Write(buffer, 0, read);
        }

        return Encoding.UTF8.GetString(memory.GetBuffer(), 0, checked((int)memory.Length));
    }

    private static JsonDocument ParseJson(string body)
    {
        try
        {
            return JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
        }
        catch (JsonException)
        {
            throw new LauncherAuthException("Auth API вернул некорректный JSON.", 0, "INVALID_AUTH_JSON");
        }
    }

    private static LauncherAuthException BuildException(HttpResponseMessage response, string body, string fallback)
    {
        var code = ExtractString(body, "code");
        var message = ExtractError(body);
        if (string.IsNullOrWhiteSpace(message)) message = fallback;

        if ((int)response.StatusCode is >= 300 and < 400)
        {
            var location = response.Headers.Location?.ToString();
            message = string.IsNullOrWhiteSpace(location)
                ? "Auth API неожиданно вернул redirect. Проверь production origin."
                : $"Auth API неожиданно вернул redirect на {location}.";
            code = "AUTH_REDIRECT_REJECTED";
        }

        return new LauncherAuthException(message, (int)response.StatusCode, code);
    }

    private static string ExtractError(string body)
    {
        try
        {
            using var json = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
            var root = json.RootElement;
            var error = GetString(root, "error");
            if (!string.IsNullOrWhiteSpace(error)) return error;
            return GetString(root, "message");
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string ExtractString(string body, string property)
    {
        try
        {
            using var json = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
            return GetString(json.RootElement, property);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string GetString(JsonElement root, string property)
        => root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static long GetInt64(JsonElement root, string property)
        => root.TryGetProperty(property, out var value) &&
           value.ValueKind == JsonValueKind.Number &&
           value.TryGetInt64(out var number)
            ? number
            : 0;

    private static long NormalizeUnix(long value)
        => value >= 10_000_000_000L ? value / 1000 : Math.Max(0, value);

    private static TimeSpan? GetRetryAfter(HttpResponseMessage response)
    {
        try
        {
            var retry = response.Headers.RetryAfter;
            if (retry?.Delta is TimeSpan delta)
                return ClampRetryAfter(delta);

            if (retry?.Date is DateTimeOffset date)
                return ClampRetryAfter(date - DateTimeOffset.UtcNow);
        }
        catch
        {
        }

        return null;
    }

    private static TimeSpan ClampRetryAfter(TimeSpan value)
    {
        if (value < TimeSpan.FromMilliseconds(500)) return TimeSpan.FromMilliseconds(500);
        if (value > TimeSpan.FromSeconds(10)) return TimeSpan.FromSeconds(10);
        return value;
    }
}
