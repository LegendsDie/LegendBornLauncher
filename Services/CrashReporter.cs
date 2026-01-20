using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace LegendBorn.Services;

/// <summary>
/// CrashReporter: ловит неперехваченные исключения и пишет crash-лог на диск.
/// Ничего не отправляет в интернет (безопасно для релиза).
/// </summary>
public sealed class CrashReporter
{
    private readonly LogService _log;
    private bool _installed;

    public CrashReporter(LogService log)
    {
        _log = log ?? LogService.Noop;

        // гарантируем папку крашей
        try
        {
            LauncherPaths.EnsureDir(LauncherPaths.CrashDir);
        }
        catch { }
    }

    /// <summary>
    /// Установить глобальные хэндлеры. Вызывать один раз на старте (App.OnStartup).
    /// </summary>
    public void Install(Application app)
    {
        if (_installed) return;
        _installed = true;

        try
        {
            app.DispatcherUnhandledException += OnDispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
            TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

            _log.Info("CrashReporter installed.");
        }
        catch (Exception ex)
        {
            _log.Error("CrashReporter install failed", ex);
        }
    }

    private void OnDispatcherUnhandledException(object? sender, DispatcherUnhandledExceptionEventArgs e)
    {
        try
        {
            WriteCrash("DispatcherUnhandledException", e.Exception);
            _log.Error("UI unhandled exception", e.Exception);
        }
        catch { }

        try
        {
            MessageBox.Show(
                "Произошла непредвиденная ошибка. Подробности записаны в crash-лог.",
                "LegendBorn Launcher",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        catch { }

        // релиз-safe: не роняем приложение
        e.Handled = true;
    }

    private void OnAppDomainUnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        try
        {
            var ex = e.ExceptionObject as Exception;
            WriteCrash("AppDomainUnhandledException", ex);
            _log.Error("AppDomain unhandled exception", ex);
        }
        catch { }
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        try
        {
            WriteCrash("UnobservedTaskException", e.Exception);
            _log.Error("Unobserved task exception", e.Exception);
        }
        catch { }

        try { e.SetObserved(); } catch { }
    }

    private void WriteCrash(string kind, Exception? ex)
    {
        try
        {
            LauncherPaths.EnsureDir(LauncherPaths.CrashDir);

            var ts = DateTimeOffset.Now;

            // делаем имя файла уникальным (если несколько крашей в одну секунду)
            var stamp = ts.ToString("yyyyMMdd_HHmmss");
            var unique = Guid.NewGuid().ToString("N")[..8];

            var file = Path.Combine(LauncherPaths.CrashDir, $"crash_{stamp}_{kind}_{unique}.log");

            var sb = new StringBuilder();
            sb.AppendLine("LegendBorn Launcher Crash Report");
            sb.AppendLine("--------------------------------");
            sb.AppendLine($"Time: {ts:O}");
            sb.AppendLine($"Kind: {kind}");
            sb.AppendLine($"Launcher: {SafeVersion()}");
            sb.AppendLine($"OS: {Environment.OSVersion}");
            sb.AppendLine($"Runtime: {RuntimeInformation.FrameworkDescription}");
            sb.AppendLine($"Process: {(Environment.ProcessPath ?? "unknown")}");
            sb.AppendLine();

            if (ex is null)
            {
                sb.AppendLine("Exception: <null>");
            }
            else
            {
                sb.AppendLine("Exception:");
                sb.AppendLine(ex.ToString());
            }

            File.WriteAllText(file, sb.ToString(), Encoding.UTF8);
        }
        catch
        {
            // ignore
        }
    }

    private static string SafeVersion()
    {
        try
        {
            return LauncherIdentity.InformationalVersion;
        }
        catch
        {
            try { return typeof(CrashReporter).Assembly.GetName().Version?.ToString() ?? "?"; }
            catch { return "?"; }
        }
    }
}
