using System;
using System.IO;
using System.Windows;
using LegendBorn.Services;
using Velopack;

namespace LegendBorn;

public partial class App : Application
{
    public static ConfigService Config { get; private set; } = null!;
    public static TokenStore Tokens { get; private set; } = null!;
    public static LogService Log { get; private set; } = null!;
    public static CrashReporter Crash { get; private set; } = null!;

    [STAThread]
    private static void Main(string[] args)
    {
        try
        {
            VelopackApp.Build().Run();
        }
        catch
        {
        }

        string logPath = "";
        string configPath = "";
        string tokenPath = "";

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
            try { Log.Info("LogService initialized."); } catch { }
        }
        catch
        {
            Log = LogService.Noop;
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

        // ✅ bootstrap before ConfigService, but WITHOUT forced overrides
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

        // ✅ фиксируем старт
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

        try { Config?.Save(); } catch { }

        try { Log.Dispose(); } catch { }

        base.OnExit(e);
    }
}
