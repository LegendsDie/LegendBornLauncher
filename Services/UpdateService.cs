using System;
using System.Threading.Tasks;
using System.Windows;
using Velopack;
using Velopack.Sources;

namespace LegendBorn.Services;

public static class UpdateService
{
    private const string RepoUrl = "https://github.com/LegendsDie/LegendBornLauncher";

    private static GithubSource CreateSource()
    {
        return new GithubSource(
            repoUrl: RepoUrl,
            accessToken: null,
            prerelease: false
        );
    }

    public static async Task CheckAndUpdateAsync(bool silent, bool showNoUpdates = false)
    {
        var mgr = new UpdateManager(CreateSource());

        if (!mgr.IsInstalled)
            return;

        try
        {
            if (mgr.UpdatePendingRestart is { } pending)
            {
                mgr.ApplyUpdatesAndRestart(pending);
                return;
            }

            var updates = await mgr.CheckForUpdatesAsync();
            if (updates is null)
            {
                if (!silent && showNoUpdates)
                {
                    MessageBox.Show(
                        "Обновлений лаунчера нет.",
                        "Обновление лаунчера",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }

                return;
            }

            var target = updates.TargetFullRelease;

            if (!silent)
            {
                var ask = MessageBox.Show(
                    $"Доступно обновление лаунчера: {target.Version}\n\nОбновить сейчас? Лаунчер перезапустится.",
                    "Обновление лаунчера",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Information);

                if (ask != MessageBoxResult.Yes)
                    return;
            }

            await mgr.DownloadUpdatesAsync(updates);

            mgr.WaitExitThenApplyUpdates(target, restart: true);
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            if (!silent)
            {
                MessageBox.Show(
                    $"Ошибка обновления:\n{ex}",
                    "Обновление лаунчера",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }
        finally
        {
            (mgr as IDisposable)?.Dispose();
        }
    }
}
