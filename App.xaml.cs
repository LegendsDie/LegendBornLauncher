using System;
using System.IO;
using System.Threading;
using System.Windows;
using LegendBorn.Services;
using Velopack;

namespace LegendBorn;

public partial class App : Application
{
    private const string SingleInstanceMutexName = @"Local\LegendBornLauncher.SingleInstance.v1";

    public static ConfigService Config { get; private set; } = null!;
    public static TokenStore Tokens { get; private set; } = null!;
    public static LogService Log { get; private set; } = null!;
    public static CrashReporter Crash { get; private set; } = null!;

    [STAThread]
    private static void Main(string[] args)
    {
        Exception? velopackInitError = null;
        try
        {
            // Velopack bootstrap must run before normal launcher initialization.
            VelopackApp.Build().Run();
        }
        catch (Exception ex)
        {
            velopackInitError = ex;
        }

        Mutex? instanceMutex = null;
        var ownsInstanceMutex = false;
        try
        {
            if (!TryAcquireSingleInstance(out instanceMutex, out ownsInstanceMutex))
            {
                try
                {
                    MessageBox.Show(
                        "LegendBorn Launcher уже запущен. Закрой первое окно перед повторным запуском.",
                        "LegendBorn",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
                catch
                {
                }
                return;
            }

            RunLauncher(velopackInitError);
        }
        finally
        {
            if (ownsInstanceMutex)
            {
                try { instanceMutex?.ReleaseMutex(); } catch { }
            }
            try { instanceMutex?.Dispose(); } catch { }
        }
    }

    private static bool TryAcquireSingleInstance(out Mutex? mutex, out bool ownsMutex)
    {
        mutex = null;
        ownsMutex = false;

        try
        {
            mutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var createdNew);
            if (createdNew)
            {
                ownsMutex = true;
                return true;
            }

            try
            {
                ownsMutex = mutex.WaitOne(0);
                return ownsMutex;
            }
            catch (AbandonedMutexException)
            {
                // The previous launcher crashed without releasing the OS mutex. Ownership is
                // transferred to this process, so a stale process marker never blocks startup.
                ownsMutex = true;
                return true;
            }
        }
        catch
        {
            // A mutex failure should not make the launcher unstartable. Pack/config writes still
            // have their own atomic protections; this guard primarily prevents normal double-click races.
            try { mutex?.Dispose(); } catch { }
            mutex = null;
            ownsMutex = false;
            return true;
        }
    }

    private static void RunLauncher(Exception? velopackInitError)
    {
        string logPath;
        string configPath;
        string tokenPath;

        try
        {
            LauncherPaths.EnsureAppDirs();
            logPath = LauncherPaths.LauncherLogFile;
            configPath = LauncherPaths.ConfigFile;
            tokenPath = LauncherPaths.TokenFile;
        }
        catch
        {
            var baseDir = Path.Combine(Path.GetTempPath(), "LegendBornLauncher");
            try { Directory.CreateDirectory(baseDir); } catch { }

            logPath = Path.Combine(baseDir, "launcher.log");
            configPath = Path.Combine(baseDir, "launcher.config.json");
            tokenPath = Path.Combine(baseDir, "tokens.dat");
        }

        try
        {
            Log = new LogService(logPath);
            Log.Info("LogService initialized.");
        }
        catch
        {
            Log = LogService.Noop;
        }

        if (velopackInitError is not null)
        {
            try { Log.Error("Velopack initialization failed. Auto-update may be unavailable.", velopackInitError); }
            catch { }
        }

        try
        {
            Crash = new CrashReporter(Log);
            try { Log.Info("CrashReporter created."); } catch { }
        }
        catch (Exception ex)
        {
            try { Log.Error("CrashReporter init failed", ex); } catch { }
            Crash = new CrashReporter(LogService.Noop);
        }

        try
        {
            SettingsBootstrapper.Bootstrap();
            try { Log.Info("SettingsBootstrapper done."); } catch { }
        }
        catch (Exception ex)
        {
            try { Log.Error("SettingsBootstrapper failed", ex); } catch { }
        }

        try
        {
            Config = new ConfigService(configPath);
            Config.LoadOrCreate();
            try
            {
                Log.Info($"ConfigService initialized. Schema={Config.Current.ConfigSchemaVersion}, RamMb={Config.Current.RamMb}");
            }
            catch { }
        }
        catch (Exception ex)
        {
            try { Log.Error("Config init failed", ex); } catch { }
            Config = new ConfigService(configPath);
            try { Config.LoadOrCreate(); } catch { }
        }

        try
        {
            Tokens = new TokenStore(tokenPath);
            try { Log.Info("TokenStore initialized."); } catch { }
        }
        catch (Exception ex)
        {
            try { Log.Error("TokenStore init failed", ex); } catch { }

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

        try
        {
            Crash.Install(this);
        }
        catch (Exception ex)
        {
            try { Log.Error("CrashReporter install failed", ex); } catch { }
        }

        try
        {
            var ver = LauncherIdentity.InformationalVersion;
            Log.Info($"Launcher started. Version: {ver}");
        }
        catch { }

        try
        {
            if (Config?.Current is not null)
            {
                Config.Current.LastLauncherStartUtc = DateTimeOffset.UtcNow;
                Config.Save();
            }
        }
        catch { }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { Log.Info("Launcher exiting."); } catch { }

        try
        {
            Config?.Flush();
        }
        catch (Exception ex)
        {
            try { Log.Error("Final config flush failed", ex); } catch { }
        }

        try { Config?.Dispose(); } catch { }
        try { Log.Flush(); } catch { }
        try { Log.Dispose(); } catch { }

        base.OnExit(e);
    }
}
