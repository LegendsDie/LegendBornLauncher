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

    // Если репо приватный — задай переменную окружения LEGENDBORN_GH_TOKEN
    // Если репо публичный — токен не нужен, может быть null
    private static string? GetToken() =>
        Environment.GetEnvironmentVariable("LEGENDBORN_GH_TOKEN");

    private static GithubSource CreateSource()
    {
        return new GithubSource(
            repoUrl: RepoUrl,
            accessToken: GetToken(),
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

        // Если репо приватный и токена нет — в silent режиме просто выходим.
        // (Иначе обычные пользователи увидят ошибки.)
        if (GetToken() is null)
        {
            if (!silent)
            {
                MessageBox.Show(
                    "Репозиторий обновлений приватный, а токен не задан.\n\n" +
                    "Варианты:\n" +
                    "1) Сделать репозиторий публичным\n" +
                    "2) Задать переменную окружения LEGENDBORN_GH_TOKEN\n",
                    "Обновление",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }

            return;
        }

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
