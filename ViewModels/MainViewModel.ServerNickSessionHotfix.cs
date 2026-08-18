using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using LegendBorn.Launching;

namespace LegendBorn.ViewModels;

public sealed partial class MainViewModel
{
    private enum RunningLegendCoreSessionRefreshOutcome
    {
        NotRunning,
        Refreshed,
        Failed
    }

    private async Task<RunningLegendCoreSessionRefreshOutcome> RefreshRunningLegendCoreSessionAfterServerNickChangeAsync(
        string accessToken)
    {
        var process = _runningProcess;
        var minecraft = _runningMinecraftService;

        if (process is null || minecraft is null)
            return RunningLegendCoreSessionRefreshOutcome.NotRunning;

        try
        {
            if (process.HasExited)
                return RunningLegendCoreSessionRefreshOutcome.NotRunning;
        }
        catch
        {
            return RunningLegendCoreSessionRefreshOutcome.NotRunning;
        }

        var serverId = TryReadRunningSessionServerId(minecraft);
        if (string.IsNullOrWhiteSpace(serverId))
            serverId = (SelectedServer?.Id ?? string.Empty).Trim();

        if (string.IsNullOrWhiteSpace(serverId))
        {
            AppendLog("LegendCore hotfix: не удалось определить serverId для обновления игровой сессии.");
            return RunningLegendCoreSessionRefreshOutcome.Failed;
        }

        var technicalMinecraftUsername = Clean(_serverNickMinecraftUsername);
        if (technicalMinecraftUsername.Length == 0)
            technicalMinecraftUsername = Clean(Profile?.Minecraft?.Username ?? Profile?.MinecraftName);
        if (technicalMinecraftUsername.Length == 0)
            technicalMinecraftUsername = Clean(Username);

        try
        {
            var response = await _site.CreateMinecraftJoinTicketAsync(
                accessToken: accessToken,
                serverId: serverId,
                mcName: technicalMinecraftUsername,
                ct: _lifetimeCts.Token,
                deviceId: null).ConfigureAwait(false);

            var nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (!LegendCoreSessionRefreshService.IsUsableTicket(response, serverId, nowUnix))
            {
                var error = response.Error ?? response.Message ?? "join-ticket rejected";
                AppendLog("LegendCore hotfix: новый join-ticket после смены ника не получен: " + error);
                return RunningLegendCoreSessionRefreshOutcome.Failed;
            }

            var refreshed = new MinecraftService.LegendCoreSession(
                ServerId: serverId,
                Ticket: response.Ticket!.Trim(),
                ExpiresAtUnix: response.ExpiresAtUnix,
                LegendUuid: response.LegendUuid,
                MinecraftUuid: response.Minecraft?.Uuid,
                MinecraftUsername: response.Minecraft?.Username ?? technicalMinecraftUsername,
                SkinUrl: response.Minecraft?.SkinUrl,
                LauncherVersion: LauncherIdentity.InformationalVersion);

            minecraft.WriteLegendCoreSession(refreshed);
            AppendLog("LegendCore hotfix: игровая сессия немедленно обновлена после изменения serverNick.");
            return RunningLegendCoreSessionRefreshOutcome.Refreshed;
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppendLog("LegendCore hotfix: ошибка немедленного обновления игровой сессии: " + ex.Message);
            return RunningLegendCoreSessionRefreshOutcome.Failed;
        }
    }

    private static string TryReadRunningSessionServerId(MinecraftService minecraft)
    {
        try
        {
            var path = minecraft.LegendCoreSessionPath;
            if (!File.Exists(path))
                return string.Empty;

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.TryGetProperty("serverId", out var serverId))
                return (serverId.GetString() ?? string.Empty).Trim();
        }
        catch
        {
        }

        return string.Empty;
    }

    private static string ServerNickMutationStatus(
        RunningLegendCoreSessionRefreshOutcome outcome,
        bool reset)
    {
        return outcome switch
        {
            RunningLegendCoreSessionRefreshOutcome.Refreshed => reset
                ? "Ник сброшен. Защищённая игровая сессия обновлена."
                : "Ник сохранён. Защищённая игровая сессия обновлена.",
            RunningLegendCoreSessionRefreshOutcome.Failed => reset
                ? "Ник сброшен, но игровую сессию не удалось обновить. Перезапусти Minecraft через LegendBorn Launcher."
                : "Ник сохранён, но игровую сессию не удалось обновить. Перезапусти Minecraft через LegendBorn Launcher.",
            _ => reset
                ? "Свой ник сброшен. Используется ник привязанного Minecraft-аккаунта."
                : "Ник сохранён. В игре он обновится через синхронизацию профиля."
        };
    }
}
