// App.xaml.cs
using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using Velopack;
using LegendBorn.Services;

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
            // ignore (release-safe)
        }

        try
        {
            SettingsBootstrapper.Bootstrap();
        }
        catch
        {
            // ignore (release-safe)
        }

        try
        {
            LauncherPaths.EnsureAppDirs();
        }
        catch
        {
            // ignore (release-safe)
        }

        try
        {
            Log = new LogService(LauncherPaths.LauncherLogFile);
            Log.Info("LogService initialized.");
        }
        catch
        {
            Log = LogService.Noop;
        }

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

        try
        {
            Tokens = new TokenStore(LauncherPaths.TokenFile);
            Log.Info("TokenStore initialized.");
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
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { Log.Info("Launcher exiting."); } catch { }

        try { Config?.Save(); } catch { }

        try { Log.Dispose(); } catch { }

        base.OnExit(e);
    }
}
