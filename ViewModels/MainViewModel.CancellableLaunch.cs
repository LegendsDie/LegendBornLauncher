// File: ViewModels/MainViewModel.CancellableLaunch.cs
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using LegendBorn.Launching;
using LegendBorn.Mvvm;
using LegendBorn.Services;

namespace LegendBorn.ViewModels;

public sealed partial class MainViewModel
{
    private AsyncRelayCommand? _cancellablePlayCommand;
    private RelayCommand? _cancelLaunchCommand;
    private bool _isLaunchInProgress;
    private bool _isLaunchCancellationRequested;

    /// <summary>
    /// Dashboard launch command. Unlike the legacy PlayCommand, this command owns a cancellable
    /// token all the way through CDN selection, clean-install, pack sync and site join-ticket work.
    /// </summary>
    public AsyncRelayCommand CancellablePlayCommand =>
        _cancellablePlayCommand ??= new AsyncRelayCommand(
            PlayCancellableAsync,
            () => CanPlay);

    public RelayCommand CancelLaunchCommand =>
        _cancelLaunchCommand ??= new RelayCommand(
            CancelLaunch,
            () => !_isClosing && CanCancelLaunch);

    public bool IsLaunchInProgress
    {
        get => _isLaunchInProgress;
        private set
        {
            if (!Set(ref _isLaunchInProgress, value))
                return;

            Raise(nameof(CanCancelLaunch));
            Raise(nameof(LaunchActionText));
            try { _cancellablePlayCommand?.RaiseCanExecuteChanged(); } catch { }
            try { _cancelLaunchCommand?.RaiseCanExecuteChanged(); } catch { }
        }
    }

    public bool IsLaunchCancellationRequested
    {
        get => _isLaunchCancellationRequested;
        private set
        {
            if (!Set(ref _isLaunchCancellationRequested, value))
                return;

            Raise(nameof(CanCancelLaunch));
            Raise(nameof(LaunchActionText));
            try { _cancelLaunchCommand?.RaiseCanExecuteChanged(); } catch { }
        }
    }

    public bool CanCancelLaunch =>
        !_isClosing &&
        IsLaunchInProgress &&
        !IsLaunchCancellationRequested &&
        !CanStop &&
        _cancellablePlayCommand?.CanCancel == true;

    public string LaunchActionText =>
        CanStop
            ? "Остановить"
            : IsLaunchInProgress
                ? (IsLaunchCancellationRequested ? "Отмена..." : "Отмена")
                : "Играть";

    private void CancelLaunch()
    {
        if (!CanCancelLaunch)
            return;

        IsLaunchCancellationRequested = true;
        StatusText = "Отмена запуска…";
        AppendLog("Запуск: запрошена отмена пользователем.");

        try { _cancellablePlayCommand?.Cancel(); } catch { }
        try { _cancelLaunchCommand?.RaiseCanExecuteChanged(); } catch { }
    }

    private async Task PlayCancellableAsync(CancellationToken commandToken)
    {
        if (_isClosing)
            return;

        var s = SelectedServer;
        if (s is null)
        {
            StatusText = "Сервер не выбран.";
            return;
        }

        var existingProcess = _runningProcess;
        if (existingProcess is not null)
        {
            try
            {
                if (!existingProcess.HasExited)
                {
                    StatusText = "Minecraft уже запущен.";
                    AppendLog("Запуск: второй экземпляр Minecraft заблокирован, пока текущая игра работает.");
                    return;
                }
            }
            catch
            {
                StatusText = "Minecraft уже запущен.";
                return;
            }
        }

        if (Interlocked.Exchange(ref _playGuard, 1) == 1)
            return;

        using var launchCts = CancellationTokenSource.CreateLinkedTokenSource(
            commandToken,
            _lifetimeCts.Token);
        var launchToken = launchCts.Token;

        var launched = false;
        var launchMc = _mc;
        var launchGameDir = _gameDir;

#if DEBUG
        if (LocalPackDebugService.IsEnabled)
        {
            launchGameDir = LocalPackDebugService.ResolveGameDirOverride()
                ?? Path.Combine(LauncherPaths.LocalDir, "dev-pack-test");
            Directory.CreateDirectory(launchGameDir);

            launchMc = new MinecraftService(launchGameDir);
            launchMc.Log += (_, line) => AppendLog(line);
            launchMc.ProgressPercent += (_, p) => OnMinecraftProgress(p);

            AppendLog("DEV pack: Debug-only local manifest mode enabled.");
            AppendLog($"DEV pack: production game dir is untouched; using {launchGameDir}");
        }
#endif

        launchMc.MaxParallelDownloads = PreferredPackParallelism;

        try
        {
            IsLaunchCancellationRequested = false;
            IsLaunchInProgress = true;
            IsBusy = true;
            ProgressPercent = 0;
            StatusText = $"Подготовка {BuildDisplayName}…";

            launchToken.ThrowIfCancellationRequested();

            CleanupLegacyGameAuthFiles(launchGameDir);
            launchMc.ClearLegendCoreSession();

            var username = ResolveLaunchMinecraftUsername();
            if (!string.Equals(Username, username, StringComparison.Ordinal))
            {
                var previousUsername = Username;
                try { _config.Current.LastUsername = username; } catch { }
                Username = username;

                AppendLog(
                    $"Minecraft identity: технический ник синхронизирован {previousUsername} -> {username}.");
            }
            else
            {
                AppendLog($"Minecraft identity: launch username={username}.");
            }

            var ram = NormalizeRamMb(RamMb);
            if (ram < 4096)
                ram = 4096;

            var ip = (ServerIp ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(ip))
                ip = (s.Address ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(ip))
                ip = null;

            var autoConnect = false;
            try { autoConnect = _config.Current.AutoConnect; } catch { }
            var ipForAutoJoin = autoConnect ? ip : null;

            if (!TryGetAccessToken(out var token) || string.IsNullOrWhiteSpace(token))
            {
                StatusText = "Требуется авторизация.";
                AppendLog("Запуск: нет access token (похоже, вы не вошли).");
                return;
            }

            launchToken.ThrowIfCancellationRequested();

            var configuredMirrors = BuildPackMirrors(s);
            string[] mirrors;
            var syncProductionPack = s.SyncPack;

#if DEBUG
            if (LocalPackDebugService.IsEnabled)
            {
                mirrors = configuredMirrors;
                StatusText = "Применяем локальный manifest одного мода…";
                await LocalPackDebugService.ApplyAsync(
                    launchGameDir,
                    mirrors,
                    log: AppendLog,
                    ct: launchToken).ConfigureAwait(false);
                syncProductionPack = false;
            }
            else
#endif
            {
                StatusText = "Выбираю ближайший доступный CDN…";
                mirrors = await PackMirrorPreflightService.OrderByFreshnessAsync(
                    configuredMirrors,
                    log: AppendLog,
                    ct: launchToken);
            }

            launchToken.ThrowIfCancellationRequested();

            var loader = CreateLoaderSpecFromServer(s);
            var launchVersionId = syncProductionPack
                ? await PrepareStableProductionBuildAsync(
                    launchMc,
                    s,
                    loader,
                    mirrors,
                    launchGameDir,
                    launchToken)
                : await PrepareWithoutProductionPackAsync(
                    launchMc,
                    s,
                    loader,
                    mirrors,
                    launchToken);

            launchToken.ThrowIfCancellationRequested();

            InvokeOnUi(() =>
            {
                Versions.Clear();
                Versions.Add(launchVersionId);
                SelectedVersion = launchVersionId;
            });

            MinecraftService.LegendCoreSession? gameSession = null;

            if (!string.IsNullOrWhiteSpace(s.Id))
            {
                StatusText = "Подготовка безопасной игровой сессии…";

                var link = await _site.LinkMinecraftAsync(
                    accessToken: token,
                    username: username,
                    ct: launchToken,
                    deviceId: null);

                launchToken.ThrowIfCancellationRequested();

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
                    ct: launchToken,
                    deviceId: null);

                launchToken.ThrowIfCancellationRequested();

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

                if (!string.Equals(
                        (jt.ServerId ?? string.Empty).Trim(),
                        s.Id.Trim(),
                        StringComparison.Ordinal))
                {
                    StatusText = "Сайт вернул игровую сессию не для выбранного сервера.";
                    AppendLog("Сервер: serverId в join-ticket не совпал с выбранным сервером.");
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
                AppendLog("LegendCore: игровая сессия будет автоматически обновляться, пока Minecraft запущен.");
            }
            else
            {
                AppendLog("Сервер: serverId не задан — защищённая LegendCore-сессия не создана.");
            }

            try
            {
                _config.Current.RamMb = ram;
                _config.Current.LastServerId = s.Id;

                var ipToSave = (ServerIp ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(ipToSave))
                    ipToSave = (s.Address ?? string.Empty).Trim();

                _config.Current.LastServerIp = ipToSave;
                ScheduleConfigSave();
            }
            catch
            {
            }

            launchToken.ThrowIfCancellationRequested();
            StatusText = "Запуск игры…";

            // CmlLib's final BuildProcessAsync path is not cancellation-token aware. If the user
            // cancels during that small window, kill the just-created process before exposing it
            // as the running game so the UI cancellation remains truthful.
            var startedProcess = await launchMc.BuildAndLaunchAsync(
                version: launchVersionId,
                username: username,
                ramMb: ram,
                serverIp: ipForAutoJoin,
                session: gameSession);

            if (launchToken.IsCancellationRequested)
            {
                TryKillCancelledProcess(startedProcess);
                throw new OperationCanceledException(launchToken);
            }

            _runningProcess = startedProcess;
            _runningMinecraftService = launchMc;
            _runningMinecraftGameDir = launchGameDir;
            launched = true;

            // From this point the dashboard action becomes "Остановить" rather than "Отмена".
            IsLaunchInProgress = false;
            IsLaunchCancellationRequested = false;
            Raise(nameof(CanStop));

            CancellationTokenSource? sessionRefreshCts = null;
            Task? sessionRefreshTask = null;

            var startSessionRefresh = gameSession is not null && !string.IsNullOrWhiteSpace(s.Id);
            if (startSessionRefresh)
                sessionRefreshCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);

            HookProcessExited(
                _runningProcess,
                launchMc,
                launchGameDir,
                sessionRefreshCts,
                () => sessionRefreshTask);

            if (startSessionRefresh && sessionRefreshCts is not null && !_runningProcess.HasExited)
            {
                sessionRefreshTask = LegendCoreSessionRefreshService.RunAsync(
                    site: _site,
                    minecraft: launchMc,
                    accessToken: token,
                    serverId: s.Id.Trim(),
                    minecraftUsername: username,
                    seedSession: gameSession!,
                    log: line => PostToUi(() =>
                    {
                        if (!_isClosing)
                            AppendLog(line);
                    }),
                    cancellationToken: sessionRefreshCts.Token);

                _runningLegendCoreSessionCts = sessionRefreshCts;
                _runningLegendCoreSessionTask = sessionRefreshTask;
            }
            else if (sessionRefreshCts is not null)
            {
                try { sessionRefreshCts.Cancel(); } catch { }
                try { sessionRefreshCts.Dispose(); } catch { }
            }

            StopGameCommand.RaiseCanExecuteChanged();

            AppendLog(autoConnect
                ? "Игра запущена (автозаход ВКЛ)."
                : "Игра запущена (автозаход ВЫКЛ, откроется меню).");

            StatusText = "Игра запущена.";
        }
        catch (OperationCanceledException) when (launchToken.IsCancellationRequested)
        {
            StatusText = "Запуск отменён.";
            AppendLog("Запуск отменён.");
        }
        catch (Exception ex)
        {
            StatusText = "Ошибка запуска.";
            AppendLog("Запуск: " + ex.Message);
            try { _log.Error("Minecraft launch failed", ex); } catch { }
        }
        finally
        {
            IsLaunchInProgress = false;
            IsLaunchCancellationRequested = false;
            IsBusy = false;
            Interlocked.Exchange(ref _playGuard, 0);

            if (!launched)
            {
                launchMc.ClearLegendCoreSession();
                CleanupLegacyGameAuthFiles(launchGameDir);
            }

            try { _cancellablePlayCommand?.RaiseCanExecuteChanged(); } catch { }
            try { _cancelLaunchCommand?.RaiseCanExecuteChanged(); } catch { }
            RefreshCanStates();
        }
    }

    private async Task<string> PrepareStableProductionBuildAsync(
        MinecraftService launchMc,
        ServerEntry server,
        MinecraftService.LoaderSpec loader,
        string[] mirrors,
        string launchGameDir,
        CancellationToken ct)
    {
        const int maxAttempts = 3;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            StatusText = "Проверяю режим установки сборки…";
            var before = await PackCleanInstallService.InspectAsync(mirrors, ct).ConfigureAwait(false);

            if (before.CleanInstall && !PackCleanInstallService.IsApplied(launchGameDir, before))
            {
                StatusText = "Чистая установка: очищаю старую сборку…";
                AppendLog(
                    $"Чистая установка: build {before.DisplayIdentity} требует полного сброса инстанса.");

                await PackCleanInstallService.CleanInstanceAsync(
                    launchGameDir,
                    before,
                    AppendLog,
                    ct).ConfigureAwait(false);

                ct.ThrowIfCancellationRequested();
                ProgressPercent = 0;
                AppendLog("Чистая установка: старые файлы удалены, начинаю установку текущей сборки.");
            }
            else if (before.CleanInstall)
            {
                AppendLog(
                    $"Чистая установка: build {before.DisplayIdentity} уже был полностью применён; повторный сброс не нужен.");
            }

            StatusText = "Проверяю файлы и загружаю изменения…";
            var launchVersionId = await launchMc.PrepareAsync(
                minecraftVersion: server.MinecraftVersion,
                loader: loader,
                packMirrors: mirrors,
                syncPack: true,
                ct: ct).ConfigureAwait(false);

            ct.ThrowIfCancellationRequested();

            StatusText = "Финальная сверка файлов сборки…";
            await ManagedPackStateVerifier.ReconcileAsync(
                launchGameDir,
                log: AppendLog,
                ct: ct).ConfigureAwait(false);

            ct.ThrowIfCancellationRequested();

            // Re-read manifest after sync. If live changed while we were preparing, never mark the
            // old clean build as applied or launch a mixed revision; restart from the new manifest.
            var after = await PackCleanInstallService.InspectAsync(mirrors, ct).ConfigureAwait(false);
            if (!string.Equals(
                    before.ManifestSha256,
                    after.ManifestSha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                AppendLog(
                    $"Сборка: live manifest изменился во время подготовки (попытка {attempt}/{maxAttempts}); повторяю с новой ревизией.");

                if (attempt == maxAttempts)
                {
                    throw new InvalidOperationException(
                        "Live manifest менялся во время подготовки несколько раз. Запуск остановлен, чтобы не смешать две сборки. Повтори запуск через несколько секунд.");
                }

                continue;
            }

            if (after.CleanInstall)
            {
                PackCleanInstallService.MarkApplied(launchGameDir, after);
                AppendLog(
                    $"Чистая установка: build {after.DisplayIdentity} установлен полностью и отмечен как применённый.");
            }

            return launchVersionId;
        }

        throw new InvalidOperationException("Не удалось стабилизировать live manifest.");
    }

    private async Task<string> PrepareWithoutProductionPackAsync(
        MinecraftService launchMc,
        ServerEntry server,
        MinecraftService.LoaderSpec loader,
        string[] mirrors,
        CancellationToken ct)
    {
        StatusText = "Проверяю файлы и загружаю изменения…";
        return await launchMc.PrepareAsync(
            minecraftVersion: server.MinecraftVersion,
            loader: loader,
            packMirrors: mirrors,
            syncPack: false,
            ct: ct).ConfigureAwait(false);
    }

    private static void TryKillCancelledProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
        }
        finally
        {
            try { process.Dispose(); } catch { }
        }
    }
}
