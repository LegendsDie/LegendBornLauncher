// File: /Services/UpdateService.cs
using System;
using System.Collections.Generic;
using System.Linq;
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
    private const string SelectelUpdateBaseUrl =
        "https://612cd759-4c9d-450e-bc91-a51d3c56e834.selstorage.ru/launcher/releases/";
    private const string Channel = "win";

    private static readonly TimeSpan CheckTimeoutPerSource = TimeSpan.FromSeconds(14);
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(10);

    private static readonly SemaphoreSlim Gate = new(1, 1);

    private sealed record UpdateSourceSpec(string Name, int Priority, Func<UpdateManager> CreateManager);
    private sealed record AvailableUpdate(string Name, int Priority, UpdateManager Manager, UpdateInfo Info);

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
        // Russia-friendly static S3-compatible feed first; GitHub remains an independent fallback.
        new UpdateSourceSpec("LegendBorn Selectel", 0, CreateSelectelManager),
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

        try
        {
            ct.ThrowIfCancellationRequested();

            var sources = CreateSources();
            var localManager = sources[0].CreateManager();

            if (!localManager.IsInstalled)
            {
                if (!silent && showNoUpdates)
                    ShowInfo("Лаунчер запущен без установки (Velopack не активен). Обновления недоступны.");
                return;
            }

            // Pending package is local state; it does not matter which remote source created it.
            if (localManager.UpdatePendingRestart is VelopackAsset pending)
            {
                if (!silent)
                {
                    var ask = ShowYesNo(
                        "Обновление уже скачано и готово к установке.\n\nПрименить сейчас? Лаунчер перезапустится.");
                    if (ask != MessageBoxResult.Yes)
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
                        ShowError(BuildAllSourcesFailedError(errors));
                    return;
                }

                if (!silent && showNoUpdates)
                    ShowInfo("Обновлений лаунчера нет.");
                return;
            }

            var target = best.Info.TargetFullRelease;
            if (!silent)
            {
                var ask = ShowYesNo(
                    $"Доступно обновление лаунчера: {target.Version}\n" +
                    $"Источник: {best.Name}\n\n" +
                    "Обновить сейчас? Лаунчер перезапустится.");

                if (ask != MessageBoxResult.Yes)
                    return;
            }

            try
            {
                using var dlCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                dlCts.CancelAfter(DownloadTimeout);

                await best.Manager.DownloadUpdatesAsync(best.Info, progress: null, cancelToken: dlCts.Token)
                    .ConfigureAwait(false);
            }
            catch (Exception firstDownloadError) when (!IsCancellation(firstDownloadError, ct))
            {
                // The selected source can disappear between feed read and asset download. If the
                // same-or-newer release exists on the other source, retry there before failing.
                var fallback = await FindDownloadFallbackAsync(
                        sources,
                        best,
                        target.Version,
                        ct)
                    .ConfigureAwait(false);

                if (fallback is null)
                {
                    if (!silent)
                        ShowError(BuildFriendlyError(
                            $"Не удалось скачать обновление с {best.Name} и резервный источник не помог.",
                            firstDownloadError));
                    return;
                }

                try
                {
                    using var fallbackCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    fallbackCts.CancelAfter(DownloadTimeout);
                    await fallback.Manager.DownloadUpdatesAsync(
                            fallback.Info,
                            progress: null,
                            cancelToken: fallbackCts.Token)
                        .ConfigureAwait(false);
                    best = fallback;
                    target = fallback.Info.TargetFullRelease;
                }
                catch (Exception fallbackError) when (!IsCancellation(fallbackError, ct))
                {
                    if (!silent)
                        ShowError(BuildFriendlyError(
                            "Не удалось скачать обновление ни через LegendBorn mirror, ни через GitHub.",
                            new AggregateException(firstDownloadError, fallbackError)));
                    return;
                }
            }

            ct.ThrowIfCancellationRequested();
            StartUpdaterAndExit(best.Manager, target, silent, restart: true);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (!silent)
                ShowError(BuildFriendlyError("Ошибка обновления.", ex));
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
                // Continue to the next independent source.
            }
        }

        return fallback;
    }

    private static string BuildAllSourcesFailedError(IReadOnlyCollection<Exception> errors)
    {
        var details = errors.Count == 0
            ? "Источники обновлений не ответили."
            : string.Join("\n\n", errors.Select(static error => error.Message));

        return
            "Не удалось проверить обновления ни через зеркало LegendBorn/Selectel, ни через GitHub.\n\n" +
            "Проверь интернет, системный прокси/DNS или попробуй другую сеть. Для пользователей из РФ " +
            "лаунчер сначала использует независимое Selectel-зеркало, поэтому GitHub не является обязательным.\n\n" +
            details;
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

    private static async Task<T> RunWithTimeout<T>(Func<Task<T>> action, TimeSpan timeout, CancellationToken ct)
    {
        var task = action();
        var delayTask = Task.Delay(timeout);
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
                "Система не может найти один из хостов обновления (DNS/фильтрация). " +
                "Лаунчер пробует и LegendBorn/Selectel, и GitHub. Проверь системный DNS, прокси/VPN или другую сеть.",

            NetworkErrorKind.TlsOrSsl =>
                "Ошибка защищённого соединения (TLS/SSL). Проверь дату/время Windows, HTTPS-сканирование антивируса, " +
                "системный прокси и корневые сертификаты.",

            NetworkErrorKind.Timeout =>
                "Истекло время ожидания. Возможны нестабильная сеть или фильтрация. Лаунчер использует два независимых источника обновлений.",

            NetworkErrorKind.ConnectionRefusedOrReset =>
                "Соединение было сброшено/отклонено. Проверь другую сеть, системный прокси/VPN и сетевые фильтры.",

            _ =>
                "Проверь соединение с интернетом. Лаунчер использует LegendBorn/Selectel как основной источник и GitHub как резервный."
        };

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
