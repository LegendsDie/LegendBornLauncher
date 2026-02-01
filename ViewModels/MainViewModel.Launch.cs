// File: ViewModels/MainViewModel.Launch.cs
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LegendBorn.Launching;

namespace LegendBorn.ViewModels;

public sealed partial class MainViewModel
{
    private const string DefaultPackBaseUrl = "https://legendborn.ru/launcher/pack/";

    // Папка/файлы, которые читает "мод авторизации/скина" внутри игры.
    // ВАЖНО: это лежит в _gameDir (папка клиента), чтобы мод гарантированно нашёл.
    private const string AuthTicketDirName = "legendborn";
    private const string AuthTicketJsonName = "auth.json";
    private const string AuthTicketTokenName = "auth.token";

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
    // AUTH TICKET (для мода)
    // =========================

    private string GetAuthTicketDir()
        => Path.Combine(_gameDir, AuthTicketDirName);

    private string GetAuthTicketJsonPath()
        => Path.Combine(GetAuthTicketDir(), AuthTicketJsonName);

    private string GetAuthTicketTokenPath()
        => Path.Combine(GetAuthTicketDir(), AuthTicketTokenName);

    private sealed class AuthTicket
    {
        public string v { get; set; } = "1";
        public string createdUtc { get; set; } = "";
        public string username { get; set; } = "";
        public string siteUserName { get; set; } = "";
        public string? avatarUrl { get; set; }
        public long rezonite { get; set; }

        public string serverId { get; set; } = "";
        public string? serverAddress { get; set; }
        public string build { get; set; } = "";

        // Токен в JSON — удобно модам, но параллельно пишем и auth.token (на всякий).
        public string accessToken { get; set; } = "";
    }

    private static void WriteAllTextAtomic(string path, string content)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(dir))
            Directory.CreateDirectory(dir);

        var tmp = path + ".tmp";

        File.WriteAllText(tmp, content);

        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch { /* ignore */ }

        File.Move(tmp, path);
    }

    private bool TryWriteAuthTicketForGame(
        string accessToken,
        string username,
        ServerEntry server,
        string? serverIpForConnect,
        out string error)
    {
        error = "";

        try
        {
            if (string.IsNullOrWhiteSpace(accessToken))
            {
                error = "AuthTicket: пустой accessToken.";
                return false;
            }

            var dir = GetAuthTicketDir();
            Directory.CreateDirectory(dir);

            // 1) auth.token (простое чтение модом)
            // НЕ логируем токен никогда.
            WriteAllTextAtomic(GetAuthTicketTokenPath(), accessToken.Trim());

            // 2) auth.json (богатые данные — для скина/аватара/отображения)
            var ticket = new AuthTicket
            {
                createdUtc = DateTimeOffset.UtcNow.ToString("O"),
                username = username,
                siteUserName = (SiteUserName ?? "").Trim(),
                avatarUrl = AvatarUrl,
                rezonite = Rezonite,

                serverId = (server.Id ?? "").Trim(),
                serverAddress = string.IsNullOrWhiteSpace(serverIpForConnect) ? null : serverIpForConnect.Trim(),
                build = (BuildDisplayName ?? "").Trim(),

                accessToken = accessToken.Trim(),
            };

            var json = JsonSerializer.Serialize(ticket, new JsonSerializerOptions
            {
                WriteIndented = true
            });

            WriteAllTextAtomic(GetAuthTicketJsonPath(), json);

            // По желанию можно скрыть (на Windows). Не критично — игнорим ошибки.
            try
            {
                File.SetAttributes(GetAuthTicketTokenPath(), FileAttributes.Hidden);
                File.SetAttributes(GetAuthTicketJsonPath(), FileAttributes.Hidden);
            }
            catch { /* ignore */ }

            return true;
        }
        catch (Exception ex)
        {
            error = "AuthTicket: не удалось записать файлы авторизации: " + ex.Message;
            return false;
        }
    }

    private void CleanupAuthTicketFiles()
    {
        try
        {
            var tokenPath = GetAuthTicketTokenPath();
            var jsonPath = GetAuthTicketJsonPath();

            try { if (File.Exists(tokenPath)) File.Delete(tokenPath); } catch { /* ignore */ }
            try { if (File.Exists(jsonPath)) File.Delete(jsonPath); } catch { /* ignore */ }

            // папку не удаляем намеренно: она может содержать другие файлы мода
        }
        catch { /* ignore */ }
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

        try
        {
            // ник
            var username = (Username ?? "Player").Trim();
            if (string.IsNullOrWhiteSpace(username)) username = "Player";
            username = MakeValidMcName(username);

            // RAM
            var ram = NormalizeRamMb(RamMb);
            if (ram < 4096) ram = 4096; // ТЗ: минимум 4GB

            // IP
            var ip = (ServerIp ?? "").Trim();
            if (string.IsNullOrWhiteSpace(ip))
                ip = (s.Address ?? "").Trim();
            if (string.IsNullOrWhiteSpace(ip))
                ip = null;

            // ✅ Мод авторизации: достаём accessToken и пишем auth ticket в gameDir
            if (!TryGetAccessToken(out var token) || string.IsNullOrWhiteSpace(token))
            {
                StatusText = "Требуется авторизация.";
                AppendLog("Запуск: нет access token (похоже, вы не вошли).");
                return;
            }

            if (!TryWriteAuthTicketForGame(token, username, s, ip, out var authErr))
            {
                StatusText = "Ошибка подготовки авторизации.";
                AppendLog(authErr);
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
            catch { /* ignore */ }

            StatusText = "Запуск игры...";

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

                    // ✅ чистим ticket после выхода игры (чтобы токен не лежал лишний раз)
                    CleanupAuthTicketFiles();

                    Raise(nameof(CanStop));
                    StopGameCommand.RaiseCanExecuteChanged();
                    RefreshCanStates();
                });
            };
        }
        catch { /* ignore */ }
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

            // ✅ чистим ticket если игру стопнули руками
            CleanupAuthTicketFiles();

            Raise(nameof(CanStop));
            StopGameCommand.RaiseCanExecuteChanged();
            RefreshCanStates();
        }
    }
}
