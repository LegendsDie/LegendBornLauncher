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
    // Emergency-only fallback for an old cached ServerEntry that has no usable HTTPS pack mirrors.
    // Normal launches must use exactly the mirrors advertised by the authoritative server catalog.
    private const string DefaultPackBaseUrl =
        "https://612cd759-4c9d-450e-bc91-a51d3c56e834.selstorage.ru/launcher/pack/";

    // Match MinecraftService's bounded connection pool. Eight parallel content-addressed blobs
    // keeps throughput high without turning unstable regional links into a request storm.
    private const int PreferredPackParallelism = 8;

    private MinecraftService? _runningMinecraftService;
    private string? _runningMinecraftGameDir;
    private CancellationTokenSource? _runningLegendCoreSessionCts;
    private Task? _runningLegendCoreSessionTask;

    private static string NormalizePackMirror(string? value)
    {
        var text = (value ?? "").Trim();
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            return "";

        var builder = new UriBuilder(uri)
        {
            Query = "",
            Fragment = ""
        };

        if (!builder.Path.EndsWith("/", StringComparison.Ordinal))
            builder.Path += "/";

        return builder.Uri.ToString();
    }

    private static string[] BuildPackMirrors(ServerEntry s)
    {
        var catalogMirrors = new[] { s.PackBaseUrl }
            .Concat(s.PackMirrors ?? Array.Empty<string>())
            .Select(NormalizePackMirror)
            .Where(static url => !string.IsNullOrWhiteSpace(url))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (catalogMirrors.Length > 0)
            return catalogMirrors;

        return new[] { NormalizePackMirror(DefaultPackBaseUrl) }
            .Where(static url => !string.IsNullOrWhiteSpace(url))
            .ToArray();
    }

    private void CleanupLegacyGameAuthFiles(string? gameDir = null)
    {
        try
        {
            var dir = Path.Combine(gameDir ?? _gameDir, "legendborn");
            var names = new[] { "auth.token", "auth.json", "auth.token.tmp", "auth.json.tmp" };
            foreach (var name in names)
            {
                try
                {
                    var path = Path.Combine(dir, name);
                    if (File.Exists(path)) File.Delete(path);
                }
                catch { }
            }
        }
        catch { }
    }

    private async Task CheckPackAsync()
    {
        if (_isClosing) return;
        var s = SelectedServer;
        if (s is null) { StatusText = "Сервер не выбран."; return; }

        try
        {
            IsBusy = true;
            StatusText = "Проверяю CDN и сборку…";
            ProgressPercent = 0;
            _mc.MaxParallelDownloads = PreferredPackParallelism;
            var mirrors = await PackMirrorPreflightService.OrderByFreshnessAsync(BuildPackMirrors(s), log: AppendLog, ct: _lifetimeCts.Token);

            if (s.SyncPack)
            {
                StatusText = "Проверяю и докачиваю изменения…";
                await _mc.SyncPackAsync(mirrors, _lifetimeCts.Token);
                StatusText = "Финальная сверка файлов сборки…";
                await ManagedPackStateVerifier.ReconcileAsync(_gameDir, log: AppendLog, ct: _lifetimeCts.Token).ConfigureAwait(false);
            }

            StatusText = "Сборка актуальна.";
        }
        catch (OperationCanceledException) { StatusText = "Отменено."; }
        catch (Exception ex)
        {
            StatusText = "Не удалось проверить сборку.";
            AppendLog("Сборка: " + ex.Message);
            try { _log.Error("Pack check failed", ex); } catch { }
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
        if (s is null) { StatusText = "Сервер не выбран."; return; }

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
            catch { StatusText = "Minecraft уже запущен."; return; }
        }

        if (Interlocked.Exchange(ref _playGuard, 1) == 1) return;

        var launched = false;
        var launchMc = _mc;
        var launchGameDir = _gameDir;

#if DEBUG
        if (LocalPackDebugService.IsEnabled)
        {
            launchGameDir = LocalPackDebugService.ResolveGameDirOverride() ?? Path.Combine(LauncherPaths.LocalDir, "dev-pack-test");
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
            CleanupLegacyGameAuthFiles(launchGameDir);
            launchMc.ClearLegendCoreSession();

            var username = ResolveLaunchMinecraftUsername();
            if (!string.Equals(Username, username, StringComparison.Ordinal))
            {
                var previousUsername = Username;
                try { _config.Current.LastUsername = username; } catch { }
                Username = username;
                AppendLog($"Minecraft identity: технический ник синхронизирован {previousUsername} -> {username}.");
            }
            else
            {
                AppendLog($"Minecraft identity: launch username={username}.");
            }

            var ram = NormalizeRamMb(RamMb);
            if (ram < 4096) ram = 4096;

            var ip = (ServerIp ?? "").Trim();
            if (string.IsNullOrWhiteSpace(ip)) ip = (s.Address ?? "").Trim();
            if (string.IsNullOrWhiteSpace(ip)) ip = null;

            var autoConnect = false;
            try { autoConnect = _config.Current.AutoConnect; } catch { }
            var ipForAutoJoin = autoConnect ? ip : null;

            if (!TryGetAccessToken(out var token) || string.IsNullOrWhiteSpace(token))
            {
                StatusText = "Требуется авторизация.";
                AppendLog("Запуск: нет access token (похоже, вы не вошли).");
                return;
            }

            IsBusy = true;
            StatusText = "Проверяю Java…";
            ProgressPercent = 0;
            var javaPath = await EnsureJavaForLaunchAsync().ConfigureAwait(false);
            AppendLog("Java: выбран совместимый runtime для Minecraft.");

            StatusText = $"Подготовка {BuildDisplayName}…";
            var configuredMirrors = BuildPackMirrors(s);
            string[] mirrors;
            var syncProductionPack = s.SyncPack;

#if DEBUG
            if (LocalPackDebugService.IsEnabled)
            {
                mirrors = configuredMirrors;
                StatusText = "Применяем локальный manifest одного мода…";
                await LocalPackDebugService.ApplyAsync(launchGameDir, mirrors, log: AppendLog, ct: _lifetimeCts.Token).ConfigureAwait(false);
                syncProductionPack = false;
            }
            else
#endif
            {
                StatusText = "Выбираю ближайший доступный CDN…";
                mirrors = await PackMirrorPreflightService.OrderByFreshnessAsync(configuredMirrors, log: AppendLog, ct: _lifetimeCts.Token);
            }

            var loader = CreateLoaderSpecFromServer(s);
            StatusText = "Проверяю файлы и загружаю изменения…";
            var launchVersionId = await launchMc.PrepareAsync(
                minecraftVersion: s.MinecraftVersion,
                loader: loader,
                packMirrors: mirrors,
                syncPack: syncProductionPack,
                ct: _lifetimeCts.Token);

            if (syncProductionPack)
            {
                StatusText = "Финальная сверка файлов сборки…";
                await ManagedPackStateVerifier.ReconcileAsync(launchGameDir, log: AppendLog, ct: _lifetimeCts.Token).ConfigureAwait(false);
            }

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
                var link = await _site.LinkMinecraftAsync(token, username, _lifetimeCts.Token, deviceId: null);
                if (!link.Ok)
                {
                    var error = link.Error ?? link.Message ?? "Не удалось связать Minecraft-профиль.";
                    StatusText = "Не удалось подготовить профиль Minecraft.";
                    AppendLog("Minecraft link: " + error);
                    return;
                }

                var jt = await _site.CreateMinecraftJoinTicketAsync(token, s.Id, username, _lifetimeCts.Token, deviceId: null);
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
                if (!string.Equals((jt.ServerId ?? string.Empty).Trim(), s.Id.Trim(), StringComparison.Ordinal))
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
                    SkinUrl: NormalizePublicUrl(jt.Minecraft?.SkinUrl),
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
                var ipToSave = (ServerIp ?? "").Trim();
                if (string.IsNullOrWhiteSpace(ipToSave)) ipToSave = (s.Address ?? "").Trim();
                _config.Current.LastServerIp = ipToSave;
                ScheduleConfigSave();
            }
            catch { }

            StatusText = "Запуск игры…";
            _runningProcess = await MinecraftJavaLauncher.BuildAndLaunchAsync(
                minecraft: launchMc,
                version: launchVersionId,
                username: username,
                ramMb: ram,
                javaPath: javaPath,
                serverIp: ipForAutoJoin,
                session: gameSession);

            _runningMinecraftService = launchMc;
            _runningMinecraftGameDir = launchGameDir;
            launched = true;

            CancellationTokenSource? sessionRefreshCts = null;
            Task? sessionRefreshTask = null;
            var startSessionRefresh = gameSession is not null && !string.IsNullOrWhiteSpace(s.Id);
            if (startSessionRefresh) sessionRefreshCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);

            HookProcessExited(_runningProcess, launchMc, launchGameDir, sessionRefreshCts, () => sessionRefreshTask);

            if (startSessionRefresh && sessionRefreshCts is not null && !_runningProcess.HasExited)
            {
                sessionRefreshTask = LegendCoreSessionRefreshService.RunAsync(
                    site: _site,
                    minecraft: launchMc,
                    accessToken: token,
                    serverId: s.Id.Trim(),
                    minecraftUsername: username,
                    seedSession: gameSession!,
                    log: line => PostToUi(() => { if (!_isClosing) AppendLog(line); }),
                    cancellationToken: sessionRefreshCts.Token);

                _runningLegendCoreSessionCts = sessionRefreshCts;
                _runningLegendCoreSessionTask = sessionRefreshTask;
            }
            else if (sessionRefreshCts is not null)
            {
                try { sessionRefreshCts.Cancel(); } catch { }
                try { sessionRefreshCts.Dispose(); } catch { }
            }

            Raise(nameof(CanStop));
            StopGameCommand.RaiseCanExecuteChanged();
            AppendLog(autoConnect ? "Игра запущена (автозаход ВКЛ)." : "Игра запущена (автозаход ВЫКЛ, откроется меню).");
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
            AppendLog("Запуск: " + ex.Message);
            try { _log.Error("Minecraft launch failed", ex); } catch { }
        }
        finally
        {
            IsBusy = false;
            Interlocked.Exchange(ref _playGuard, 0);
            if (!launched)
            {
                launchMc.ClearLegendCoreSession();
                CleanupLegacyGameAuthFiles(launchGameDir);
            }
            RefreshCanStates();
        }
    }

    private void HookProcessExited(
        Process p,
        MinecraftService mc,
        string gameDir,
        CancellationTokenSource? sessionRefreshCts,
        Func<Task?> sessionRefreshTaskProvider)
    {
        var handled = 0;
        async void HandleExited(object? _, EventArgs __)
        {
            if (Interlocked.Exchange(ref handled, 1) == 1) return;
            try { sessionRefreshCts?.Cancel(); } catch { }
            var sessionRefreshTask = sessionRefreshTaskProvider();
            if (sessionRefreshTask is not null)
            {
                try { await sessionRefreshTask.ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                catch { }
            }
            try { mc.ClearLegendCoreSession(); } catch { }
            CleanupLegacyGameAuthFiles(gameDir);
            try { sessionRefreshCts?.Dispose(); } catch { }
            if (_isClosing) return;

            PostToUi(() =>
            {
                if (_isClosing) return;
                AppendLog("Игра закрыта.");
                _runningProcess = null;
                _runningMinecraftService = null;
                _runningMinecraftGameDir = null;
                if (ReferenceEquals(_runningLegendCoreSessionCts, sessionRefreshCts))
                {
                    _runningLegendCoreSessionCts = null;
                    _runningLegendCoreSessionTask = null;
                }
                Raise(nameof(CanStop));
                StopGameCommand.RaiseCanExecuteChanged();
                RefreshCanStates();
            });
        }

        try
        {
            p.EnableRaisingEvents = true;
            p.Exited += HandleExited;
            if (p.HasExited) HandleExited(p, EventArgs.Empty);
        }
        catch { HandleExited(p, EventArgs.Empty); }
    }

    private MinecraftService.LoaderSpec CreateLoaderSpecFromServer(ServerEntry s)
    {
        var loaderType = (s.LoaderName ?? "vanilla").Trim().ToLowerInvariant();
        var loaderVer = (s.LoaderVersion ?? "").Trim();
        var installerUrl = (s.LoaderInstallerUrl ?? "").Trim();
        if (loaderType == "vanilla" || string.IsNullOrWhiteSpace(loaderType)) return new MinecraftService.LoaderSpec("vanilla", "", "");
        if (loaderType != "neoforge") throw new InvalidOperationException($"Loader '{loaderType}' не поддерживается этой сборкой лаунчера.");
        if (string.IsNullOrWhiteSpace(loaderVer)) throw new InvalidOperationException("NeoForge требует loader.version.");

        if (string.IsNullOrWhiteSpace(installerUrl))
        {
            if (!NeoForgeDistributionBootstrap.TryResolve(loaderVer, out var distribution) || string.IsNullOrWhiteSpace(distribution.InstallerUrl))
                throw new InvalidOperationException($"Для NeoForge {loaderVer} отсутствует доверенный installer URL из server catalog.");
            installerUrl = distribution.InstallerUrl;
        }
        return new MinecraftService.LoaderSpec(loaderType, loaderVer, installerUrl);
    }

    private void OpenGameDir()
    {
        try
        {
            var dir = _runningMinecraftGameDir ?? _gameDir;
            Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppendLog("Папка игры: " + ex.Message);
            try { _log.Error("Open game directory failed", ex); } catch { }
        }
    }

    private void StopGame()
    {
        var sessionRefreshCts = _runningLegendCoreSessionCts;
        _runningLegendCoreSessionCts = null;
        _runningLegendCoreSessionTask = null;
        try { sessionRefreshCts?.Cancel(); } catch { }

        try
        {
            if (_runningProcess is null || _runningProcess.HasExited) return;
            _runningProcess.Kill(entireProcessTree: true);
            AppendLog("Процесс игры остановлен.");
        }
        catch (Exception ex)
        {
            AppendLog("Остановка игры: " + ex.Message);
            try { _log.Error("Stop game failed", ex); } catch { }
        }
        finally
        {
            _runningProcess = null;
            var mc = _runningMinecraftService ?? _mc;
            var gameDir = _runningMinecraftGameDir ?? _gameDir;
            try { mc.ClearLegendCoreSession(); } catch { }
            CleanupLegacyGameAuthFiles(gameDir);
            _runningMinecraftService = null;
            _runningMinecraftGameDir = null;
            Raise(nameof(CanStop));
            StopGameCommand.RaiseCanExecuteChanged();
            RefreshCanStates();
        }
    }
}
