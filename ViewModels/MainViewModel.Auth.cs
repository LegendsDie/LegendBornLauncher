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
        try { cts.Cancel(); } catch { }
        try { cts.Dispose(); } catch { }
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
                fullUrl += (fullUrl.Contains("?") ? "&" : "?") + "deviceId=" + Uri.EscapeDataString(deviceId);
            return fullUrl;
        }
    }

    private static DateTimeOffset BuildDeadline(long expiresAtUnix)
    {
        try { if (expiresAtUnix > 0) return DateTimeOffset.FromUnixTimeSeconds(expiresAtUnix); }
        catch { }
        return DateTimeOffset.UtcNow.AddMinutes(10);
    }

    private async Task ApplySuccessfulLoginAsync(AuthTokens tokens, CancellationToken ct)
    {
        _tokens = tokens;
        var me = await _site.GetMeAsync(tokens.SafeAccessToken, ct);
        Profile = me;
        SiteUserName = string.IsNullOrWhiteSpace(me.UserName) ? "Пользователь" : me.UserName;

        // The website allows selecting a skin before Minecraft is linked. Once Launcher auth
        // succeeds, immediately make the account playable instead of waiting for the Play button.
        var desiredMinecraftName = ResolveLaunchMinecraftUsername();
        if (!IsValidMcName(desiredMinecraftName))
            desiredMinecraftName = MakeValidMcName(me.MinecraftName ?? me.UserName ?? "Player");

        SiteAuthService.MinecraftLinkResponse? link = null;
        try
        {
            link = await _site.LinkMinecraftAsync(
                tokens.SafeAccessToken,
                desiredMinecraftName,
                ct,
                deviceId: null).ConfigureAwait(false);

            if (link.Ok)
            {
                var linkedName = (link.Minecraft?.Username ?? desiredMinecraftName).Trim();
                if (IsValidMcName(linkedName))
                {
                    try { _config.Current.LastUsername = linkedName; } catch { }
                    Username = linkedName;
                }

                // Re-read the authoritative snapshot so IsLinked, serverNick and selected skin
                // are visible everywhere immediately after authentication.
                me = await _site.GetMeAsync(tokens.SafeAccessToken, ct).ConfigureAwait(false);
                ApplyBuiltinSkinFallback(me, link.Minecraft?.SelectedSkinKey);
                Profile = me;
                SiteUserName = string.IsNullOrWhiteSpace(me.UserName) ? "Пользователь" : me.UserName;
                AppendLog($"Minecraft: аккаунт привязан как {linkedName}.");
            }
            else
            {
                AppendLog("Minecraft: автоматическая привязка не выполнена: " +
                          (link.Error ?? link.Message ?? "неизвестная ошибка"));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Site login remains valid even if the secondary link endpoint is temporarily down.
            AppendLog("Minecraft: автоматическая привязка временно недоступна: " + ex.Message);
        }

        if (link is null || !link.Ok)
        {
            var accountName = (me.Minecraft?.Username ?? me.MinecraftName ?? string.Empty).Trim();
            if (!IsValidMcName(accountName)) accountName = MakeValidMcName(accountName.Length > 0 ? accountName : SiteUserName);
            try { _config.Current.LastUsername = accountName; } catch { }
            Username = accountName;
            ApplyBuiltinSkinFallback(me, null);
        }

        IsLoggedIn = true;
        try
        {
            _config.Current.LastSuccessfulLoginUtc = DateTimeOffset.UtcNow;
            ScheduleConfigSave();
        }
        catch { }

        if (!me.CanPlay)
        {
            StatusText = string.IsNullOrWhiteSpace(me.Reason) ? "Доступ к игре ограничен." : me.Reason!;
            AppendLog(StatusText);
        }
        else
        {
            StatusText = link is { Ok: false }
                ? "Вход выполнен. Minecraft пока не привязан."
                : "Вход выполнен.";
            AppendLog($"Сайт: вошли как {SiteUserName}");
        }
    }

    private static void ApplyBuiltinSkinFallback(UserProfile profile, string? selectedKeyFromLink)
    {
        if (profile.Minecraft is null) return;
        if (profile.Minecraft.SelectedSkin is not null) return;

        var key = (profile.Minecraft.SelectedSkinKey ?? selectedKeyFromLink ?? string.Empty).Trim();
        UserProfile.SkinSnapshot? fallback = key switch
        {
            "builtin_default" => new UserProfile.SkinSnapshot
            {
                Title = "Стандартный скин",
                PreviewUrl = "/skins/default.png",
                SkinUrl = "/skins/default.png",
                IsEnabled = true
            },
            "builtin_example" => new UserProfile.SkinSnapshot
            {
                Title = "Стандартный скин 2",
                PreviewUrl = "/skins/example.png",
                SkinUrl = "/skins/example.png",
                IsEnabled = true
            },
            _ => null
        };

        if (fallback is null) return;
        profile.Minecraft.SelectedSkinKey = key;
        profile.Minecraft.SelectedSkin = fallback;
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
        if (!IsLoggedIn) IsLoggedIn = true;
        if (string.IsNullOrWhiteSpace(SiteUserName) || SiteUserName == "Не вошли") SiteUserName = "Пользователь";
        StatusText = statusText;
    }

    private async Task TryAutoLoginAsync(CancellationToken ct)
    {
        if (_isClosing) return;
        var saved = _tokenStore.Load();
        if (saved is null || !saved.HasAccessToken) return;
        if (saved.IsExpired()) { _tokenStore.Clear(); return; }

        try
        {
            IsBusy = true;
            StatusText = "Проверяю вход…";
            await ApplySuccessfulLoginAsync(saved, ct);
        }
        catch (OperationCanceledException) { StatusText = "Отменено."; }
        catch (Exception ex)
        {
            if (LooksLikeUnauthorized(ex))
            {
                _tokenStore.Clear();
                ApplyLoggedOutUiState("Требуется вход.");
            }
            else
            {
                ApplyOfflineAuthenticatedUiState(saved, "Вход сохранён. Нет связи с сайтом.");
                AppendLog("Автовход: сайт/сеть недоступны — использую сохранённую авторизацию.");
            }
        }
        finally
        {
            IsBusy = false;
            if (string.Equals(StatusText, "Проверяю вход…", StringComparison.Ordinal)) StatusText = "Готово.";
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
            StatusText = "Начинаю вход…";
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
                StatusText = "Откройте ссылку входа вручную.";
            }
            else StatusText = "Подтвердите вход на сайте.";

            var deadline = BuildDeadline(start.ExpiresAtUnix);
            var transientFailures = 0;

            while (!_loginCts.IsCancellationRequested && !_isClosing)
            {
                if (DateTimeOffset.UtcNow > deadline)
                {
                    AppendLog("Auth: время ожидания подтверждения истекло.");
                    StatusText = "Время входа истекло. Попробуйте снова.";
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
                        if (serverDeadline < deadline) deadline = serverDeadline;
                    }
                    continue;
                }

                if (poll.State == LauncherAuthClient.PollState.TransientError)
                {
                    transientFailures++;
                    if (transientFailures == 1 || transientFailures % 5 == 0)
                        AppendLog($"Auth polling: временная ошибка HTTP {poll.HttpStatus}" +
                                  (string.IsNullOrWhiteSpace(poll.Code) ? "." : $" ({poll.Code})."));
                    StatusText = poll.HttpStatus == 429
                        ? "Слишком много запросов. Продолжаю ждать…"
                        : "Сайт временно недоступен. Продолжаю ждать…";
                    var retry = poll.RetryAfter ?? TimeSpan.FromMilliseconds(Math.Min(5000, 1000 + transientFailures * 500));
                    await Task.Delay(retry, _loginCts.Token);
                    continue;
                }

                if (poll.State == LauncherAuthClient.PollState.TerminalError)
                {
                    var code = string.IsNullOrWhiteSpace(poll.Code) ? "AUTH_FAILED" : poll.Code;
                    AppendLog($"Auth завершён: {code}, HTTP {poll.HttpStatus}.");
                    StatusText = string.IsNullOrWhiteSpace(poll.Message) ? "Запрос входа больше недействителен." : poll.Message!;
                    return;
                }

                var tokens = poll.Tokens;
                if (tokens is null || !tokens.HasAccessToken)
                {
                    StatusText = "Сайт вернул некорректный ответ входа.";
                    return;
                }
                if (tokens.IsExpired())
                {
                    StatusText = "Сайт вернул просроченную сессию. Попробуйте снова.";
                    return;
                }

                _tokenStore.Save(tokens);
                try { await ApplySuccessfulLoginAsync(tokens, _loginCts.Token); }
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
                        ApplyOfflineAuthenticatedUiState(tokens, "Вход подтверждён. Профиль временно недоступен.");
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
            StatusText = "Неожиданная ошибка входа. Подробности записаны в журнал.";
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
        if (string.IsNullOrWhiteSpace(url)) return;
        if (!TryOpenUrlInBrowser(url, out var err))
        {
            AppendLog(err);
            StatusText = "Не удалось открыть ссылку. Скопируйте её и откройте вручную.";
        }
        else StatusText = "Ссылка открыта в браузере.";
    }

    private void CopyLoginUrl()
    {
        var url = LoginUrl;
        if (string.IsNullOrWhiteSpace(url)) return;
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
                    error = "Не удалось открыть браузер автоматически.\n" +
                            $"1) {ex1.Message}\n2) {ex2.Message}\n3) {ex3.Message}";
                    return false;
                }
            }
        }
    }

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
            catch { }

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
        finally { RefreshCanStates(); }

        if (string.IsNullOrWhiteSpace(tokenToRevoke)) return;
        try
        {
            using var revokeCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
            revokeCts.CancelAfter(TimeSpan.FromSeconds(6));
            var revoked = await new LauncherAuthClient().RevokeAsync(tokenToRevoke, revokeCts.Token);
            AppendLog(revoked ? "Сайт: Launcher-токен отозван на сервере." : "Сайт: сервер не подтвердил отзыв токена.");
        }
        catch (OperationCanceledException) { AppendLog("Сайт: отзыв токена не подтверждён из-за таймаута."); }
        catch (Exception ex) { AppendLog($"Сайт: ошибка отзыва токена: {ex.GetType().Name}: {ex.Message}"); }
    }
}
