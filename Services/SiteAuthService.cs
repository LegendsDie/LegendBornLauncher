using System;
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

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    // ✅ HttpClient живёт долго + timeout + user-agent + decompression
    private static readonly HttpClient Http = CreateHttp();

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All
        })
        {
            BaseAddress = new Uri(SiteBaseUrl),
            Timeout = TimeSpan.FromSeconds(60)
        };

        http.DefaultRequestHeaders.UserAgent.ParseAdd("LegendBornLauncher/0.1.6");
        return http;
    }

    private static string NormalizeToken(string token)
    {
        // На практике часто прилетает token с кавычками или пробелами
        return (token ?? string.Empty).Trim().Trim('"');
    }

    private static async Task<string> ReadBodyAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        // ReadAsStringAsync(ct) не везде доступен (зависит от TF), поэтому так:
        var s = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        return s ?? string.Empty;
    }

    private static string TryGetString(JsonElement root, string name)
        => root.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String
            ? (p.GetString() ?? "")
            : "";

    private static long TryGetInt64(JsonElement root, string name)
        => root.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt64(out var v)
            ? v
            : 0;

    // POST /api/launcher/login -> { deviceId, connectUrl, expiresAtUnix }
    public async Task<(string DeviceId, string ConnectUrl, long ExpiresAtUnix)> StartLauncherLoginAsync(CancellationToken ct)
    {
        using var resp = await Http.PostAsync("api/launcher/login", content: null, ct).ConfigureAwait(false);
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

        using var resp = await Http.GetAsync($"api/launcher/login?deviceId={Uri.EscapeDataString(deviceId)}", ct)
            .ConfigureAwait(false);

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

        using var req = new HttpRequestMessage(HttpMethod.Get, "api/launcher/me");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);

        if (resp.StatusCode == HttpStatusCode.Unauthorized)
            throw new UnauthorizedAccessException("Unauthorized (launcher token invalid/expired).");

        resp.EnsureSuccessStatusCode();

        var json = await ReadBodyAsync(resp, ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize<UserProfile>(json, JsonOptions)
               ?? new UserProfile { UserName = "Unknown", MinecraftName = "Player" };
    }

    // GET /api/launcher/economy/balance -> { currency:"RZN", balance:123 }
    public async Task<long> GetRezoniteBalanceAsync(string accessToken, CancellationToken ct)
    {
        accessToken = NormalizeToken(accessToken);

        using var req = new HttpRequestMessage(HttpMethod.Get, "api/launcher/economy/balance");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);

        if (resp.StatusCode == HttpStatusCode.Unauthorized)
            throw new UnauthorizedAccessException("Unauthorized (launcher token invalid/expired).");

        resp.EnsureSuccessStatusCode();

        var json = await ReadBodyAsync(resp, ct).ConfigureAwait(false);
        var dto = JsonSerializer.Deserialize<RezoniteBalanceResponse>(json, JsonOptions);

        return dto?.Balance ?? 0;
    }

    // POST /api/launcher/events
    // body: { key, idempotencyKey, payload }
    public async Task<LauncherEventResponse?> SendLauncherEventAsync(
        string accessToken,
        string key,
        string idempotencyKey,
        object? payload,
        CancellationToken ct)
    {
        accessToken = NormalizeToken(accessToken);

        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("key is required", nameof(key));
        if (string.IsNullOrWhiteSpace(idempotencyKey))
            throw new ArgumentException("idempotencyKey is required", nameof(idempotencyKey));

        var body = new LauncherEventRequest
        {
            Key = key.Trim(),
            IdempotencyKey = idempotencyKey.Trim(),
            Payload = payload
        };

        var jsonBody = JsonSerializer.Serialize(body, JsonOptions);

        using var req = new HttpRequestMessage(HttpMethod.Post, "api/launcher/events");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        req.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

        using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);

        if (resp.StatusCode == HttpStatusCode.Unauthorized)
            throw new UnauthorizedAccessException("Unauthorized (launcher token invalid/expired).");

        if (!resp.IsSuccessStatusCode)
            return null;

        var respJson = await ReadBodyAsync(resp, ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize<LauncherEventResponse>(respJson, JsonOptions);
    }

    // ===== DTO =====
    private sealed class RezoniteBalanceResponse
    {
        public string? Currency { get; set; }
        public long Balance { get; set; }
    }

    private sealed class LauncherEventRequest
    {
        public string Key { get; set; } = "";
        public string IdempotencyKey { get; set; } = "";
        public object? Payload { get; set; }
    }

    public sealed class LauncherEventResponse
    {
        public bool Ok { get; set; }
        public bool Duplicated { get; set; }
        public string? Error { get; set; }
        public long? Balance { get; set; }
        public string? Currency { get; set; }
    }
}
