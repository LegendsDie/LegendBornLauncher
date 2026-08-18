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

/// <summary>
/// Thin launcher client for the already-existing LegendBorn server nickname API.
/// The Web backend remains authoritative; the launcher only edits and displays that state.
/// </summary>
public sealed class LauncherServerNickService
{
    private const string SiteBaseUrl = "https://legendborn.xyz/";
    private const string ServerNickPath = "api/minecraft/server-nick";
    private const int MaxResponseBytes = 128 * 1024;
    private const int Attempts = 3;

    private static readonly HttpClient Http = CreateHttp();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    public sealed class RulesDto
    {
        [JsonPropertyName("minLength")]
        public int MinLength { get; set; } = 3;

        [JsonPropertyName("maxLength")]
        public int MaxLength { get; set; } = 24;

        [JsonPropertyName("caseInsensitiveUnique")]
        public bool CaseInsensitiveUnique { get; set; } = true;
    }

    public sealed class ServerNickResponse
    {
        [JsonPropertyName("ok")]
        public bool Ok { get; set; }

        [JsonPropertyName("error")]
        public string? Error { get; set; }

        [JsonPropertyName("message")]
        public string? Message { get; set; }

        [JsonPropertyName("code")]
        public string? Code { get; set; }

        [JsonPropertyName("minecraftUsername")]
        public string? MinecraftUsername { get; set; }

        [JsonPropertyName("serverNick")]
        public string? ServerNick { get; set; }

        [JsonPropertyName("effectiveServerNick")]
        public string? EffectiveServerNick { get; set; }

        [JsonPropertyName("rules")]
        public RulesDto? Rules { get; set; }

        [JsonIgnore]
        public HttpStatusCode? HttpStatus { get; set; }
    }

    private sealed class UpdateRequest
    {
        [JsonPropertyName("serverNick")]
        public string? ServerNick { get; init; }
    }

    public Task<ServerNickResponse> GetAsync(string accessToken, CancellationToken ct)
        => SendAsync(HttpMethod.Get, accessToken, serverNick: null, ct);

    public Task<ServerNickResponse> PutAsync(string accessToken, string? serverNick, CancellationToken ct)
        => SendAsync(HttpMethod.Put, accessToken, serverNick, ct);

    private static async Task<ServerNickResponse> SendAsync(
        HttpMethod method,
        string accessToken,
        string? serverNick,
        CancellationToken ct)
    {
        var token = NormalizeToken(accessToken);
        if (token.Length == 0)
        {
            return new ServerNickResponse
            {
                Ok = false,
                Error = "EMPTY_TOKEN"
            };
        }

        Exception? last = null;

        for (var attempt = 1; attempt <= Attempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(12));

                using var request = CreateRequest(method, token, serverNick);
                using var response = await Http.SendAsync(
                        request,
                        HttpCompletionOption.ResponseHeadersRead,
                        timeout.Token)
                    .ConfigureAwait(false);

                if (IsTransient(response.StatusCode) && attempt < Attempts)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt), ct).ConfigureAwait(false);
                    continue;
                }

                var body = await ReadBodyLimitedAsync(response, timeout.Token).ConfigureAwait(false);
                var dto = DeserializeResponse(body);
                dto.HttpStatus = response.StatusCode;

                if (!response.IsSuccessStatusCode)
                {
                    dto.Ok = false;
                    dto.Error ??= dto.Message ?? $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}";
                }
                else if (!dto.Ok && string.IsNullOrWhiteSpace(dto.Error) && string.IsNullOrWhiteSpace(dto.Message))
                {
                    // The current API returns ok=true, but fail open for forward-compatible 2xx responses
                    // that keep the same data contract and omit the explicit flag.
                    dto.Ok = true;
                }

                return dto;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (attempt < Attempts)
            {
                last = ex;
                await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt), ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                last = ex;
            }
        }

        return new ServerNickResponse
        {
            Ok = false,
            Error = last?.Message ?? "SERVER_NICK_UNAVAILABLE"
        };
    }

    private static HttpRequestMessage CreateRequest(HttpMethod method, string token, string? serverNick)
    {
        var request = new HttpRequestMessage(method, ServerNickPath)
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower
        };

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
        request.Headers.TryAddWithoutValidation("X-Client", "LegendBornLauncher");
        request.Headers.TryAddWithoutValidation("X-Client-Version", LauncherIdentity.InformationalVersion ?? string.Empty);

        if (method == HttpMethod.Put)
        {
            var json = JsonSerializer.Serialize(new UpdateRequest { ServerNick = serverNick }, JsonOptions);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        return request;
    }

    private static ServerNickResponse DeserializeResponse(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return new ServerNickResponse();

        try
        {
            return JsonSerializer.Deserialize<ServerNickResponse>(body, JsonOptions)
                   ?? new ServerNickResponse();
        }
        catch (JsonException)
        {
            return new ServerNickResponse
            {
                Ok = false,
                Error = "Сервер вернул некорректный ответ."
            };
        }
    }

    private static async Task<string> ReadBodyLimitedAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength is long length && length > MaxResponseBytes)
            throw new InvalidOperationException("Ответ API игрового ника слишком большой.");

        await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);

        try
        {
            using var output = new MemoryStream();
            while (true)
            {
                var read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false);
                if (read == 0) break;

                if (output.Length + read > MaxResponseBytes)
                    throw new InvalidOperationException("Ответ API игрового ника слишком большой.");

                output.Write(buffer, 0, read);
            }

            return Encoding.UTF8.GetString(output.GetBuffer(), 0, checked((int)output.Length));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static string NormalizeToken(string token)
    {
        var value = (token ?? string.Empty).Trim().Trim('"');
        if (value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            value = value[7..].Trim();
        return value;
    }

    private static bool IsTransient(HttpStatusCode code)
        => (int)code >= 500 ||
           code is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests;

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
            Timeout = Timeout.InfiniteTimeSpan
        };

        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        try { http.DefaultRequestHeaders.UserAgent.ParseAdd(LauncherIdentity.UserAgent ?? "LegendBornLauncher"); } catch { }
        return http;
    }
}
