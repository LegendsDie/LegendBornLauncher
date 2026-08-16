// File: /Services/UpdateService.cs
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using LegendBorn.Views;
using Velopack;
using Velopack.Sources;

namespace LegendBorn.Services;

public static class UpdateService
{
    private const string RepoUrlOrSlug = "https://github.com/LegendsDie/LegendBornLauncher";
    private const string SelectelUpdateBaseUrl =
        "https://612cd759-4c9d-450e-bc91-a51d3c56e834.selstorage.ru/launcher/releases/";
    private const string Channel = "win";

    private static readonly TimeSpan CheckTimeoutPerSource = TimeSpan.FromSeconds(14);
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(10);
    private static readonly SemaphoreSlim Gate = new(1, 1);

    private sealed record UpdateSourceSpec(string Name, int Priority, Func<UpdateManager> CreateManager);
    private sealed record AvailableUpdate(string Name, int Priority, UpdateManager Manager, UpdateInfo Info);

    private static string CurrentVersion
        => string.IsNullOrWhiteSpace(LauncherIdentity.InformationalVersion)
            ? "—"
            : LauncherIdentity.InformationalVersion.Trim();

    private static UpdateOptions CreateOptions() => new()
    {
        ExplicitChannel = Channel
    };

    private static UpdateManager CreateSelectelManager()
        => new(new SimpleWebSource(SelectelUpdateBaseUrl), CreateOptions());

    private static UpdateManager CreateGitHubManager()
    {
        var repoUrl = NormalizeGithubRepoUrl(RepoUrlOrSlug);
        var token = Environment.GetEnvironmentVariable("LEGENDBORN_GITHUB_TOKEN") ?? "";
        var source = new GithubSource(repoUrl: repoUrl, accessToken: token, prerelease: false);
        return new UpdateManager(source, CreateOptions());
    }

    private static UpdateSourceSpec[] CreateSources() => new[]
    {
        // Russia-friendly first-party static feed first; GitHub remains an independent fallback.
        new UpdateSourceSpec("LegendBorn CDN", 0, CreateSelectelManager),
        new UpdateSourceSpec("GitHub Releases", 1, CreateGitHubManager)
    };

    private static string NormalizeGithubRepoUrl(string input)
    {
        input = (input ?? "").Trim();
        if (string.IsNullOrWhiteSpace(input))
            return "https://github.com/LegendsDie/LegendBornLauncher";

        if (input.StartsWith("//", StringComparison.Ordinal))
            input = "https:" + input;

        if (!input.Contains("://", StringComparison.Ordinal))
        {
            var slug = input.Trim().TrimEnd('/');
            if (slug.StartsWith("github.com/", StringComparison.OrdinalIgnoreCase))
                slug = slug["github.com/".Length..];

            var parts = slug.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
                return $"https://github.com/{parts[0]}/{parts[1]}";

            return "https://github.com/LegendsDie/LegendBornLauncher";
        }

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
        try
        {
            await Gate.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return;
        }

        LauncherUpdateDialog? progressDialog = null;

        try
        {
            ct.ThrowIfCancellationRequested();

            var sources = CreateSources();
            var localManager = sources[0].CreateManager();

            if (!localManager.IsInstalled)
            {
                if (!silent && showNoUpdates)
                {
                    ShowInfo(
                        "Обновления недоступны",
                        "Эта копия лаунчера запущена как portable/debug-сборка. Установленная версия LegendBorn обновляется автоматически.");
                }
                return;
            }

            // Pending package is local state; it does not matter which remote source created it.
            if (localManager.UpdatePendingRestart is VelopackAsset pending)
            {
                if (!silent && !ConfirmUpdate(
                        "Обновление готово",
                        "Пакет уже скачан и проверен. Осталось применить его и перезапустить лаунчер.",
                        pending.Version.ToString(),
                        "Локальный пакет",
                        "Установить"))
                {
                    return;
                }

                StartUpdaterAndExit(localManager, pending, silent, restart: true);
                return;
            }

            var (best, errors, successfulSources) = await FindBestUpdateAsync(sources, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();

            if (best is null)
            {
                if (successfulSources == 0)
                {
                    if (!silent)
                        ShowError("Не удалось проверить обновления", BuildAllSourcesFailedError(errors));
                    return;
                }

                if (!silent && showNoUpdates)
                {
                    ShowInfo(
                        "У тебя актуальная версия",
                        $"LegendBorn Launcher {CurrentVersion} уже обновлён. Новых версий сейчас нет.");
                }
                return;
            }

            var target = best.Info.TargetFullRelease;
            if (!silent && !ConfirmUpdate(
                    "Доступно обновление",
                    "Новая версия будет скачана в фоне, проверена Velopack и применена после закрытия текущего окна.",
                    target.Version.ToString(),
                    best.Name,
                    "Обновить"))
            {
                return;
            }

            if (!silent)
                progressDialog = ShowProgress(target.Version.ToString(), best.Name);

            try
            {
                using var dlCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                dlCts.CancelAfter(DownloadTimeout);

                await best.Manager.DownloadUpdatesAsync(
                        best.Info,
                        progress: progressDialog is null
                            ? null
                            : value => SetProgress(progressDialog, value, "Загружаю обновление…"),
                        cancelToken: dlCts.Token)
                    .ConfigureAwait(false);
            }
            catch (Exception firstDownloadError) when (!IsCancellation(firstDownloadError, ct))
            {
                // The selected source can disappear between feed read and asset download. If the
                // same-or-newer release exists on the other source, retry there before failing.
                if (progressDialog is not null)
                    SetProgress(progressDialog, 0, "Основной источник недоступен. Переключаюсь на резервный…");

                var fallback = await FindDownloadFallbackAsync(
                        sources,
                        best,
                        target.Version,
                        ct)
                    .ConfigureAwait(false);

                if (fallback is null)
                {
                    CloseProgress(progressDialog);
                    progressDialog = null;
                    if (!silent)
                    {
                        ShowError(
                            "Не удалось скачать обновление",
                            BuildFriendlyError(
                                $"Источник {best.Name} не ответил, а резервный источник не смог отдать ту же версию.",
                                firstDownloadError));
                    }
                    return;
                }

                try
                {
                    if (progressDialog is not null)
                    {
                        SetProgressSource(progressDialog, fallback.Name);
                        SetProgress(progressDialog, 0, "Загружаю с резервного источника…");
                    }

                    using var fallbackCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    fallbackCts.CancelAfter(DownloadTimeout);
                    await fallback.Manager.DownloadUpdatesAsync(
                            fallback.Info,
                            progress: progressDialog is null
                                ? null
                                : value => SetProgress(progressDialog, value, "Загружаю с резервного источника…"),
                            cancelToken: fallbackCts.Token)
                        .ConfigureAwait(false);
                    best = fallback;
                    target = fallback.Info.TargetFullRelease;
                }
                catch (Exception fallbackError) when (!IsCancellation(fallbackError, ct))
                {
                    CloseProgress(progressDialog);
                    progressDialog = null;
                    if (!silent)
                    {
                        ShowError(
                            "Не удалось скачать обновление",
                            BuildFriendlyError(
                                "Оба независимых источника обновления сейчас недоступны.",
                                new AggregateException(firstDownloadError, fallbackError)));
                    }
                    return;
                }
            }

            ct.ThrowIfCancellationRequested();
            if (progressDialog is not null)
                SetProgress(progressDialog, 100, "Готово. Перезапускаю лаунчер…");

            StartUpdaterAndExit(best.Manager, target, silent, restart: true);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            CloseProgress(progressDialog);
        }
        catch (Exception ex)
        {
            CloseProgress(progressDialog);
            if (!silent)
                ShowError("Ошибка обновления", BuildFriendlyError("Не удалось завершить обновление лаунчера.", ex));
        }
        finally
        {
            try { Gate.Release(); } catch { }
        }
    }

    private static async Task<(AvailableUpdate? Best, List<Exception> Errors, int SuccessfulSources)> FindBestUpdateAsync(
        IEnumerable<UpdateSourceSpec> sources,
        CancellationToken ct)
    {
        AvailableUpdate? best = null;
        var errors = new List<Exception>();
        var successful = 0;

        // Update feeds are tiny. Query both independent feeds so a stale mirror can never hide a
        // newer release published to the other source.
        foreach (var source in sources.OrderBy(static item => item.Priority))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var manager = source.CreateManager();
                var info = await RunWithTimeout(
                        () => manager.CheckForUpdatesAsync(),
                        CheckTimeoutPerSource,
                        ct)
                    .ConfigureAwait(false);

                successful++;
                if (info?.TargetFullRelease is not VelopackAsset target)
                    continue;

                var candidate = new AvailableUpdate(source.Name, source.Priority, manager, info);
                if (best is null ||
                    SemanticVersion.CompareByVersion(target.Version, best.Info.TargetFullRelease.Version) > 0 ||
                    (SemanticVersion.CompareByVersion(target.Version, best.Info.TargetFullRelease.Version) == 0 &&
                     candidate.Priority < best.Priority))
                {
                    best = candidate;
                }
            }
            catch (Exception ex) when (!IsCancellation(ex, ct))
            {
                errors.Add(new InvalidOperationException($"{source.Name}: {ex.Message}", ex));
            }
        }

        return (best, errors, successful);
    }

    private static async Task<AvailableUpdate?> FindDownloadFallbackAsync(
        IEnumerable<UpdateSourceSpec> sources,
        AvailableUpdate failed,
        SemanticVersion minimumVersion,
        CancellationToken ct)
    {
        AvailableUpdate? fallback = null;

        foreach (var source in sources
                     .Where(item => !item.Name.Equals(failed.Name, StringComparison.Ordinal))
                     .OrderBy(static item => item.Priority))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var manager = source.CreateManager();
                var info = await RunWithTimeout(
                        () => manager.CheckForUpdatesAsync(),
                        CheckTimeoutPerSource,
                        ct)
                    .ConfigureAwait(false);

                if (info?.TargetFullRelease is not VelopackAsset target ||
                    SemanticVersion.CompareByVersion(target.Version, minimumVersion) < 0)
                    continue;

                var candidate = new AvailableUpdate(source.Name, source.Priority, manager, info);
                if (fallback is null ||
                    SemanticVersion.CompareByVersion(target.Version, fallback.Info.TargetFullRelease.Version) > 0)
                {
                    fallback = candidate;
                }
            }
            catch (Exception ex) when (!IsCancellation(ex, ct))
            {
                _ = ex;
            }
        }

        return fallback;
    }

    private static string BuildAllSourcesFailedError(IReadOnlyCollection<Exception> errors)
    {
        var last = errors.LastOrDefault()?.Message;
        return string.IsNullOrWhiteSpace(last)
            ? "Не удалось связаться с источниками обновлений. Проверь интернет и попробуй ещё раз."
            : "Не удалось связаться с источниками обновлений. Проверь интернет и попробуй ещё раз.\n\n" + last;
    }

    private static void StartUpdaterAndExit(UpdateManager manager, VelopackAsset toApply, bool silent, bool restart)
    {
        try
        {
            manager.WaitExitThenApplyUpdates(toApply, silent: silent, restart: restart);
            RequestAppShutdown();
            ForceExitSoon(TimeSpan.FromSeconds(15));
        }
        catch (Exception ex)
        {
            if (!silent)
                ShowError("Не удалось применить обновление", BuildFriendlyError("Пакет скачан, но updater не смог его применить.", ex));
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

    private static async Task<T> RunWithTimeout<T>(Func<Task<T>> action, TimeSpan timeout, CancellationToken ct)
    {
        var task = action();
        var delayTask = Task.Delay(timeout, ct);
        var finished = await Task.WhenAny(task, delayTask).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();

        if (finished == delayTask)
            throw new TimeoutException($"Operation timed out after {timeout.TotalSeconds:0}s");

        return await task.ConfigureAwait(false);
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
                    if (app.MainWindow != null)
                    {
                        try { app.MainWindow.Close(); } catch { }
                    }
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
                "Не удалось найти сервер обновлений. Проверь DNS, прокси/VPN или попробуй другую сеть.",
            NetworkErrorKind.TlsOrSsl =>
                "Windows не смог установить защищённое соединение. Проверь дату и время системы, антивирус и системный прокси.",
            NetworkErrorKind.Timeout =>
                "Сервер отвечает слишком долго. Лаунчер автоматически пробует независимый резервный источник.",
            NetworkErrorKind.ConnectionRefusedOrReset =>
                "Соединение было сброшено. Проверь сеть или попробуй ещё раз через несколько минут.",
            _ =>
                "Проверь соединение с интернетом и повтори попытку."
        };

        var detail = FindUsefulMessage(ex);
        return string.IsNullOrWhiteSpace(detail)
            ? $"{title}\n\n{hint}"
            : $"{title}\n\n{hint}\n\n{detail}";
    }

    private static string FindUsefulMessage(Exception ex)
    {
        var current = ex;
        while (current.InnerException is not null)
            current = current.InnerException;
        return (current.Message ?? string.Empty).Trim();
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
        for (var current = ex; current != null; current = current.InnerException)
        {
            if (current is TimeoutException or TaskCanceledException)
                return NetworkErrorKind.Timeout;

            if (current is HttpRequestException requestError)
            {
                if (requestError.InnerException is SocketException socket)
                {
                    if (socket.SocketErrorCode is SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain)
                        return NetworkErrorKind.DnsOrHostNotFound;
                    if (socket.SocketErrorCode == SocketError.TimedOut)
                        return NetworkErrorKind.Timeout;
                    if (socket.SocketErrorCode is SocketError.ConnectionRefused or SocketError.ConnectionReset or
                        SocketError.NetworkReset or SocketError.HostUnreachable or SocketError.NetworkUnreachable)
                        return NetworkErrorKind.ConnectionRefusedOrReset;
                }

                var message = requestError.Message ?? "";
                if (message.Contains("No such host is known", StringComparison.OrdinalIgnoreCase) ||
                    message.Contains("Name or service not known", StringComparison.OrdinalIgnoreCase))
                    return NetworkErrorKind.DnsOrHostNotFound;

                if (message.Contains("SSL", StringComparison.OrdinalIgnoreCase) ||
                    message.Contains("TLS", StringComparison.OrdinalIgnoreCase) ||
                    message.Contains("authentication failed", StringComparison.OrdinalIgnoreCase))
                    return NetworkErrorKind.TlsOrSsl;
            }

            var text = current.Message ?? "";
            if (text.Contains("SSL", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("TLS", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("authentication failed", StringComparison.OrdinalIgnoreCase))
                return NetworkErrorKind.TlsOrSsl;
        }

        return NetworkErrorKind.Unknown;
    }

    private static bool ConfirmUpdate(
        string title,
        string message,
        string targetVersion,
        string source,
        string primaryText)
    {
        try
        {
            var app = Application.Current;
            if (app?.Dispatcher is null) return false;

            bool Show() => LauncherUpdateDialog.Confirm(
                app.MainWindow,
                title,
                message,
                CurrentVersion,
                targetVersion,
                source,
                primaryText);

            return app.Dispatcher.CheckAccess() ? Show() : app.Dispatcher.Invoke(Show);
        }
        catch
        {
            return false;
        }
    }

    private static LauncherUpdateDialog? ShowProgress(string targetVersion, string source)
    {
        try
        {
            var app = Application.Current;
            if (app?.Dispatcher is null) return null;

            LauncherUpdateDialog CreateAndShow()
            {
                var dialog = LauncherUpdateDialog.CreateProgress(
                    app.MainWindow,
                    CurrentVersion,
                    targetVersion,
                    source);
                dialog.Show();
                return dialog;
            }

            return app.Dispatcher.CheckAccess()
                ? CreateAndShow()
                : app.Dispatcher.Invoke(CreateAndShow);
        }
        catch
        {
            return null;
        }
    }

    private static void SetProgress(LauncherUpdateDialog dialog, int value, string status)
    {
        try
        {
            if (dialog.Dispatcher.CheckAccess())
                dialog.SetProgress(value, status);
            else
                _ = dialog.Dispatcher.BeginInvoke(() => dialog.SetProgress(value, status));
        }
        catch { }
    }

    private static void SetProgressSource(LauncherUpdateDialog dialog, string source)
    {
        try
        {
            if (dialog.Dispatcher.CheckAccess())
                dialog.SetSource(source);
            else
                _ = dialog.Dispatcher.BeginInvoke(() => dialog.SetSource(source));
        }
        catch { }
    }

    private static void CloseProgress(LauncherUpdateDialog? dialog)
    {
        if (dialog is null) return;
        try
        {
            if (dialog.Dispatcher.CheckAccess())
                dialog.Close();
            else
                dialog.Dispatcher.Invoke(dialog.Close);
        }
        catch { }
    }

    private static void ShowInfo(string title, string text)
        => ShowMessage(title, text, error: false);

    private static void ShowError(string title, string text)
        => ShowMessage(title, text, error: true);

    private static void ShowMessage(string title, string text, bool error)
    {
        try
        {
            var app = Application.Current;
            if (app?.Dispatcher is null) return;

            void Show() => LauncherUpdateDialog.ShowMessage(
                app.MainWindow,
                title,
                text,
                CurrentVersion,
                error: error);

            if (app.Dispatcher.CheckAccess()) Show();
            else app.Dispatcher.Invoke(Show);
        }
        catch { }
    }
}
