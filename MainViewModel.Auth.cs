using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using LegendBorn.Models;

namespace LegendBorn;

public sealed partial class MainViewModel
{
    private AuthTokens? _tokens;
    private CancellationTokenSource? _loginCts;

    private void CancelLoginWait()
    {
        var cts = _loginCts;
        _loginCts = null;

        if (cts is null) return;

        try { cts.Cancel(); } catch { }
        try { cts.Dispose(); } catch { }
    }

    private async Task TrySendDailyLauncherLoginEventAsync()
    {
        try
        {
            if (_isClosing) return;
            if (_tokens is null || !_tokens.HasAccessToken)
                return;

            var key = "launcher_login";
            var idem = $"launcher_login:{DateTime.UtcNow:yyyy-MM-dd}";

            await _site.SendLauncherEventAsync(
                _tokens.SafeAccessToken,
                key,
                idem,
                payload: new { client = "LegendBornLauncher", v = "1" },
                ct: CancellationToken.None);
        }
        catch { }
    }

    private async Task TryAutoLoginAsync()
    {
        if (_isClosing) return;

        var saved = _tokenStore.Load();
        if (saved is null || !saved.HasAccessToken)
            return;

        if (saved.IsExpired())
        {
            _tokenStore.Clear();
            return;
        }

        try
        {
            IsBusy = true;
            StatusText = "Проверка входа на сайте...";

            _tokens = saved;

            var me = await _site.GetMeAsync(_tokens.SafeAccessToken, CancellationToken.None);
            Profile = me;

            SiteUserName = string.IsNullOrWhiteSpace(me.UserName) ? "Пользователь" : me.UserName;
            IsLoggedIn = true;

            var mcName = string.IsNullOrWhiteSpace(me.MinecraftName) ? SiteUserName : me.MinecraftName;
            Username = MakeValidMcName(mcName);

            await TrySendDailyLauncherLoginEventAsync();

            if (!me.CanPlay)
            {
                StatusText = string.IsNullOrWhiteSpace(me.Reason) ? "Доступ к игре ограничен." : me.Reason!;
                AppendLog(StatusText);
            }
            else
            {
                StatusText = "Вход выполнен.";
                AppendLog($"Сайт: вошли как {SiteUserName}");
            }
        }
        catch
        {
            _tokens = null;
            _tokenStore.Clear();

            Profile = null;
            Rezonite = 0;

            IsLoggedIn = false;
            SiteUserName = "Не вошли";

            StatusText = "Требуется вход.";
        }
        finally
        {
            IsBusy = false;

            if (string.Equals(StatusText, "Проверка входа на сайте...", StringComparison.Ordinal))
                StatusText = "Готово.";

            RefreshCanStates();
        }
    }

    private async Task LoginViaSiteAsync()
    {
        if (_isClosing) return;

        CancelLoginWait();
        _loginCts = new CancellationTokenSource();

        try
        {
            IsWaitingSiteConfirm = true;
            LoginUrl = null;
            StatusText = "Запрос входа...";
            ProgressPercent = 0;

            IsBusy = true;

            var (deviceId, connectUrl, expiresAtUnix) =
                await _site.StartLauncherLoginAsync(_loginCts.Token);

            IsBusy = false;

            var path = string.IsNullOrWhiteSpace(connectUrl) ? "/launcher/connect" : connectUrl.Trim();
            var fullUrl = path.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? path
                : SiteBaseUrl + (path.StartsWith("/") ? path : "/" + path);

            if (!fullUrl.Contains("deviceId=", StringComparison.OrdinalIgnoreCase) &&
                !fullUrl.Contains("deviceid=", StringComparison.OrdinalIgnoreCase))
            {
                fullUrl += (fullUrl.Contains("?") ? "&" : "?") + "deviceId=" + Uri.EscapeDataString(deviceId);
            }

            LoginUrl = fullUrl;
            AppendLog($"Ссылка для входа: {fullUrl}");

            if (!TryOpenUrlInBrowser(fullUrl, out var openError))
            {
                AppendLog(openError);
                StatusText = "Сайт не открылся. Нажми «Скопировать ссылку» и открой вручную.";
            }
            else
            {
                StatusText = "Подтверди вход на сайте.";
            }

            var hardDeadline = expiresAtUnix > 0
                ? DateTimeOffset.FromUnixTimeSeconds(expiresAtUnix)
                : DateTimeOffset.UtcNow.AddMinutes(10);

            while (!_loginCts.IsCancellationRequested && !_isClosing)
            {
                if (DateTimeOffset.UtcNow > hardDeadline)
                {
                    AppendLog("Время ожидания подтверждения истекло.");
                    StatusText = "Не подтверждено. Попробуй снова.";
                    return;
                }

                await Task.Delay(1200, _loginCts.Token);

                var tokens = await _site.PollLauncherLoginAsync(deviceId, _loginCts.Token);
                if (tokens is null || !tokens.HasAccessToken)
                    continue;

                // релизно: если сервер внезапно отдал уже протухший токен — не сохраняем
                if (tokens.IsExpired())
                {
                    AppendLog("Сайт вернул просроченный токен. Попробуй снова.");
                    StatusText = "Ошибка входа. Попробуй снова.";
                    continue;
                }

                _tokens = tokens;
                _tokenStore.Save(tokens);

                var me = await _site.GetMeAsync(tokens.SafeAccessToken, _loginCts.Token);
                Profile = me;

                SiteUserName = string.IsNullOrWhiteSpace(me.UserName) ? "Пользователь" : me.UserName;
                IsLoggedIn = true;

                var mcName = string.IsNullOrWhiteSpace(me.MinecraftName) ? SiteUserName : me.MinecraftName;
                Username = MakeValidMcName(mcName);

                await TrySendDailyLauncherLoginEventAsync();

                if (!me.CanPlay)
                {
                    StatusText = string.IsNullOrWhiteSpace(me.Reason) ? "Доступ к игре ограничен." : me.Reason!;
                    AppendLog(StatusText);
                }
                else
                {
                    StatusText = "Вход выполнен.";
                    AppendLog($"Сайт: вошли как {SiteUserName}");
                }

                return;
            }
        }
        catch (TaskCanceledException)
        {
            AppendLog("Ожидание входа отменено.");
            StatusText = "Вход отменён.";
        }
        catch (Exception ex)
        {
            AppendLog(ex.ToString());
            StatusText = "Ошибка входа.";
        }
        finally
        {
            IsBusy = false;
            IsWaitingSiteConfirm = false;
            LoginUrl = null;

            CancelLoginWait();
            RefreshCanStates();
        }
    }

    private void OpenLoginUrl()
    {
        var url = LoginUrl;
        if (string.IsNullOrWhiteSpace(url))
            return;

        if (!TryOpenUrlInBrowser(url, out var err))
        {
            AppendLog(err);
            StatusText = "Не удалось открыть ссылку. Скопируй и открой вручную.";
        }
        else
        {
            StatusText = "Открыл ссылку в браузере.";
        }
    }

    private void CopyLoginUrl()
    {
        var url = LoginUrl;
        if (string.IsNullOrWhiteSpace(url))
            return;

        try
        {
            InvokeOnUi(() => Clipboard.SetText(url));
            StatusText = "Ссылка скопирована.";
            AppendLog("Ссылка скопирована.");
        }
        catch (Exception ex)
        {
            AppendLog(ex.ToString());
            StatusText = "Не удалось скопировать ссылку.";
        }
    }

    private static bool TryOpenUrlInBrowser(string url, out string error)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
            error = "";
            return true;
        }
        catch (Exception ex1)
        {
            try
            {
                Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = url, UseShellExecute = true });
                error = "";
                return true;
            }
            catch (Exception ex2)
            {
                try
                {
                    var escaped = url.Replace("\"", "\\\"");
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "cmd.exe",
                        Arguments = $"/c start \"\" \"{escaped}\"",
                        CreateNoWindow = true,
                        UseShellExecute = false
                    });
                    error = "";
                    return true;
                }
                catch (Exception ex3)
                {
                    error =
                        "Не удалось открыть браузер автоматически.\n" +
                        $"1) {ex1.Message}\n" +
                        $"2) {ex2.Message}\n" +
                        $"3) {ex3.Message}";
                    return false;
                }
            }
        }
    }

    private void SiteLogout()
    {
        try
        {
            CancelLoginWait();

            _tokens = null;
            _tokenStore.Clear();

            Profile = null;
            Rezonite = 0;

            IsLoggedIn = false;
            IsWaitingSiteConfirm = false;
            SiteUserName = "Не вошли";

            LoginUrl = null;

            StatusText = "Вы вышли.";
            AppendLog("Сайт: выход выполнен.");
        }
        finally
        {
            RefreshCanStates();
        }
    }
}
