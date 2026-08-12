// File: ViewModels/MainViewModel.Launch.cs
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LegendBorn.Launching;
using LegendBorn.Services;

namespace LegendBorn.ViewModels;

public sealed partial class MainViewModel
{
    private const string DefaultPackBaseUrl = "https://legendborn.ru/launcher/pack/";

    private static readonly string[] SourceForgePackMirrors =
    {
        "https://master.dl.sourceforge.net/project/legendborn-pack/launcher/pack/"
    };

    private static bool IsLegendbornHost(string? url)
        => !string.IsNullOrWhiteSpace(url) &&
           url.Contains("legendborn.ru", StringComparison.OrdinalIgnoreCase);

    private static bool IsSourceForgeMaster(string? url)
        => !string.IsNullOrWhiteSpace(url) &&
           url.Contains("master.dl.sourceforge.net", StringComparison.OrdinalIgnoreCase);

    private static bool IsSourceForgeDownloads(string? url)
        => !string.IsNullOrWhiteSpace(url) &&
           url.Contains("downloads.sourceforge.net", StringComparison.OrdinalIgnoreCase);

    private static string[] BuildPackMirrors(ServerEntry s)
    {
        var baseUrl = EnsureSlash(s.PackBaseUrl);
        if (string.IsNullOrWhiteSpace(baseUrl))
            baseUrl = EnsureSlash(DefaultPackBaseUrl);

        var extra = (s.PackMirrors ?? Array.Empty<string>())
            .Select(EnsureSlash)
            .Where(u => !string.IsNullOrWhiteSpace(u))
            .Where(u => !IsSourceForgeDownloads(u))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var all = new[] { baseUrl }
            .Concat(extra)
            .Where(u => !string.IsNullOrWhiteSpace(u))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (IsLegendbornHost(baseUrl))
        {
            if (!all.Any(IsSourceForgeMaster))
                all.AddRange(SourceForgePackMirrors.Select(EnsureSlash).Where(x => !string.IsNullOrWhiteSpace(x)));
        }

        if (all.Count == 0)
        {
            all.Add(EnsureSlash(DefaultPackBaseUrl));
            all.AddRange(SourceForgePackMirrors.Select(EnsureSlash).Where(x => !string.IsNullOrWhiteSpace(x)));
        }

        return all
            .OrderBy(u =>
            {
                if (u.Equals(baseUrl, StringComparison.OrdinalIgnoreCase)) return 0;
                if (IsSourceForgeMaster(u)) return 1;
                return 2;
            })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    // =========================
    // Legacy auth cleanup
    // =========================

    /// <summary>
    /// Older launcher builds wrote the long-lived site access token into the game directory.
    /// The current flow uses only a short-lived one-time join-ticket via .legendcore/session.json.
    /// Remove only the known legacy credential files; never delete the whole mod directory.
    /// </summary>
    private void CleanupLegacyGameAuthFiles()
    {
        try
        {
            var dir = Path.Combine(_gameDir, "legendborn");
            var names = new[]
            {
                "auth.token",
                "auth.json",
                "auth.token.tmp",
                "auth.json.tmp"
            };

            foreach (var name in names)
            {
                try
                {
                    var path = Path.Combine(dir, name);
                    if (File.Exists(path))
                        File.Delete(path);
                }
                catch
                {
                }
            }
        }
        catch
        {
        }
    }

    // =========================
    // Pack / Launch
    // =========================

    private async Task CheckPackAsync()
    {
        if (_isClosing) return;

        var s = SelectedServer;
        if (s is null)
        {
            StatusText = "Сервер не выбран.";
            return;
        }

        try
        {
            IsBusy = true;
            StatusText = "Проверка обновлений сборки...";
            ProgressPercent = 0;

            var mirrors = BuildPackMirrors(s);

            if (s.SyncPack)
                await _mc.SyncPackAsync(mirrors, _lifetimeCts.Token);

            StatusText = "Сборка актуальна.";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Отменено.";
        }
        catch (Exception ex)
        {
            StatusText = "Ошибка проверки сборки.";
            AppendLog(ex.ToString());
        }
        finally
        {
            IsBusy = false;
            RefreshCanStates();
        }
    }

    private async Task PlayAsync()
    {
        if (_isClosing) return;

        var s = SelectedServer;
        if (s is null)
        {
            StatusText = "Сервер не выбран.";
            return;
        }

        if (Interlocked.Exchange(ref _playGuard, 1) == 1)
            return;

        var launched = false;

        try
        {
            CleanupLegacyGameAuthFiles();
            _mc.ClearLegendCoreSession();

            var username = (Username ?? "Player").Trim();
            if (string.IsNullOrWhiteSpace(username)) username = "Player";
            username = MakeValidMcName(username);

            var ram = NormalizeRamMb(RamMb);
            if (ram < 4096) ram = 4096;

            var ip = (ServerIp ?? "").Trim();
            if (string.IsNullOrWhiteSpace(ip))
                ip = (s.Address ?? "").Trim();
            if (string.IsNullOrWhiteSpace(ip))
                ip = null;

            var autoConnect = false;
            try { autoConnect = _config.Current.AutoConnect; } catch { }

            // Respect the persisted launcher setting instead of a compile-time constant.
            var ipForAutoJoin = autoConnect ? ip : null;

            if (!TryGetAccessToken(out var token) || string.IsNullOrWhiteSpace(token))
            {
                StatusText = "Требуется авторизация.";
                AppendLog("Запуск: нет access token (похоже, вы не вошли).");
                return;
            }

            IsBusy = true;
            StatusText = $"Подготовка {BuildDisplayName}...";
            ProgressPercent = 0;

            var mirrors = BuildPackMirrors(s);
            var loader = CreateLoaderSpecFromServer(s);

            var launchVersionId = await _mc.PrepareAsync(
                minecraftVersion: s.MinecraftVersion,
                loader: loader,
                packMirrors: mirrors,
                syncPack: s.SyncPack,
                ct: _lifetimeCts.Token);

            InvokeOnUi(() =>
            {
                Versions.Clear();
                Versions.Add(launchVersionId);
                SelectedVersion = launchVersionId;
            });

            MinecraftService.LegendCoreSession? gameSession = null;

            if (!string.IsNullOrWhiteSpace(s.Id))
            {
                StatusText = "Подготовка безопасной игровой сессии...";

                // Keep the website-side Minecraft identity synchronized with the name selected
                // in the launcher. The long-lived access token is used only over HTTPS here and
                // is never copied into the Minecraft instance.
                var link = await _site.LinkMinecraftAsync(
                    accessToken: token,
                    username: username,
                    ct: _lifetimeCts.Token,
                    deviceId: null);

                if (!link.Ok)
                {
                    var error = link.Error ?? link.Message ?? "Не удалось связать Minecraft-профиль.";
                    StatusText = "Не удалось подготовить профиль Minecraft.";
                    AppendLog("Minecraft link: " + error);
                    return;
                }

                var jt = await _site.CreateMinecraftJoinTicketAsync(
                    accessToken: token,
                    serverId: s.Id,
                    mcName: username,
                    ct: _lifetimeCts.Token,
                    deviceId: null);

                if (!jt.Ok || string.IsNullOrWhiteSpace(jt.Ticket))
                {
                    var error = jt.Error ?? jt.Message ?? "Сайт не выдал join-ticket.";
                    StatusText = "Не удалось подготовить игровую сессию.";
                    AppendLog("Сервер: join-ticket не получен: " + error);
                    return;
                }

                var nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                if (jt.ExpiresAtUnix > 0 && jt.ExpiresAtUnix <= nowUnix + 10)
                {
                    StatusText = "Игровая сессия уже истекла. Повтори запуск.";
                    AppendLog("Сервер: получен слишком короткий/просроченный join-ticket.");
                    return;
                }

                gameSession = new MinecraftService.LegendCoreSession(
                    ServerId: s.Id.Trim(),
                    Ticket: jt.Ticket.Trim(),
                    ExpiresAtUnix: jt.ExpiresAtUnix,
                    LegendUuid: jt.LegendUuid,
                    MinecraftUuid: jt.Minecraft?.Uuid ?? link.Minecraft?.Uuid,
                    MinecraftUsername: jt.Minecraft?.Username ?? link.Minecraft?.Username ?? username,
                    SkinUrl: jt.Minecraft?.SkinUrl,
                    LauncherVersion: LauncherIdentity.InformationalVersion);

                AppendLog("Сервер: безопасный одноразовый join-ticket получен.");

                if (!autoConnect)
                    AppendLog("Автозаход выключен: join-ticket короткоживущий, подключайся к серверу сразу после запуска.");
            }
            else
            {
                AppendLog("Сервер: serverId не задан — защищённая LegendCore-сессия не создана.");
            }

            try
            {
                _config.Current.RamMb = ram;
                _config.Current.LastServerId = s.Id;

                var ipToSave = (ServerIp ?? "").Trim();
                if (string.IsNullOrWhiteSpace(ipToSave))
                    ipToSave = (s.Address ?? "").Trim();

                _config.Current.LastServerIp = ipToSave;
                ScheduleConfigSave();
            }
            catch
            {
            }

            StatusText = "Запуск игры...";

            _runningProcess = await _mc.BuildAndLaunchAsync(
                version: launchVersionId,
                username: username,
                ramMb: ram,
                serverIp: ipForAutoJoin,
                session: gameSession);

            launched = true;

            HookProcessExited(_runningProcess);

            Raise(nameof(CanStop));
            StopGameCommand.RaiseCanExecuteChanged();

            AppendLog(autoConnect
                ? "Игра запущена (автозаход ВКЛ)."
                : "Игра запущена (автозаход ВЫКЛ, откроется меню).");

            StatusText = "Игра запущена.";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Отменено.";
            AppendLog("Запуск отменён.");
        }
        catch (Exception ex)
        {
            StatusText = "Ошибка запуска.";
            AppendLog(ex.ToString());
        }
        finally
        {
            IsBusy = false;
            Interlocked.Exchange(ref _playGuard, 0);

            if (!launched)
            {
                _mc.ClearLegendCoreSession();
                CleanupLegacyGameAuthFiles();
            }

            RefreshCanStates();
        }
    }

    private void HookProcessExited(Process p)
    {
        try
        {
            p.EnableRaisingEvents = true;
            p.Exited += (_, __) =>
            {
                try { _mc.ClearLegendCoreSession(); } catch { }
                CleanupLegacyGameAuthFiles();

                if (_isClosing) return;

                PostToUi(() =>
                {
                    if (_isClosing) return;

                    AppendLog("Игра закрыта.");
                    _runningProcess = null;

                    Raise(nameof(CanStop));
                    StopGameCommand.RaiseCanExecuteChanged();
                    RefreshCanStates();
                });
            };
        }
        catch
        {
        }
    }

    private MinecraftService.LoaderSpec CreateLoaderSpecFromServer(ServerEntry s)
    {
        var loaderType = (s.LoaderName ?? "vanilla").Trim().ToLowerInvariant();
        var loaderVer = (s.LoaderVersion ?? "").Trim();
        var installerUrl = (s.LoaderInstallerUrl ?? "").Trim();

        if (loaderType == "vanilla" || string.IsNullOrWhiteSpace(loaderType))
            return new MinecraftService.LoaderSpec("vanilla", "", "");

        if (string.IsNullOrWhiteSpace(loaderVer))
            throw new InvalidOperationException($"Loader '{loaderType}' требует версию (loader.version).");

        if (string.IsNullOrWhiteSpace(installerUrl))
        {
            if (loaderType == "neoforge")
            {
                installerUrl =
                    $"https://maven.neoforged.net/releases/net/neoforged/neoforge/{loaderVer}/neoforge-{loaderVer}-installer.jar";
            }
            else if (loaderType == "forge")
            {
                installerUrl =
                    $"https://maven.minecraftforge.net/net/minecraftforge/forge/{s.MinecraftVersion}-{loaderVer}/forge-{s.MinecraftVersion}-{loaderVer}-installer.jar";
            }
            else
            {
                throw new InvalidOperationException($"Loader '{loaderType}' требует installerUrl (не задан в конфиге сервера).");
            }
        }

        return new MinecraftService.LoaderSpec(loaderType, loaderVer, installerUrl);
    }

    private void OpenGameDir()
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = _gameDir, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppendLog(ex.ToString());
        }
    }

    private void StopGame()
    {
        try
        {
            if (_runningProcess is null || _runningProcess.HasExited)
                return;

            _runningProcess.Kill(entireProcessTree: true);
            AppendLog("Процесс игры остановлен.");
        }
        catch (Exception ex)
        {
            AppendLog(ex.ToString());
        }
        finally
        {
            _runningProcess = null;

            try { _mc.ClearLegendCoreSession(); } catch { }
            CleanupLegacyGameAuthFiles();

            Raise(nameof(CanStop));
            StopGameCommand.RaiseCanExecuteChanged();
            RefreshCanStates();
        }
    }
}
