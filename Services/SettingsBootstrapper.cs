using System;
using System.Configuration;
using System.IO;
using LegendBorn.Properties;

namespace LegendBorn.Services;

/// <summary>
/// Релизный bootstrap пользовательских настроек.
/// Цели:
/// 1) Гарантировать Settings.Upgrade() один раз после обновления.
/// 2) Удерживать "схему" конфига (ConfigVersion) отдельно от версии лаунчера.
/// 3) Самовосстановление при битом user.config (ConfigurationErrorsException).
/// </summary>
internal static class SettingsBootstrapper
{
    // Config schema version (НЕ равно версии лаунчера).
    // Менять только при реальных изменениях схемы/миграциях.
    private const string ConfigSchemaVersion = "0.2.0";

    /// <summary>
    /// Вызвать один раз при старте приложения, до создания UI/VM.
    /// </summary>
    public static void Bootstrap()
    {
        try
        {
            EnsureUpgraded();
            EnsureSchemaVersion();
            SaveSafe();
        }
        catch (ConfigurationErrorsException cex)
        {
            // Битый user.config — удаляем и пробуем заново (release-safe).
            try { ResetCorruptedUserConfig(cex); } catch { }

            try
            {
                // Повторная попытка после reset
                EnsureUpgraded(force: true);
                EnsureSchemaVersion();
                SaveSafe();
            }
            catch
            {
                // В худшем случае: не валим запуск
            }
        }
        catch
        {
            // Не валим запуск в релизе
        }
    }

    private static void EnsureUpgraded(bool force = false)
    {
        try
        {
            // Если force=true — пытаемся Upgrade даже если флаг уже true (на случай reset).
            if (force || !Settings.Default.SettingsUpgraded)
            {
                try { Settings.Default.Upgrade(); } catch { }
                Settings.Default.SettingsUpgraded = true;
            }
        }
        catch { }
    }

    private static void EnsureSchemaVersion()
    {
        try
        {
            var cv = (Settings.Default.ConfigVersion ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(cv))
                cv = "0.0.0";

            // Здесь в будущем добавляются реальные миграции:
            // if (cv == "0.2.0") { ... } и т.п.

            if (!cv.Equals(ConfigSchemaVersion, StringComparison.OrdinalIgnoreCase))
                Settings.Default.ConfigVersion = ConfigSchemaVersion;
        }
        catch { }
    }

    private static void SaveSafe()
    {
        try { Settings.Default.Save(); }
        catch { }
    }

    /// <summary>
    /// Удаляет повреждённый user.config, чтобы Settings мог пересоздать файл.
    /// </summary>
    private static void ResetCorruptedUserConfig(ConfigurationErrorsException ex)
    {
        // Часто файл лежит по пути из ex.Filename.
        // Если он пустой — пытаемся вычислить стандартный user.config.
        var file = (ex.Filename ?? string.Empty).Trim();

        if (string.IsNullOrWhiteSpace(file) || !File.Exists(file))
        {
            file = TryResolveUserConfigPath();
        }

        if (string.IsNullOrWhiteSpace(file) || !File.Exists(file))
            return;

        try
        {
            // Чтобы не терять файл полностью — делаем backup рядом.
            var dir = Path.GetDirectoryName(file);
            var bak = Path.Combine(dir ?? "", $"user_corrupt_{DateTime.Now:yyyyMMdd_HHmmss}.config");
            File.Copy(file, bak, overwrite: true);
        }
        catch { }

        try
        {
            File.Delete(file);
        }
        catch { }
    }

    private static string TryResolveUserConfigPath()
    {
        try
        {
            // AppDomain.CurrentDomain.SetupInformation.ConfigurationFile — это обычно app.exe.config,
            // а user.config лежит глубже. Берём путь через ConfigurationManager.
            var config = ConfigurationManager.OpenExeConfiguration(ConfigurationUserLevel.PerUserRoamingAndLocal);
            return config.FilePath;
        }
        catch
        {
            return "";
        }
    }
}
