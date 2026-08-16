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

        // Fail closed to one first-party emergency source for legacy cached entries instead of
        // silently re-introducing mirrors that the live catalog intentionally stopped advertising.
        return new[] { NormalizePackMirror(DefaultPackBaseUrl) }
            .Where(static url => !string.IsNullOrWhiteSpace(url))
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
    private void CleanupLegacyGameAuthFiles(string? gameDir = null)
    {
        try
        {
            var dir = Path.Combine(gameDir ?? _gameDir, "legendborn");
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
            StatusText = "Проверяю CDN и сборку…";
            ProgressPercent = 0;
            _mc.MaxParallelDownloads = PreferredPackParallelism;

            var mirrors = await PackMirrorPreflightService.OrderByFreshnessAsync(
                BuildPackMirrors(s),
                log: AppendLog,
                ct: _lifetimeCts.Token);

            if (s.SyncPack)
            {
                StatusText = "Убираю устаревшие файлы сборки…";
                await ManagedPackCleanupService.ReconcileAsync(
                    _gameDir,
                    mirrors,
                    log: AppendLog,
                    ct: _lifetimeCts.Token).ConfigureAwait(false);

                StatusText = "Проверяю и докачиваю изменения…";
                await _mc.SyncPackAsync(mirrors, _lifetimeCts.Token);
            }

            StatusText = "Сборка актуальна.";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Отменено.";
        }
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
        if (s is null)
        {
            StatusText = "Сервер не выбран.";
            return;
        }

        if (Interlocked.Exchange(ref _playGuard, 1) == 1)
            return;

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
            CleanupLegacyGameAuthFiles(launchGameDir);
            launchMc.ClearLegendCoreSession();

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

            var ipForAutoJoin = autoConnect ? ip : null;

            if (!TryGetAccessToken(out var token) || string.IsNullOrWhiteSpace(token))
            {
                StatusText = "Требуется авторизация.";
                AppendLog("Запуск: нет access token (похоже, вы не вошли).");
                return;
            }

            IsBusy = true;
            StatusText = $"Подготовка {BuildDisplayName}…";
            ProgressPercent = 0;

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
                    ct: _lifetimeCts.Token).ConfigureAwait(false);
                syncProductionPack = false;
            }
            else
#endif
            {
                StatusText = "Выбираю ближайший доступный CDN…";
                mirrors = await PackMirrorPreflightService.OrderByFreshnessAsync(
                    configuredMirrors,
                    log: AppendLog,
                    ct: _lifetimeCts.Token);
            }

            if (syncProductionPack)
            {
                // Destructive pack ownership is reconciled before normal download/hash work.
                // This makes stale mods/kubejs/scripts fail closed instead of silently mixing builds.
                StatusText = "Очищаю старые файлы сборки…";
                await ManagedPackCleanupService.ReconcileAsync(
                    launchGameDir,
                    mirrors,
                    log: AppendLog,
                    ct: _lifetimeCts.Token).ConfigureAwait(false);
            }

            var loader = CreateLoaderSpecFromServer(s);
            StatusText = "Проверяю файлы и загружаю изменения…";

            var launchVersionId = await launchMc.PrepareAsync(
                minecraftVersion: s.MinecraftVersion,
                loader: loader,
                packMirrors: mirrors,
                syncPack: syncProductionPack,
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
                StatusText = "Подготовка безопасной игровой сессии…";

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

            StatusText = "Запуск игры…";

            _runningProcess = await launchMc.BuildAndLaunchAsync(
                version: launchVersionId,
                username: username,
                ramMb: ram,
                serverIp: ipForAutoJoin,
                session: gameSession);

            _runningMinecraftService = launchMc;
            _runningMinecraftGameDir = launchGameDir;
            launched = true;

            HookProcessExited(_runningProcess, launchMc, launchGameDir);

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

    private void HookProcessExited(Process p, MinecraftService mc, string gameDir)
    {
        try
        {
            p.EnableRaisingEvents = true;
            p.Exited += (_, __) =>
            {
                try { mc.ClearLegendCoreSession(); } catch { }
                CleanupLegacyGameAuthFiles(gameDir);

                if (_isClosing) return;

                PostToUi(() =>
                {
                    if (_isClosing) return;

                    AppendLog("Игра закрыта.");
                    _runningProcess = null;
                    _runningMinecraftService = null;
                    _runningMinecraftGameDir = null;

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

        if (loaderType != "neoforge")
            throw new InvalidOperationException($"Loader '{loaderType}' не поддерживается этой сборкой лаунчера.");

        if (string.IsNullOrWhiteSpace(loaderVer))
            throw new InvalidOperationException("NeoForge требует loader.version.");

        if (string.IsNullOrWhiteSpace(installerUrl))
        {
            if (!NeoForgeDistributionBootstrap.TryResolve(loaderVer, out var distribution) ||
                string.IsNullOrWhiteSpace(distribution.InstallerUrl))
            {
                throw new InvalidOperationException(
                    $"Для NeoForge {loaderVer} отсутствует доверенный installer URL из server catalog.");
            }

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
        try
        {
            if (_runningProcess is null || _runningProcess.HasExited)
                return;

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
