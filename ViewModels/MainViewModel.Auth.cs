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
        // если сервер вернул относительный путь — соберём с базой.
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

    private async Task TrySendDailyLauncherLoginEventAsync(CancellationToken ct)
    {
        try
        {
            if (_isClosing) return;
            if (_tokens is null || !_tokens.HasAccessToken) return;

            var key = "launcher_login";
            var idem = $"launcher_login:{DateTime.UtcNow:yyyy-MM-dd}";

            var resp = await _site.SendLauncherEventAsync(
                _tokens.SafeAccessToken,
                key,
                idem,
                payload: new
                {
                    client = "LegendBornLauncher",
                    launcher = LauncherIdentity.InformationalVersion,
                    v = "1"
                },
                ct: ct);

            if (resp is not null && resp.Ok && resp.Balance >= 0)
                PostToUi(() => Rezonite = resp.Balance);
        }
        catch { /* ignore */ }
    }

    private async Task ApplySuccessfulLoginAsync(AuthTokens tokens, CancellationToken ct)
    {
        _tokens = tokens;

        var me = await _site.GetMeAsync(tokens.SafeAccessToken, ct);
        Profile = me;

        SiteUserName = string.IsNullOrWhiteSpace(me.UserName) ? "Пользователь" : me.UserName;
        IsLoggedIn = true;

        // ✅ Ник с сайта берём только если в конфиге нет нормального ника.
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

        await TrySendDailyLauncherLoginEventAsync(ct).ConfigureAwait(false);

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
        // ✅ сеть/сайт легли -> не разлогиниваем, токен НЕ удаляем
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

            var fullUrl = BuildConnectUrl(deviceId, connectUrl);
            LoginUrl = fullUrl;
            AppendLog($"Ссылка для входа: {fullUrl}");

            // ✅ БОЛЬШЕ НЕ ИСПОЛЬЗУЕМ СТАРУЮ ФРАЗУ
            if (!TryOpenUrlInBrowser(fullUrl, out var openError))
            {
                AppendLog(openError);
                StatusText = "Не удалось открыть браузер автоматически. Скопируй ссылку и открой вручную.";
            }
            else
            {
                StatusText = "Подтверди вход на сайте.";
            }

            var deadline = BuildDeadline(expiresAtUnix);

            while (!_loginCts.IsCancellationRequested && !_isClosing)
            {
                if (DateTimeOffset.UtcNow > deadline)
                {
                    AppendLog("Время ожидания подтверждения истекло.");
                    StatusText = "Не подтверждено. Попробуй снова.";
                    return;
                }

                await Task.Delay(1200, _loginCts.Token);

                var tokens = await _site.PollLauncherLoginAsync(deviceId, _loginCts.Token);
                if (tokens is null || !tokens.HasAccessToken)
                    continue;

                if (tokens.IsExpired())
                {
                    AppendLog("Сайт вернул просроченный токен. Попробуй снова.");
                    StatusText = "Ошибка входа. Попробуй снова.";
                    continue;
                }

                // ✅ Сохраняем токен сразу
                _tokenStore.Save(tokens);

                // ✅ Улучшение: если /me упал (сайт/сеть), не считаем это провалом входа
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
                        AppendLog("Сайт: токен не принят (401/403).");
                    }
                    else
                    {
                        ApplyOfflineAuthenticatedUiState(tokens, "Вход подтверждён (нет связи с сайтом).");
                        AppendLog("Вход подтверждён, но профиль недоступен — сайт/сеть недоступны.");
                    }
                }

                return;
            }
        }
        catch (OperationCanceledException)
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

            // ✅ ТЗ: при полном выходе — ник удаляем из конфига
            try
            {
                _config.Current.LastUsername = null;
                ScheduleConfigSave();
            }
            catch { /* ignore */ }

            // ✅ сбрасываем UI-ник, НЕ через сеттер (иначе он снова сохранит в конфиг)
            _username = "Player";
            Raise(nameof(Username));

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
