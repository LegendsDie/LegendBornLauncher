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
    public static CrashReporter Crash { get; private set; } = null!;

    [STAThread]
    private static void Main(string[] args)
    {
        // Velopack hooks должны вызываться до старта WPF UI.
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
            LauncherPaths.EnsureAppDirs();
        }
        catch
        {
            // ignore (release-safe)
        }

        // ===== Лог =====
        try
        {
            Log = new LogService(LauncherPaths.LauncherLogFile);
            Log.Info("LogService initialized.");
        }
        catch
        {
            Log = LogService.Noop;
        }

        // ===== CrashReporter (до остального) =====
        try
        {
            Crash = new CrashReporter(Log);
            Log.Info("CrashReporter created.");
        }
        catch (Exception ex)
        {
            try { Log.Error("CrashReporter init failed", ex); } catch { }
            Crash = new CrashReporter(LogService.Noop);
        }

        // ===== Конфиг =====
        try
        {
            Config = new ConfigService(LauncherPaths.ConfigFile);
            Config.LoadOrCreate();
            Log.Info("ConfigService initialized.");
        }
        catch (Exception ex)
        {
            try { Log.Error("Config init failed", ex); } catch { }

            Config = new ConfigService(LauncherPaths.ConfigFile);
            try { Config.LoadOrCreate(); } catch { }
        }

        // ===== Токены =====
        try
        {
            Tokens = new TokenStore(LauncherPaths.TokenFile);
            Log.Info("TokenStore initialized.");
        }
        catch (Exception ex)
        {
            try { Log.Error("TokenStore init failed", ex); } catch { }

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

        // CrashReporter: подписки на глобальные исключения
        try
        {
            Crash.Install(this);
        }
        catch (Exception ex)
        {
            try { Log.Error("CrashReporter install failed", ex); } catch { }
        }

        // Дополнительная страховка (в релизе полезно держать)
        DispatcherUnhandledException += (_, ex) =>
        {
            try { Log.Error("UI unhandled exception (App hook)", ex.Exception); } catch { }

            try
            {
                MessageBox.Show(
                    "Произошла непредвиденная ошибка. Подробности записаны в лог лаунчера.",
                    "LegendBorn Launcher",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            catch { }

            ex.Handled = true;
        };

        TaskScheduler.UnobservedTaskException += (_, ex) =>
        {
            try { Log.Error("Unobserved task exception (App hook)", ex.Exception); } catch { }
            try { ex.SetObserved(); } catch { }
        };

        AppDomain.CurrentDomain.UnhandledException += (_, ex) =>
        {
            try { Log.Error("AppDomain unhandled exception (App hook)", ex.ExceptionObject as Exception); } catch { }
        };

        // Лог запуска
        try
        {
            var ver = LauncherIdentity.InformationalVersion;
            Log.Info($"Launcher started. Version: {ver}");
        }
        catch { }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { Log.Info("Launcher exiting."); } catch { }

        try
        {
            // Сбрасываем хвост очереди логов перед выходом
            Log.Dispose();
        }
        catch { }

        base.OnExit(e);
    }
}
