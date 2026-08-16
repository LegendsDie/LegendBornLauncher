using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace LegendBorn.Services;

/// <summary>
/// Small read-only dashboard feed. The dedicated /api/launcher/news contract is preferred when
/// available; until the website exposes that feed, /api/launcher/latest provides one real current
/// release card instead of inventing placeholder news.
/// </summary>
public static class LauncherNewsService
{
    private const string NewsUrl = "https://legendborn.xyz/api/launcher/news";
    private const string LatestUrl = "https://legendborn.xyz/api/launcher/latest";
    private const int MaxBodyBytes = 512 * 1024;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(7);
    private static readonly HttpClient Http = CreateHttp();

    public sealed record NewsItem(string Title, string Summary, string Date, string? Url);

    private sealed class NewsEnvelope
    {
        [JsonPropertyName("ok")] public bool Ok { get; set; }
        [JsonPropertyName("items")] public List<NewsDto>? Items { get; set; }
        [JsonPropertyName("news")] public List<NewsDto>? News { get; set; }
    }

    private sealed class NewsDto
    {
        [JsonPropertyName("title")] public string? Title { get; set; }
        [JsonPropertyName("summary")] public string? Summary { get; set; }
        [JsonPropertyName("excerpt")] public string? Excerpt { get; set; }
        [JsonPropertyName("date")] public string? Date { get; set; }
        [JsonPropertyName("publishedAt")] public string? PublishedAt { get; set; }
        [JsonPropertyName("url")] public string? Url { get; set; }
    }

    private sealed class LatestEnvelope
    {
        [JsonPropertyName("ok")] public bool Ok { get; set; }
        [JsonPropertyName("launcher")] public LatestLauncher? Launcher { get; set; }
    }

    private sealed class LatestLauncher
    {
        [JsonPropertyName("version")] public string? Version { get; set; }
        [JsonPropertyName("publishedAt")] public string? PublishedAt { get; set; }
        [JsonPropertyName("notes")] public string? Notes { get; set; }
    }

    public static async Task<IReadOnlyList<NewsItem>> GetLatestAsync(CancellationToken ct = default)
    {
        var dedicated = await TryGetDedicatedFeedAsync(ct).ConfigureAwait(false);
        if (dedicated.Count > 0)
            return dedicated;

        var latest = await TryGetLatestReleaseAsync(ct).ConfigureAwait(false);
        return latest is null ? Array.Empty<NewsItem>() : new[] { latest };
    }

    private static async Task<IReadOnlyList<NewsItem>> TryGetDedicatedFeedAsync(CancellationToken ct)
    {
        try
        {
            var bytes = await GetJsonAsync(NewsUrl, ct).ConfigureAwait(false);
            if (bytes is null) return Array.Empty<NewsItem>();

            var envelope = JsonSerializer.Deserialize<NewsEnvelope>(bytes, JsonOptions);
            if (envelope?.Ok != true) return Array.Empty<NewsItem>();

            var source = envelope.Items ?? envelope.News ?? new List<NewsDto>();
            return source
                .Select(ToNewsItem)
                .Where(static x => x is not null)
                .Select(static x => x!)
                .Take(9)
                .ToArray();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return Array.Empty<NewsItem>(); }
    }

    private static async Task<NewsItem?> TryGetLatestReleaseAsync(CancellationToken ct)
    {
        try
        {
            var bytes = await GetJsonAsync(LatestUrl, ct).ConfigureAwait(false);
            if (bytes is null) return null;

            var envelope = JsonSerializer.Deserialize<LatestEnvelope>(bytes, JsonOptions);
            var launcher = envelope?.Ok == true ? envelope.Launcher : null;
            if (launcher is null) return null;

            var version = Clean(launcher.Version);
            var notes = Shorten(Clean(launcher.Notes), 220);
            var title = version.Length > 0 ? $"LegendBorn Launcher {version}" : "Обновление LegendBorn Launcher";
            var summary = notes.Length > 0 ? notes : "Доступна актуальная версия LegendBorn Launcher.";

            return new NewsItem(title, summary, FormatDate(launcher.PublishedAt), "https://legendborn.xyz/");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return null; }
    }

    private static NewsItem? ToNewsItem(NewsDto dto)
    {
        var title = Clean(dto.Title);
        if (title.Length == 0) return null;

        var summary = Shorten(Clean(dto.Summary ?? dto.Excerpt), 220);
        var date = Clean(dto.Date);
        if (date.Length == 0) date = FormatDate(dto.PublishedAt);

        var url = NormalizeLegendBornUrl(dto.Url);
        return new NewsItem(title, summary, date, url);
    }

    private static string? NormalizeLegendBornUrl(string? raw)
    {
        var value = Clean(raw);
        if (value.Length == 0) return null;

        if (value.StartsWith('/'))
            value = "https://legendborn.xyz" + value;

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            return null;

        // Dashboard links remain first-party. External URLs should be opened from the website itself.
        return uri.Host.Equals("legendborn.xyz", StringComparison.OrdinalIgnoreCase)
            ? uri.AbsoluteUri
            : null;
    }

    private static async Task<byte[]?> GetJsonAsync(string url, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(RequestTimeout);

        using var request = new HttpRequestMessage(HttpMethod.Get, url)
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode || response.RequestMessage?.RequestUri is not { Scheme: "https" })
            return null;

        if (response.Content.Headers.ContentLength is long declared && declared > MaxBodyBytes)
            return null;

        var bytes = await response.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false);
        return bytes.Length <= MaxBodyBytes ? bytes : null;
    }

    private static string FormatDate(string? raw)
    {
        var text = Clean(raw);
        if (text.Length == 0) return string.Empty;

        if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var value))
            return value.ToLocalTime().ToString("dd.MM.yyyy", CultureInfo.GetCultureInfo("ru-RU"));

        return text;
    }

    private static string Clean(string? value)
        => (value ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();

    private static string Shorten(string value, int max)
        => value.Length <= max ? value : value[..Math.Max(1, max - 1)].TrimEnd() + "…";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    private static HttpClient CreateHttp()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            Proxy = WebRequest.DefaultWebProxy,
            UseProxy = true,
            ConnectTimeout = TimeSpan.FromSeconds(4),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            MaxConnectionsPerServer = 2,
            AllowAutoRedirect = true
        };
        return new HttpClient(handler) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
    }
}
