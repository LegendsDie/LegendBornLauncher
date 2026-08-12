// File: ViewModels/MainViewModel.Auth.cs
using System;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using LegendBorn.Models;
using LegendBorn.Services;

namespace LegendBorn.ViewModels;

public sealed partial class MainViewModel
{
    private AuthTokens? _tokens;
    private CancellationTokenSource? _loginCts;

    private void CancelLoginWait()
    {
        var cts = _loginCts;
        _loginCts = null;

        if (cts is null) return;

        try { cts.Cancel(); } catch { /* ignore */ }
        try { cts.Dispose(); } catch { /* ignore */ }
    }

    private static bool LooksLikeUnauthorized(Exception ex)
    {
        if (ex is HttpRequestException hre && hre.StatusCode is HttpStatusCode sc)
            return sc is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;

        var msg = (ex.Message ?? "").ToLowerInvariant();
        return msg.Contains("401") || msg.Contains("403") || msg.Contains("unauthorized") || msg.Contains("forbidden");
    }

    private static string BuildConnectUrl(string deviceId, string connectUrl)
    {
        var path = string.IsNullOrWhiteSpace(connectUrl) ? "/launcher/connect" : connectUrl.Trim();

        var fullUrl = path.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? path
            : SiteBaseUrl + (path.StartsWith("/") ? path : "/" + path);

        try
        {
            var ub = new UriBuilder(fullUrl);
            var query = (ub.Query ?? "").TrimStart('?');

            if (query.IndexOf("deviceid=", StringComparison.OrdinalIgnoreCase) < 0 &&
                query.IndexOf("deviceId=", StringComparison.OrdinalIgnoreCase) < 0)
            {
                if (!string.IsNullOrWhiteSpace(query)) query += "&";
                query += "deviceId=" + Uri.EscapeDataString(deviceId);
                ub.Query = query;
            }

            return ub.Uri.ToString();
        }
        catch
        {
            if (!fullUrl.Contains("deviceId=", StringComparison.OrdinalIgnoreCase) &&
                !fullUrl.Contains("deviceid=", StringComparison.OrdinalIgnoreCase))
            {
                fullUrl += (fullUrl.Contains("?") ? "&" : "?") + "deviceId=" + Uri.EscapeDataString(deviceId);
            }

            return fullUrl;
        }
    }

    private static DateTimeOffset BuildDeadline(long expiresAtUnix)
    {
        try
        {
            if (expiresAtUnix > 0)
                return DateTimeOffset.FromUnixTimeSeconds(expiresAtUnix);
        }
        catch { /* ignore */ }

        return DateTimeOffset.UtcNow.AddMinutes(10);
    }

    private bool HasConfigUsername(out string normalized)
    {
        normalized = "";
        try
        {
            var raw = (_config.Current.LastUsername ?? "").Trim();

            if (string.IsNullOrWhiteSpace(raw)) return false;
            if (raw.Equals("Player", StringComparison.OrdinalIgnoreCase)) return false;

            normalized = IsValidMcName(raw) ? raw : MakeValidMcName(raw);
            return !string.IsNullOrWhiteSpace(normalized);
        }
        catch
        {
            return false;
        }
    }

    private async Task ApplySuccessfulLoginAsync(AuthTokens tokens, CancellationToken ct)
    {
        _tokens = tokens;

        // legendbornweb /api/launcher/me is the source of truth for profile,
        // play access and RZN. Do not call legacy/phantom launcher economy/events APIs.
        var me = await _site.GetMeAsync(tokens.SafeAccessToken, ct);
        Profile = me;

        SiteUserName = string.IsNullOrWhiteSpace(me.UserName) ? "Пользователь" : me.UserName;
        IsLoggedIn = true;

        if (HasConfigUsername(out var local))
        {
            if (!string.Equals(Username, local, StringComparison.Ordinal))
                Username = local;
        }
        else
        {
            var mcName = string.IsNullOrWhiteSpace(me.MinecraftName) ? SiteUserName : me.MinecraftName!;
            Username = MakeValidMcName(mcName);
        }

        try
        {
            _config.Current.LastSuccessfulLoginUtc = DateTimeOffset.UtcNow;
            ScheduleConfigSave();
        }
        catch { /* ignore */ }

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

    private void ApplyLoggedOutUiState(string statusText)
    {
        _tokens = null;

        Profile = null;
        Rezonite = 0;

        IsLoggedIn = false;
        IsWaitingSiteConfirm = false;
        SiteUserName = "Не вошли";

        LoginUrl = null;

        StatusText = statusText;
    }

    private void ApplyOfflineAuthenticatedUiState(AuthTokens tokens, string statusText)
    {
        _tokens = tokens;

        if (!IsLoggedIn)
            IsLoggedIn = true;

        if (string.IsNullOrWhiteSpace(SiteUserName) || SiteUserName == "Не вошли")
            SiteUserName = "Пользователь";

        StatusText = statusText;
    }

    private async Task TryAutoLoginAsync(CancellationToken ct)
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
            await ApplySuccessfulLoginAsync(saved, ct);
        }
        catch (OperationCanceledException)
        {
            StatusText = "Отменено.";
        }
        catch (Exception ex)
        {
            if (LooksLikeUnauthorized(ex))
            {
                _tokenStore.Clear();
                ApplyLoggedOutUiState("Требуется вход.");
            }
            else
            {
                ApplyOfflineAuthenticatedUiState(saved, "Вход сохранён (нет связи с сайтом).");
                AppendLog("Автовход: сайт/сеть недоступны — использую сохранённую авторизацию.");
            }
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
        _loginCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
        var auth = new LauncherAuthClient();

        try
        {
            IsWaitingSiteConfirm = true;
            LoginUrl = null;
            StatusText = "Запрос входа...";
            ProgressPercent = 0;
            IsBusy = true;

            var start = await auth.StartAsync(_loginCts.Token);
            IsBusy = false;

            var fullUrl = BuildConnectUrl(start.DeviceId, start.ConnectUrl);
            LoginUrl = fullUrl;
            AppendLog($"Ссылка для входа: {fullUrl}");

            if (!TryOpenUrlInBrowser(fullUrl, out var openError))
            {
                AppendLog(openError);
                StatusText = "Не удалось открыть браузер автоматически. Скопируй ссылку и открой вручную.";
            }
            else
            {
                StatusText = "Подтверди вход на сайте.";
            }

            var deadline = BuildDeadline(start.ExpiresAtUnix);
            var transientFailures = 0;

            while (!_loginCts.IsCancellationRequested && !_isClosing)
            {
                if (DateTimeOffset.UtcNow > deadline)
                {
                    AppendLog("Auth: время ожидания подтверждения истекло.");
                    StatusText = "Запрос входа истёк. Начни авторизацию заново.";
                    return;
                }

                await Task.Delay(1200, _loginCts.Token);

                var poll = await auth.PollAsync(start.DeviceId, _loginCts.Token);

                if (poll.State == LauncherAuthClient.PollState.Pending)
                {
                    transientFailures = 0;
                    if (poll.ExpiresAtUnix > 0)
                    {
                        var serverDeadline = BuildDeadline(poll.ExpiresAtUnix);
                        if (serverDeadline < deadline)
                            deadline = serverDeadline;
                    }
                    continue;
                }

                if (poll.State == LauncherAuthClient.PollState.TransientError)
                {
                    transientFailures++;
                    if (transientFailures == 1 || transientFailures % 5 == 0)
                    {
                        AppendLog(
                            $"Auth polling: временная ошибка HTTP {poll.HttpStatus}" +
                            (string.IsNullOrWhiteSpace(poll.Code) ? "." : $" ({poll.Code})."));
                    }

                    StatusText = poll.HttpStatus == 429
                        ? "Слишком много запросов. Продолжаю ожидание подтверждения..."
                        : "Сайт временно недоступен. Продолжаю ожидание подтверждения...";

                    var retry = poll.RetryAfter ?? TimeSpan.FromMilliseconds(Math.Min(5000, 1000 + transientFailures * 500));
                    await Task.Delay(retry, _loginCts.Token);
                    continue;
                }

                if (poll.State == LauncherAuthClient.PollState.TerminalError)
                {
                    var code = string.IsNullOrWhiteSpace(poll.Code) ? "AUTH_FAILED" : poll.Code;
                    AppendLog($"Auth завершён: {code}, HTTP {poll.HttpStatus}.");
                    StatusText = string.IsNullOrWhiteSpace(poll.Message)
                        ? "Запрос авторизации больше недействителен. Начни вход заново."
                        : poll.Message!;
                    return;
                }

                var tokens = poll.Tokens;
                if (tokens is null || !tokens.HasAccessToken)
                {
                    AppendLog("Auth: сервер сообщил OK без пригодного токена.");
                    StatusText = "Сайт вернул некорректный ответ входа. Попробуй снова.";
                    return;
                }

                if (tokens.IsExpired())
                {
                    AppendLog("Auth: сайт вернул уже просроченный токен.");
                    StatusText = "Сайт вернул просроченный токен. Попробуй снова.";
                    return;
                }

                _tokenStore.Save(tokens);

                try
                {
                    await ApplySuccessfulLoginAsync(tokens, _loginCts.Token);
                }
                catch (Exception ex)
                {
                    if (LooksLikeUnauthorized(ex))
                    {
                        _tokenStore.Clear();
                        ApplyLoggedOutUiState("Требуется вход.");
                        AppendLog("Сайт: новый Launcher-токен не принят /api/launcher/me (401/403).");
                    }
                    else
                    {
                        ApplyOfflineAuthenticatedUiState(tokens, "Вход подтверждён (профиль временно недоступен).");
                        AppendLog("Вход подтверждён, но /api/launcher/me временно недоступен.");
                    }
                }

                return;
            }
        }
        catch (LauncherAuthClient.LauncherAuthException ex)
        {
            var code = string.IsNullOrWhiteSpace(ex.Code) ? "AUTH_ERROR" : ex.Code;
            AppendLog($"Auth: {code}, HTTP {ex.HttpStatus}: {ex.Message}");
            StatusText = ex.Message;
        }
        catch (OperationCanceledException)
        {
            AppendLog("Ожидание входа отменено.");
            StatusText = "Вход отменён.";
        }
        catch (Exception ex)
        {
            AppendLog($"Auth unexpected error: {ex.GetType().Name}: {ex.Message}");
            StatusText = "Неожиданная ошибка входа. Подробности записаны в лог.";
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

    // RelayCommand intentionally accepts this async-void UI command. All exceptions are
    // handled inside the method; local logout happens immediately, server revocation is best-effort.
    private async void SiteLogout()
    {
        string tokenToRevoke = string.Empty;

        try
        {
            CancelLoginWait();

            tokenToRevoke = _tokens?.SafeAccessToken ?? string.Empty;
            _tokens = null;
            _tokenStore.Clear();

            try
            {
                _config.Current.LastUsername = null;
                ScheduleConfigSave();
            }
            catch { /* ignore */ }

            _username = "Player";
            Raise(nameof(Username));

            Profile = null;
            Rezonite = 0;

            IsLoggedIn = false;
            IsWaitingSiteConfirm = false;
            SiteUserName = "Не вошли";
            LoginUrl = null;

            StatusText = "Вы вышли.";
            AppendLog("Сайт: локальный выход выполнен.");
        }
        finally
        {
            RefreshCanStates();
        }

        if (string.IsNullOrWhiteSpace(tokenToRevoke))
            return;

        try
        {
            using var revokeCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
            revokeCts.CancelAfter(TimeSpan.FromSeconds(6));

            var revoked = await new LauncherAuthClient().RevokeAsync(tokenToRevoke, revokeCts.Token);
            AppendLog(revoked
                ? "Сайт: Launcher-токен отозван на сервере."
                : "Сайт: локальный выход выполнен, но сервер не подтвердил отзыв токена.");
        }
        catch (OperationCanceledException)
        {
            AppendLog("Сайт: локальный выход выполнен; отзыв токена не подтверждён из-за отмены/таймаута.");
        }
        catch (Exception ex)
        {
            AppendLog($"Сайт: локальный выход выполнен; ошибка отзыва токена: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            tokenToRevoke = string.Empty;
        }
    }
}
