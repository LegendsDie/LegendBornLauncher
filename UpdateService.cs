using System;
using System.Threading.Tasks;
using System.Windows;
using Velopack;
using Velopack.Sources;

namespace LegendBorn;

public static class UpdateService
{
    // Repo URL БЕЗ .git
    private const string RepoUrl = "https://github.com/LegendsDie/LegendBornLauncher";

    private static GithubSource CreateSource()
    {
        // Публичный репо -> accessToken не нужен
        return new GithubSource(
            repoUrl: RepoUrl,
            accessToken: null,
            prerelease: false
        );
    }

    /// <summary>
    /// silent=true  -> без окон, тихо
    /// silent=false -> показывает окна
    /// </summary>
    public static async Task CheckAndUpdateAsync(bool silent)
    {
        var mgr = new UpdateManager(CreateSource());

        // Обновления работают только у установленной версии (через Setup.exe).
        // Из Rider обычно приложение не установлено — просто выходим.
        if (!mgr.IsInstalled)
            return;

        try
        {
            // Если апдейт уже скачан и ждёт перезапуска — применяем сразу
            if (mgr.UpdatePendingRestart is { } pending)
            {
                mgr.ApplyUpdatesAndRestart(pending);
                return;
            }

            var updates = await mgr.CheckForUpdatesAsync();
            if (updates is null)
            {
                if (!silent)
                {
                    MessageBox.Show(
                        "Обновлений нет.",
                        "Обновление",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }

                return;
            }

            var target = updates.TargetFullRelease;

            if (!silent)
            {
                var ask = MessageBox.Show(
                    $"Доступно обновление: {target.Version}\n\nСкачать и перезапустить?",
                    "Обновление",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Information);

                if (ask != MessageBoxResult.Yes)
                    return;
            }

            await mgr.DownloadUpdatesAsync(updates);

            // Применяем после выхода приложения + перезапуск
            mgr.WaitExitThenApplyUpdates(target, restart: true);
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            if (!silent)
            {
                MessageBox.Show(
                    $"Ошибка обновления:\n{ex}",
                    "Обновление",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }
    }
}
