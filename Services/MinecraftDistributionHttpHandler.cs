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
/// Resilient GET/HEAD transport for CmlLib Minecraft/NeoForge distribution traffic.
/// It never routes large binaries through the LegendBorn Next.js app. NeoForge Maven uses the
/// mirror bases received from the authoritative launcher catalog; known Mojang layouts may use
/// BMCLAPI before the official source is retried. Integrity remains enforced by the caller/CmlLib.
/// </summary>
internal sealed class MinecraftDistributionHttpHandler : HttpMessageHandler
{
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
        var preferMirrors = string.Equals(originKey, "neoforge-maven", StringComparison.Ordinal);
        var hostDegraded = IsHostDegraded(original.Host);
        var mirrors = BuildMirrorCandidates(original, originKey).ToArray();

        if (!preferMirrors && !hostDegraded)
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
            if (UriEquals(original, mirror.Uri))
                continue;

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
                if (string.Equals(mirror.Label, "BMCLAPI", StringComparison.Ordinal))
                    _log?.Invoke("Minecraft CDN: используется источник BMCLAPI.");
                return mirrored.Response;
            }

            mirrored.Response?.Dispose();
        }

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
        if (string.Equals(originKey, "neoforge-maven", StringComparison.Ordinal))
        {
            foreach (var candidate in BuildNeoForgeMavenCandidates(original))
                yield return candidate;
            yield break;
        }

        var bmcl = BuildBmclApiUri(original, originKey);
        if (bmcl is not null)
            yield return (bmcl, "BMCLAPI");
    }

    private static IEnumerable<(Uri Uri, string Label)> BuildNeoForgeMavenCandidates(Uri original)
    {
        var relative = original.AbsolutePath.TrimStart('/');
        if (relative.StartsWith("releases/", StringComparison.OrdinalIgnoreCase))
            relative = relative["releases/".Length..];
        if (relative.Length == 0)
            yield break;

        foreach (var mirror in NeoForgeDistributionBootstrap.GetRegisteredMavenMirrors())
        {
            var baseUrl = NeoForgeDistributionBootstrap.NormalizeHttpsBase(mirror);
            if (baseUrl.Length == 0) continue;

            Uri target;
            try
            {
                target = new Uri(new Uri(baseUrl, UriKind.Absolute), relative);
                if (!string.IsNullOrEmpty(original.Query))
                    target = new UriBuilder(target) { Query = original.Query.TrimStart('?') }.Uri;
            }
            catch
            {
                continue;
            }

            yield return (target, NeoForgeDistributionBootstrap.DescribeSource(baseUrl));
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
            "maven.neoforged.net" => "neoforge-maven",
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
