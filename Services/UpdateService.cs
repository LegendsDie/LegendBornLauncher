using System;
using System.Threading.Tasks;
using System.Windows;
using Velopack;
using Velopack.Sources;

namespace LegendBorn.Services;

public static class UpdateService
{
    // Можно задавать и URL, и "Owner/Repo". Мы нормализуем в URL.
    private const string RepoUrlOrSlug = "https://github.com/LegendsDie/LegendBornLauncher";

    private static GithubSource CreateSource()
    {
        var repoUrl = NormalizeGithubRepoUrl(RepoUrlOrSlug);
        return new GithubSource(repoUrl: repoUrl, accessToken: "", prerelease: false);
    }

    private static string NormalizeGithubRepoUrl(string input)
    {
        input = (input ?? "").Trim();
        if (string.IsNullOrWhiteSpace(input))
            return "https://github.com/LegendsDie/LegendBornLauncher";

        // "//github.com/Owner/Repo"
        if (input.StartsWith("//", StringComparison.Ordinal))
            input = "https:" + input;

        // already URL
        if (input.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            input.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            if (Uri.TryCreate(input, UriKind.Absolute, out var uri))
            {
                var parts = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2)
                    return $"https://github.com/{parts[0]}/{parts[1]}";
            }

            // fallback as-is (но лучше не доходить сюда)
            return input.Trim().TrimEnd('/');
        }

        // treat as slug: "Owner/Repo" or "github.com/Owner/Repo"
        var slug = input.Trim().TrimEnd('/');
        if (slug.StartsWith("github.com/", StringComparison.OrdinalIgnoreCase))
            slug = slug.Substring("github.com/".Length);

        var sp = slug.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (sp.Length >= 2)
            return $"https://github.com/{sp[0]}/{sp[1]}";

        return "https://github.com/LegendsDie/LegendBornLauncher";
    }

    public static async Task CheckAndUpdateAsync(bool silent, bool showNoUpdates = false)
    {
        UpdateManager? mgr = null;

        try
        {
            mgr = new UpdateManager(CreateSource());

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

            mgr.WaitExitThenApplyUpdates(target, restart: true);

            // корректно закрываем WPF, чтобы apply мог отработать
            Application.Current?.Dispatcher.Invoke(() =>
            {
                try { Application.Current.Shutdown(); } catch { }
            });
        }
        catch (Exception ex)
        {
            if (!silent)
                ShowError($"Ошибка обновления:\n{ex}");
        }
        finally
        {
            try { (mgr as IDisposable)?.Dispose(); } catch { }
        }
    }

    private static void ShowInfo(string text)
    {
        try
        {
            Application.Current?.Dispatcher.Invoke(() =>
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
            Application.Current?.Dispatcher.Invoke(() =>
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
            return Application.Current?.Dispatcher.Invoke(() =>
                MessageBox.Show(text, "Обновление лаунчера", MessageBoxButton.YesNo, MessageBoxImage.Information)
            ) ?? MessageBoxResult.No;
        }
        catch
        {
            return MessageBoxResult.No;
        }
    }
}
