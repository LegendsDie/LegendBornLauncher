using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using Velopack;
using LegendBorn.Services;

namespace LegendBorn;

public partial class App : Application
{
    // Глобальные сервисы (минимально без DI)
    public static ConfigService Config { get; private set; } = null!;
    public static TokenStore Tokens { get; private set; } = null!;
    public static LogService Log { get; private set; } = null!;

    [STAThread]
    private static void Main(string[] args)
    {
        // Velopack hooks должны вызываться до старта WPF UI.
        // В релизе не даём этому уронить запуск (на случай странной среды/падения native части).
        try
        {
            VelopackApp.Build().Run();
        }
        catch
        {
            // ignore (release-safe)
        }

        // Bootstrap settings ДО создания UI/VM/окон
        try
        {
            SettingsBootstrapper.Bootstrap();
        }
        catch
        {
            // ignore (release-safe)
        }

        // Папки / пути
        try
        {
            Directory.CreateDirectory(LauncherPaths.AppDir);
            Directory.CreateDirectory(LauncherPaths.LogsDir);
        }
        catch
        {
            // ignore (release-safe)
        }

        // Инициализация базовых сервисов (конфиг, токены, лог)
        try
        {
            Log = new LogService(Path.Combine(LauncherPaths.LogsDir, "launcher.log"));
        }
        catch
        {
            // если лог не поднялся — делаем заглушку, чтобы не падать
            Log = LogService.Noop;
        }

        try
        {
            Config = new ConfigService(LauncherPaths.ConfigFile);
            Config.LoadOrCreate();
        }
        catch (Exception ex)
        {
            // конфиг не должен ломать запуск
            Log.Error("Config init failed", ex);
            // fallback: пустой конфиг в памяти
            Config = new ConfigService(LauncherPaths.ConfigFile);
            try { Config.LoadOrCreate(); } catch { }
        }

        try
        {
            Tokens = new TokenStore(LauncherPaths.TokenFile);
        }
        catch (Exception ex)
        {
            Log.Error("TokenStore init failed", ex);
            // fallback на временный путь
            var tmp = Path.Combine(Path.GetTempPath(), "LegendBornLauncher.tokens.dat");
            Tokens = new TokenStore(tmp);
        }

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Глобальная защита от падений UI
        DispatcherUnhandledException += (_, ex) =>
        {
            try { Log.Error("UI unhandled exception", ex.Exception); } catch { }
            MessageBox.Show(
                "Произошла непредвиденная ошибка. Подробности записаны в лог лаунчера.",
                "LegendBorn Launcher",
                MessageBoxButton.OK,
                MessageBoxImage.Error);

            ex.Handled = true; // не роняем приложение
        };

        // Ошибки из задач/потоков
        TaskScheduler.UnobservedTaskException += (_, ex) =>
        {
            try { Log.Error("Unobserved task exception", ex.Exception); } catch { }
            ex.SetObserved();
        };

        AppDomain.CurrentDomain.UnhandledException += (_, ex) =>
        {
            try { Log.Error("AppDomain unhandled exception", ex.ExceptionObject as Exception); } catch { }
            // тут уже может быть поздно показывать UI, но лог запишем
        };

        // Лог запуска
        try { Log.Info($"Launcher started. Version: {GetType().Assembly.GetName().Version}"); } catch { }
    }
}
