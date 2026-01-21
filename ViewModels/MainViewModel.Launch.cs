using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LegendBorn.Launching;

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

    private async Task CheckPackAsync()
    {
        if (_isClosing) return;

        if (SelectedServer is null)
        {
            StatusText = "Сервер не выбран.";
            return;
        }

        try
        {
            IsBusy = true;
            StatusText = "Проверка обновлений сборки...";
            ProgressPercent = 0;

            var mirrors = BuildPackMirrors(SelectedServer);

            if (SelectedServer.SyncPack)
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

        if (SelectedServer is null)
        {
            StatusText = "Сервер не выбран.";
            return;
        }

        if (Interlocked.Exchange(ref _playGuard, 1) == 1)
            return;

        try
        {
            var username = (Username ?? "Player").Trim();
            if (string.IsNullOrWhiteSpace(username)) username = "Player";

            var ram = NormalizeRamMb(RamMb);
            if (ram < 1024) ram = 1024;

            IsBusy = true;
            StatusText = $"Подготовка {BuildDisplayName}...";
            ProgressPercent = 0;

            var mirrors = BuildPackMirrors(SelectedServer);
            var loader = CreateLoaderSpecFromServer(SelectedServer);

            var launchVersionId = await _mc.PrepareAsync(
                minecraftVersion: SelectedServer.MinecraftVersion,
                loader: loader,
                packMirrors: mirrors,
                syncPack: SelectedServer.SyncPack,
                ct: _lifetimeCts.Token);

            InvokeOnUi(() =>
            {
                Versions.Clear();
                Versions.Add(launchVersionId);
                SelectedVersion = launchVersionId;
            });

            try
            {
                _config.Current.RamMb = ram;
                _config.Current.LastServerId = SelectedServer.Id;

                var ipToSave = (ServerIp ?? "").Trim();
                if (string.IsNullOrWhiteSpace(ipToSave))
                    ipToSave = (SelectedServer.Address ?? "").Trim();

                _config.Current.LastServerIp = ipToSave;
                ScheduleConfigSave();
            }
            catch { }

            StatusText = "Запуск игры...";

            var ip = (ServerIp ?? "").Trim();
            if (string.IsNullOrWhiteSpace(ip))
                ip = (SelectedServer.Address ?? "").Trim();

            if (string.IsNullOrWhiteSpace(ip))
                ip = null;

            _runningProcess = await _mc.BuildAndLaunchAsync(
                version: launchVersionId,
                username: username,
                ramMb: ram,
                serverIp: ip);

            HookProcessExited(_runningProcess);

            Raise(nameof(CanStop));
            StopGameCommand.RaiseCanExecuteChanged();

            AppendLog("Игра запущена.");
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
        catch { }
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
                throw new InvalidOperationException($"Неизвестный loader '{loaderType}' (нет installerUrl).");
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

            Raise(nameof(CanStop));
            StopGameCommand.RaiseCanExecuteChanged();
            RefreshCanStates();
        }
    }
}
