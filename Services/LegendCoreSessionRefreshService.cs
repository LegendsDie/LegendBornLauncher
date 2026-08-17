using System;
using System.Threading;
using System.Threading.Tasks;
using LegendBorn.Launching;

namespace LegendBorn.Services;

/// <summary>
/// Keeps the short-lived one-time LegendCore join-ticket fresh while Minecraft is running.
/// The long-lived launcher access token remains in Launcher memory and is never serialized into the game directory.
/// </summary>
public static class LegendCoreSessionRefreshService
{
    public const int MinimumUsableLifetimeSeconds = 15;
    public const int MinimumRefreshDelaySeconds = 8;
    public const int MaximumRefreshDelaySeconds = 30;
    public const int RefreshJitterSeconds = 3;
    public const int MaximumFailureBackoffSeconds = 30;

    public static TimeSpan ComputeRefreshDelay(long expiresAtUnix, long nowUnix, int jitterSeconds = 0)
    {
        var remaining = expiresAtUnix - nowUnix;

        // Refresh at roughly one quarter of the current ticket lifetime. This keeps a fresh
        // handoff available without hammering the join-ticket endpoint if the server changes TTL.
        var seconds = remaining > 0 ? remaining / 4 : MinimumRefreshDelaySeconds;
        seconds = Math.Clamp(seconds, MinimumRefreshDelaySeconds, MaximumRefreshDelaySeconds);
        seconds = Math.Clamp(seconds + jitterSeconds, MinimumRefreshDelaySeconds, MaximumRefreshDelaySeconds);

        return TimeSpan.FromSeconds(seconds);
    }

    public static TimeSpan ComputeFailureBackoff(int consecutiveFailures)
    {
        var failures = Math.Clamp(consecutiveFailures, 1, 6);
        var seconds = Math.Min(5 * (1 << (failures - 1)), MaximumFailureBackoffSeconds);
        return TimeSpan.FromSeconds(seconds);
    }

    public static bool IsUsableTicket(
        SiteAuthService.MinecraftJoinTicketResponse response,
        string expectedServerId,
        long nowUnix)
    {
        if (response is null || !response.Ok || string.IsNullOrWhiteSpace(response.Ticket))
            return false;

        var returnedServerId = (response.ServerId ?? string.Empty).Trim();
        if (!string.Equals(returnedServerId, expectedServerId, StringComparison.Ordinal))
            return false;

        return response.ExpiresAtUnix > nowUnix + MinimumUsableLifetimeSeconds;
    }

    public static async Task RunAsync(
        SiteAuthService site,
        MinecraftService minecraft,
        string accessToken,
        string serverId,
        string minecraftUsername,
        MinecraftService.LegendCoreSession seedSession,
        Action<string>? log,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(site);
        ArgumentNullException.ThrowIfNull(minecraft);
        ArgumentNullException.ThrowIfNull(seedSession);

        accessToken = (accessToken ?? string.Empty).Trim();
        serverId = (serverId ?? string.Empty).Trim();
        minecraftUsername = (minecraftUsername ?? string.Empty).Trim();

        if (accessToken.Length == 0)
            throw new ArgumentException("accessToken is empty", nameof(accessToken));
        if (serverId.Length == 0)
            throw new ArgumentException("serverId is empty", nameof(serverId));

        var expiresAtUnix = seedSession.ExpiresAtUnix;
        var legendUuid = seedSession.LegendUuid;
        var minecraftUuid = seedSession.MinecraftUuid;
        var effectiveMinecraftUsername = seedSession.MinecraftUsername;
        var skinUrl = seedSession.SkinUrl;
        var failures = 0;

        var nextDelay = ComputeRefreshDelay(
            expiresAtUnix,
            DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Random.Shared.Next(-RefreshJitterSeconds, RefreshJitterSeconds + 1));

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(nextDelay, cancellationToken).ConfigureAwait(false);

                var response = await site.CreateMinecraftJoinTicketAsync(
                    accessToken: accessToken,
                    serverId: serverId,
                    mcName: minecraftUsername,
                    ct: cancellationToken,
                    deviceId: null).ConfigureAwait(false);

                var nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                if (!IsUsableTicket(response, serverId, nowUnix))
                {
                    failures++;
                    nextDelay = ComputeFailureBackoff(failures);
                    log?.Invoke("LegendCore: не удалось обновить игровую сессию; будет повторная попытка.");
                    continue;
                }

                legendUuid = response.LegendUuid ?? legendUuid;
                minecraftUuid = response.Minecraft?.Uuid ?? minecraftUuid;
                effectiveMinecraftUsername = response.Minecraft?.Username ?? effectiveMinecraftUsername ?? minecraftUsername;
                skinUrl = response.Minecraft?.SkinUrl ?? skinUrl;

                var refreshed = new MinecraftService.LegendCoreSession(
                    ServerId: serverId,
                    Ticket: response.Ticket!.Trim(),
                    ExpiresAtUnix: response.ExpiresAtUnix,
                    LegendUuid: legendUuid,
                    MinecraftUuid: minecraftUuid,
                    MinecraftUsername: effectiveMinecraftUsername,
                    SkinUrl: skinUrl,
                    LauncherVersion: LauncherIdentity.InformationalVersion);

                cancellationToken.ThrowIfCancellationRequested();
                minecraft.WriteLegendCoreSession(refreshed);

                // If process shutdown raced with the tiny synchronous file-write window, remove
                // the just-written handoff so a stale ticket is never left behind after Minecraft.
                if (cancellationToken.IsCancellationRequested)
                {
                    minecraft.ClearLegendCoreSession();
                    cancellationToken.ThrowIfCancellationRequested();
                }

                expiresAtUnix = response.ExpiresAtUnix;
                failures = 0;
                nextDelay = ComputeRefreshDelay(
                    expiresAtUnix,
                    nowUnix,
                    Random.Shared.Next(-RefreshJitterSeconds, RefreshJitterSeconds + 1));

                log?.Invoke("LegendCore: безопасная игровая сессия обновлена.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception error)
            {
                failures++;
                nextDelay = ComputeFailureBackoff(failures);
                log?.Invoke($"LegendCore: обновление игровой сессии временно недоступно ({error.GetType().Name}).");
            }
        }
    }
}
