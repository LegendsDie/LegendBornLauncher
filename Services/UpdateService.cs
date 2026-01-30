// File: /Services/UpdateService.cs
using System;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Velopack;
using Velopack.Sources;

namespace LegendBorn.Services;

public static class UpdateService
{
    private const string RepoUrlOrSlug = "https://github.com/LegendsDie/LegendBornLauncher";
    private const string Channel = "win";

    // watchdog timeouts (не даём зависать вечно)
    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(10);

    private static readonly SemaphoreSlim _gate = new(1, 1);

    private static GithubSource CreateSource()
    {
        var repoUrl = NormalizeGithubRepoUrl(RepoUrlOrSlug);

        // optional: env token to reduce GitHub rate-limit issues
        var token = Environment.GetEnvironmentVariable("LEGENDBORN_GITHUB_TOKEN") ?? "";

        return new GithubSource(repoUrl: repoUrl, accessToken: token, prerelease: false);
    }

    private static UpdateManager CreateManager()
    {
        var options = new UpdateOptions
        {
            ExplicitChannel = Channel
        };

        return new UpdateManager(CreateSource(), options);
    }

    private static string NormalizeGithubRepoUrl(string input)
    {
        input = (input ?? "").Trim();
        if (string.IsNullOrWhiteSpace(input))
            return "https://github.com/LegendsDie/LegendBornLauncher";

        if (input.StartsWith("//", StringComparison.Ordinal))
            input = "https:" + input;

        // slug: owner/repo
        if (!input.Contains("://", StringComparison.Ordinal))
        {
            var slug = input.Trim().TrimEnd('/');

            if (slug.StartsWith("github.com/", StringComparison.OrdinalIgnoreCase))
                slug = slug["github.com/".Length..];

            var sp = slug.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (sp.Length >= 2)
                return $"https://github.com/{sp[0]}/{sp[1]}";

            return "https://github.com/LegendsDie/LegendBornLauncher";
        }

        // full URL
        if (Uri.TryCreate(input, UriKind.Absolute, out var uri))
        {
            var parts = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
            {
                var owner = parts[0];
                var repo = parts[1];
                if (repo.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
                    repo = repo[..^4];

                return $"https://github.com/{owner}/{repo}";
            }
        }

        return input.Trim().TrimEnd('/');
    }

    public static async Task CheckAndUpdateAsync(
        bool silent,
        bool showNoUpdates = false,
        CancellationToken ct = default)
    {
        // ✅ cancellation-safe gate acquire (не вылетим мимо try/catch)
        try
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return;
        }

        try
        {
            ct.ThrowIfCancellationRequested();

            var mgr = CreateManager();

            if (!mgr.IsInstalled)
            {
                if (!silent && showNoUpdates)
                    ShowInfo("Лаунчер запущен без установки (Velopack не активен). Обновления недоступны.");
                return;
            }

            // 1) Если уже есть подготовленное обновление (скачано и ждёт рестарта)
            if (mgr.UpdatePendingRestart is VelopackAsset pending)
            {
                if (!silent)
                {
                    var ask = ShowYesNo(
                        "Обновление уже скачано и готово к установке.\n\nПрименить сейчас? Лаунчер перезапустится.");

                    if (ask != MessageBoxResult.Yes)
                        return;
                }

                // ✅ Правильный порядок: сначала запускаем updater (он ждёт выхода),
                // затем выходим из приложения.
                StartUpdaterAndExit(mgr, pending, silent, restart: true);
                return;
            }

            // 2) Проверка обновлений (у Velopack нет ct в CheckForUpdatesAsync, поэтому только watchdog)
            UpdateInfo? updates;
            try
            {
                updates = await RunWithTimeout(
                        () => mgr.CheckForUpdatesAsync(),
                        CheckTimeout,
                        ct)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (!IsCancellation(ex, ct))
            {
                if (!silent)
                    ShowError(BuildFriendlyError("Не удалось проверить обновления.", ex));
                return;
            }

            ct.ThrowIfCancellationRequested();

            var target = updates?.TargetFullRelease;
            if (target is null)
            {
                if (!silent && showNoUpdates)
                    ShowInfo("Обновлений лаунчера нет.");
                return;
            }

            if (!silent)
            {
                var ask = ShowYesNo(
                    $"Доступно обновление лаунчера: {target.Version}\n\nОбновить сейчас? Лаунчер перезапустится.");

                if (ask != MessageBoxResult.Yes)
                    return;
            }

            // 3) Скачивание (у DownloadUpdatesAsync есть CancellationToken)
            try
            {
                // Реально отменяем загрузку и ставим общий таймаут.
                using var dlCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                dlCts.CancelAfter(DownloadTimeout);

                // progress можно прикрутить позже (Action<int> 0..100)
                await mgr.DownloadUpdatesAsync(updates!, progress: null, cancelToken: dlCts.Token)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (!IsCancellation(ex, ct))
            {
                if (!silent)
                    ShowError(BuildFriendlyError("Не удалось скачать обновление.", ex));
                return;
            }

            ct.ThrowIfCancellationRequested();

            // 4) Применение (правильный порядок + не заставляем silent=true всегда)
            StartUpdaterAndExit(mgr, target, silent, restart: true);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // silent cancel
        }
        catch (Exception ex)
        {
            if (!silent)
                ShowError(BuildFriendlyError("Ошибка обновления.", ex));
        }
        finally
        {
            try { _gate.Release(); } catch { }
        }
    }

    // -------------------------
    // Apply helpers
    // -------------------------

    private static void StartUpdaterAndExit(UpdateManager mgr, VelopackAsset toApply, bool silent, bool restart)
    {
        try
        {
            // ✅ запускаем updater (он будет ждать выхода приложения)
            mgr.WaitExitThenApplyUpdates(toApply, silent: silent, restart: restart);

            // ✅ затем инициируем нормальное закрытие приложения
            RequestAppShutdown();

            // ✅ last resort: если вдруг приложение не умирает из-за foreground-thread и т.п.
            ForceExitSoon(TimeSpan.FromSeconds(15));
        }
        catch (Exception ex)
        {
            if (!silent)
                ShowError(BuildFriendlyError("Не удалось применить обновление.", ex));
        }
    }

    private static void ForceExitSoon(TimeSpan delay)
    {
        try
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(delay).ConfigureAwait(false);
                    Environment.Exit(0);
                }
                catch { }
            });
        }
        catch { }
    }

    // -------------------------
    // Watchdog helpers
    // -------------------------

    // ✅ for Task<T> (не маскируем cancel под timeout)
    private static async Task<T> RunWithTimeout<T>(Func<Task<T>> action, TimeSpan timeout, CancellationToken ct)
    {
        var task = action();
        var delayTask = Task.Delay(timeout); // <- без ct, чтобы cancel не превращался в timeout

        var finished = await Task.WhenAny(task, delayTask).ConfigureAwait(false);

        // если отменили — это отмена
        ct.ThrowIfCancellationRequested();

        if (finished == delayTask)
            throw new TimeoutException($"Operation timed out after {timeout.TotalSeconds:0}s");

        return await task.ConfigureAwait(false);
    }

    // ✅ for Task (void) (не маскируем cancel под timeout)
    private static async Task RunWithTimeout(Func<Task> action, TimeSpan timeout, CancellationToken ct)
    {
        var task = action();
        var delayTask = Task.Delay(timeout); // <- без ct

        var finished = await Task.WhenAny(task, delayTask).ConfigureAwait(false);

        ct.ThrowIfCancellationRequested();

        if (finished == delayTask)
            throw new TimeoutException($"Operation timed out after {timeout.TotalSeconds:0}s");

        await task.ConfigureAwait(false);
    }

    private static bool IsCancellation(Exception ex, CancellationToken ct)
        => ct.IsCancellationRequested && ex is OperationCanceledException;

    private static void RequestAppShutdown()
    {
        try
        {
            var app = Application.Current;
            if (app?.Dispatcher is null) return;

            if (app.Dispatcher.CheckAccess())
            {
                try
                {
                    // Сначала пытаемся закрыть главное окно (чтобы Closing-логика отработала)
                    if (app.MainWindow != null)
                    {
                        try { app.MainWindow.Close(); } catch { }
                    }

                    // Затем гарантируем Shutdown (иначе можно зависнуть из-за ShutdownMode/окон/foreground threads)
                    try { app.Shutdown(); } catch { }
                }
                catch
                {
                    try { app.Shutdown(); } catch { }
                }

                return;
            }

            app.Dispatcher.Invoke(RequestAppShutdown);
        }
        catch { }
    }

    private static string BuildFriendlyError(string title, Exception ex)
    {
        var kind = ClassifyNetworkError(ex);

        var hint = kind switch
        {
            NetworkErrorKind.DnsOrHostNotFound =>
                "Похоже, система не может найти хост (DNS/блокировка домена). " +
                "Чаще всего это связано с провайдером, DNS (1.1.1.1/8.8.8.8), VPN, прокси или корпоративной сетью.\n\n" +
                "Что можно попробовать:\n" +
                "• сменить DNS (например, 1.1.1.1 или 8.8.8.8)\n" +
                "• включить/выключить VPN\n" +
                "• проверить, открывается ли GitHub в браузере\n" +
                "• проверить файл hosts и настройки прокси в системе",

            NetworkErrorKind.TlsOrSsl =>
                "Ошибка защищённого соединения (TLS/SSL). Возможные причины: " +
                "неверное время/дата на ПК, перехват HTTPS антивирусом/прокси, устаревшие корневые сертификаты.\n\n" +
                "Что можно попробовать:\n" +
                "• проверить дату/время Windows\n" +
                "• временно отключить HTTPS-сканирование в антивирусе\n" +
                "• попробовать другую сеть/VPN",

            NetworkErrorKind.Timeout =>
                "Истекло время ожидания. Возможные причины: нестабильный интернет, блокировки, " +
                "медленная сеть или недоступность GitHub.\n\n" +
                "Что можно попробовать:\n" +
                "• повторить попытку позже\n" +
                "• попробовать VPN/другую сеть\n" +
                "• проверить доступность GitHub в браузере",

            NetworkErrorKind.ConnectionRefusedOrReset =>
                "Соединение было сброшено/отклонено. Часто это блокировки, прокси, VPN, " +
                "фильтрация трафика или временная проблема сети.\n\n" +
                "Что можно попробовать:\n" +
                "• попробовать другую сеть/VPN\n" +
                "• отключить прокси/антивирусный веб-фильтр\n" +
                "• повторить попытку позже",

            _ =>
                "Проверьте доступность GitHub и соединение с интернетом (DNS/VPN/прокси/антивирус)."
        };

        var raw = ex.ToString();
        if (raw.Contains("release-assets.githubusercontent.com", StringComparison.OrdinalIgnoreCase))
            hint = "Не удаётся получить доступ к GitHub Release Assets (release-assets.githubusercontent.com).\n\n" + hint;

        return $"{title}\n\n{hint}\n\nТехнические детали:\n{ex}";
    }

    private enum NetworkErrorKind
    {
        Unknown = 0,
        DnsOrHostNotFound,
        Timeout,
        TlsOrSsl,
        ConnectionRefusedOrReset
    }

    private static NetworkErrorKind ClassifyNetworkError(Exception ex)
    {
        for (var e = ex; e != null; e = e.InnerException)
        {
            if (e is TimeoutException)
                return NetworkErrorKind.Timeout;

            // ВАЖНО: TaskCanceledException бывает и при таймаутах HttpClient.
            // Отмену "пользователем" мы обрабатываем выше (OperationCanceledException + ct.IsCancellationRequested).
            if (e is TaskCanceledException)
                return NetworkErrorKind.Timeout;

            if (e is HttpRequestException hre)
            {
                if (hre.InnerException is SocketException se)
                {
                    if (se.SocketErrorCode == SocketError.HostNotFound ||
                        se.SocketErrorCode == SocketError.NoData ||
                        se.SocketErrorCode == SocketError.TryAgain)
                        return NetworkErrorKind.DnsOrHostNotFound;

                    if (se.SocketErrorCode == SocketError.TimedOut)
                        return NetworkErrorKind.Timeout;

                    if (se.SocketErrorCode == SocketError.ConnectionRefused ||
                        se.SocketErrorCode == SocketError.ConnectionReset ||
                        se.SocketErrorCode == SocketError.NetworkReset ||
                        se.SocketErrorCode == SocketError.HostUnreachable ||
                        se.SocketErrorCode == SocketError.NetworkUnreachable)
                        return NetworkErrorKind.ConnectionRefusedOrReset;
                }

                var msg = hre.Message ?? "";
                if (msg.Contains("No such host is known", StringComparison.OrdinalIgnoreCase) ||
                    msg.Contains("Name or service not known", StringComparison.OrdinalIgnoreCase))
                    return NetworkErrorKind.DnsOrHostNotFound;

                if (msg.Contains("SSL", StringComparison.OrdinalIgnoreCase) ||
                    msg.Contains("TLS", StringComparison.OrdinalIgnoreCase) ||
                    msg.Contains("authentication failed", StringComparison.OrdinalIgnoreCase))
                    return NetworkErrorKind.TlsOrSsl;
            }

            var s = e.Message ?? "";
            if (s.Contains("SSL", StringComparison.OrdinalIgnoreCase) ||
                s.Contains("TLS", StringComparison.OrdinalIgnoreCase) ||
                s.Contains("authentication failed", StringComparison.OrdinalIgnoreCase))
                return NetworkErrorKind.TlsOrSsl;
        }

        return NetworkErrorKind.Unknown;
    }

    private static void ShowInfo(string text)
    {
        try
        {
            var app = Application.Current;
            if (app?.Dispatcher is null) return;

            if (app.Dispatcher.CheckAccess())
            {
                MessageBox.Show(text, "Обновление лаунчера", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            app.Dispatcher.Invoke(() => ShowInfo(text));
        }
        catch { }
    }

    private static void ShowError(string text)
    {
        try
        {
            var app = Application.Current;
            if (app?.Dispatcher is null) return;

            if (app.Dispatcher.CheckAccess())
            {
                MessageBox.Show(text, "Обновление лаунчера", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            app.Dispatcher.Invoke(() => ShowError(text));
        }
        catch { }
    }

    private static MessageBoxResult ShowYesNo(string text)
    {
        try
        {
            var app = Application.Current;
            if (app?.Dispatcher is null) return MessageBoxResult.No;

            if (app.Dispatcher.CheckAccess())
                return MessageBox.Show(text, "Обновление лаунчера", MessageBoxButton.YesNo, MessageBoxImage.Information);

            return app.Dispatcher.Invoke(() => ShowYesNo(text));
        }
        catch
        {
            return MessageBoxResult.No;
        }
    }
}
