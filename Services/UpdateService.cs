using System;
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

    private static readonly SemaphoreSlim _gate = new(1, 1);

    private static GithubSource CreateSource()
    {
        var repoUrl = NormalizeGithubRepoUrl(RepoUrlOrSlug);
        return new GithubSource(repoUrl: repoUrl, accessToken: "", prerelease: false);
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

        if (input.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            input.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            if (Uri.TryCreate(input, UriKind.Absolute, out var uri))
            {
                var parts = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2)
                    return $"https://github.com/{parts[0]}/{parts[1]}";
            }

            return input.Trim().TrimEnd('/');
        }

        var slug = input.Trim().TrimEnd('/');

        if (slug.StartsWith("github.com/", StringComparison.OrdinalIgnoreCase))
            slug = slug.Substring("github.com/".Length);

        var sp = slug.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (sp.Length >= 2)
            return $"https://github.com/{sp[0]}/{sp[1]}";

        return "https://github.com/LegendsDie/LegendBornLauncher";
    }

    public static async Task CheckAndUpdateAsync(
        bool silent,
        bool showNoUpdates = false,
        CancellationToken ct = default)
    {
        var entered = false;
        UpdateManager? mgr = null;

        try
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            entered = true;

            ct.ThrowIfCancellationRequested();

            mgr = CreateManager();

            if (!mgr.IsInstalled)
            {
                if (!silent && showNoUpdates)
                    ShowInfo("Лаунчер запущен без установки (Velopack не активен). Обновления недоступны.");
                return;
            }

            if (mgr.UpdatePendingRestart is { } pending)
            {
                mgr.ApplyUpdatesAndRestart(pending);
                return;
            }

            var updates = await mgr.CheckForUpdatesAsync().ConfigureAwait(false);
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

            await mgr.DownloadUpdatesAsync(updates!).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();

            mgr.WaitExitThenApplyUpdates(target, restart: true);

            try
            {
                Application.Current?.Dispatcher.Invoke(() =>
                {
                    try { Application.Current.Shutdown(); } catch { }
                });
            }
            catch { }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (!silent)
                ShowError($"Ошибка обновления:\n{ex}");
        }
        finally
        {
            if (entered)
            {
                try { _gate.Release(); } catch { }
            }
        }
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

            app.Dispatcher.Invoke(() =>
            {
                MessageBox.Show(text, "Обновление лаунчера", MessageBoxButton.OK, MessageBoxImage.Information);
            });
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

            app.Dispatcher.Invoke(() =>
            {
                MessageBox.Show(text, "Обновление лаунчера", MessageBoxButton.OK, MessageBoxImage.Error);
            });
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

            return app.Dispatcher.Invoke(() =>
                MessageBox.Show(text, "Обновление лаунчера", MessageBoxButton.YesNo, MessageBoxImage.Information));
        }
        catch
        {
            return MessageBoxResult.No;
        }
    }
}
