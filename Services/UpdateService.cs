using System;
using System.Threading.Tasks;
using System.Windows;
using Velopack;
using Velopack.Sources;

namespace LegendBorn.Services;

public static class UpdateService
{
    // Repo URL БЕЗ .git
    private const string RepoUrl = "https://github.com/LegendsDie/LegendBornLauncher";

    private static GithubSource CreateSource()
    {
        // Публичный репо -> токен не нужен
        return new GithubSource(
            repoUrl: RepoUrl,
            accessToken: null,
            prerelease: false
        );
    }

    /// <summary>
    /// silent=true  -> вообще без окон (полностью тихо)
    /// silent=false -> показать окно ТОЛЬКО если есть обновление.
    /// showNoUpdates=true -> при ручной проверке покажет "обновлений нет".
    /// </summary>
    public static async Task CheckAndUpdateAsync(bool silent, bool showNoUpdates = false)
    {
        var mgr = new UpdateManager(CreateSource());

        // Обновления работают только у установленной версии (через Setup.exe / Velopack install).
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

            // Применяем после закрытия приложения и перезапускаем
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
            // На случай если в будущих версиях UpdateManager станет IDisposable (без поломки текущей)
            (mgr as IDisposable)?.Dispose();
        }
    }
}
