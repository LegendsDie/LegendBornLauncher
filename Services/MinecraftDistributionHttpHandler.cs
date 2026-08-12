using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace LegendBorn.Services;

/// <summary>
/// Resilient GET/HEAD transport for CmlLib Minecraft distribution traffic.
///
/// Normal networks keep using Mojang directly. If an official distribution host times out or
/// returns a transient/block-like status, that host is temporarily marked degraded and subsequent
/// requests prefer the LegendBorn fixed-origin proxy. For URL layouts that BMCLAPI documents as
/// Mojang-compatible, BMCLAPI is an independent mirror before retrying the official host.
///
/// Integrity is still enforced by CmlLib's normal SHA-1 checks; this handler only changes transport.
/// </summary>
internal sealed class MinecraftDistributionHttpHandler : HttpMessageHandler
{
    internal const string LegendBornMirrorBase = "https://legendborn.xyz/api/mirror/mojang/";
    internal const string BmclApiBase = "https://bmclapi2.bangbang93.com/";

    private static readonly TimeSpan DirectAttemptTimeout = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan MirrorAttemptTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan DegradedHostTtl = TimeSpan.FromMinutes(15);

    private static readonly ConcurrentDictionary<string, long> DegradedOfficialHosts =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly HttpMessageInvoker _transport;
    private readonly Action<string>? _log;

    public MinecraftDistributionHttpHandler(HttpMessageHandler innerHandler, Action<string>? log = null)
    {
        _transport = new HttpMessageInvoker(
            innerHandler ?? throw new ArgumentNullException(nameof(innerHandler)),
            disposeHandler: true);
        _log = log;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.RequestUri is null)
            throw new InvalidOperationException("HTTP request URI is missing.");

        if ((request.Method != HttpMethod.Get && request.Method != HttpMethod.Head) ||
            request.Content is not null ||
            !TryGetOfficialOrigin(request.RequestUri, out var originKey))
        {
            return await SendCloneAsync(
                    request,
                    request.RequestUri,
                    cancellationToken,
                    timeout: null)
                .ConfigureAwait(false);
        }

        var original = request.RequestUri;
        var hostDegraded = IsHostDegraded(original.Host);
        var mirrors = BuildMirrorCandidates(original, originKey).ToArray();

        if (!hostDegraded)
        {
            var direct = await TrySendCandidateAsync(
                    request,
                    original,
                    cancellationToken,
                    DirectAttemptTimeout,
                    mirrorCandidate: false)
                .ConfigureAwait(false);

            if (direct.Response is not null && !direct.ShouldFailOver)
                return direct.Response;

            direct.Response?.Dispose();
            if (direct.ShouldFailOver)
                MarkHostDegraded(original.Host);
        }

        foreach (var mirror in mirrors)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var mirrored = await TrySendCandidateAsync(
                    request,
                    mirror.Uri,
                    cancellationToken,
                    MirrorAttemptTimeout,
                    mirrorCandidate: true)
                .ConfigureAwait(false);

            if (mirrored.Response is not null && !mirrored.ShouldFailOver)
            {
                _log?.Invoke($"Minecraft CDN: fallback -> {mirror.Label} ({original.Host})");
                return mirrored.Response;
            }

            mirrored.Response?.Dispose();
        }

        // A degraded marker is only an optimization. If every mirror failed, give the official
        // endpoint one final attempt so a recovered Mojang host can immediately heal the process.
        var finalDirect = await SendCloneAsync(
                request,
                original,
                cancellationToken,
                timeout: MirrorAttemptTimeout)
            .ConfigureAwait(false);

        if (IsUsable(finalDirect.StatusCode))
            ClearHostDegraded(original.Host);

        return finalDirect;
    }

    private async Task<(HttpResponseMessage? Response, bool ShouldFailOver)> TrySendCandidateAsync(
        HttpRequestMessage source,
        Uri target,
        CancellationToken cancellationToken,
        TimeSpan timeout,
        bool mirrorCandidate)
    {
        try
        {
            var response = await SendCloneAsync(source, target, cancellationToken, timeout).ConfigureAwait(false);

            // For the official source, preserve authoritative non-transient errors such as 404.
            // For a mirror/proxy, any unusable response means "try the next transport".
            var failOver = mirrorCandidate
                ? !IsUsable(response.StatusCode)
                : ShouldOfficialFailOver(response.StatusCode);

            if (!mirrorCandidate && !failOver && IsUsable(response.StatusCode))
                ClearHostDegraded(target.Host);

            return (response, failOver);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (null, true);
        }
        catch (HttpRequestException)
        {
            return (null, true);
        }
    }

    private async Task<HttpResponseMessage> SendCloneAsync(
        HttpRequestMessage source,
        Uri target,
        CancellationToken cancellationToken,
        TimeSpan? timeout)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (timeout.HasValue)
            linked.CancelAfter(timeout.Value);

        using var clone = new HttpRequestMessage(source.Method, target)
        {
            Version = source.Version,
            VersionPolicy = source.VersionPolicy
        };

        foreach (var header in source.Headers)
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);

        return await _transport.SendAsync(clone, linked.Token).ConfigureAwait(false);
    }

    private static bool IsUsable(HttpStatusCode status)
    {
        var code = (int)status;
        return code is >= 200 and <= 299 || status == HttpStatusCode.NotModified;
    }

    private static bool ShouldOfficialFailOver(HttpStatusCode status)
    {
        var code = (int)status;
        return status == HttpStatusCode.Forbidden ||
               status == HttpStatusCode.RequestTimeout ||
               status == (HttpStatusCode)429 ||
               status == HttpStatusCode.BadGateway ||
               status == HttpStatusCode.ServiceUnavailable ||
               status == HttpStatusCode.GatewayTimeout ||
               code is >= 500 and <= 599;
    }

    private static IEnumerable<(Uri Uri, string Label)> BuildMirrorCandidates(Uri original, string originKey)
    {
        var legendBorn = BuildLegendBornProxyUri(original, originKey);
        if (legendBorn is not null)
            yield return (legendBorn, "LegendBorn");

        var bmcl = BuildBmclApiUri(original, originKey);
        if (bmcl is not null && !UriEquals(legendBorn, bmcl))
            yield return (bmcl, "BMCLAPI");
    }

    private static Uri? BuildLegendBornProxyUri(Uri original, string originKey)
    {
        try
        {
            var path = original.AbsolutePath.TrimStart('/');
            if (path.Length == 0) return null;

            var target = new Uri(new Uri(LegendBornMirrorBase, UriKind.Absolute), originKey + "/" + path);
            return new UriBuilder(target) { Query = original.Query.TrimStart('?') }.Uri;
        }
        catch
        {
            return null;
        }
    }

    private static Uri? BuildBmclApiUri(Uri original, string originKey)
    {
        try
        {
            var path = original.AbsolutePath.TrimStart('/');
            if (path.Length == 0) return null;

            string mappedPath = originKey switch
            {
                "launchermeta" => path,
                "launcher" => path,
                "libraries" => "maven/" + path,
                "resources" => "assets/" + path,
                _ => ""
            };

            if (mappedPath.Length == 0) return null;

            var target = new Uri(new Uri(BmclApiBase, UriKind.Absolute), mappedPath);
            return new UriBuilder(target) { Query = original.Query.TrimStart('?') }.Uri;
        }
        catch
        {
            return null;
        }
    }

    private static bool TryGetOfficialOrigin(Uri uri, out string originKey)
    {
        originKey = uri.Host.ToLowerInvariant() switch
        {
            "launchermeta.mojang.com" => "launchermeta",
            "piston-meta.mojang.com" => "piston-meta",
            "piston-data.mojang.com" => "piston-data",
            "libraries.minecraft.net" => "libraries",
            "resources.download.minecraft.net" => "resources",
            "launcher.mojang.com" => "launcher",
            _ => ""
        };

        return originKey.Length > 0 && uri.Scheme == Uri.UriSchemeHttps;
    }

    private static bool IsHostDegraded(string host)
    {
        if (!DegradedOfficialHosts.TryGetValue(host, out var untilUnix))
            return false;

        if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() < untilUnix)
            return true;

        DegradedOfficialHosts.TryRemove(host, out _);
        return false;
    }

    private static void MarkHostDegraded(string host)
    {
        var until = DateTimeOffset.UtcNow.Add(DegradedHostTtl).ToUnixTimeSeconds();
        DegradedOfficialHosts[host] = until;
    }

    private static void ClearHostDegraded(string host)
        => DegradedOfficialHosts.TryRemove(host, out _);

    private static bool UriEquals(Uri? left, Uri? right)
        => left is not null && right is not null &&
           string.Equals(left.AbsoluteUri, right.AbsoluteUri, StringComparison.OrdinalIgnoreCase);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _transport.Dispose();
        base.Dispose(disposing);
    }
}
