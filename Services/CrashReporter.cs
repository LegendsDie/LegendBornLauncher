using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace LegendBorn.Services;

public sealed class CrashReporter
{
    private readonly LogService _log;

    private int _installed;
    private int _uiCrashDialogShown;

    private readonly object _crashWriteLock = new();

    public CrashReporter(LogService log)
    {
        _log = log ?? LogService.Noop;

        try
        {
            LauncherPaths.EnsureDir(LauncherPaths.CrashDir);
        }
        catch { }
    }

    public void Install(Application app)
    {
        if (app is null) return;
        if (Interlocked.Exchange(ref _installed, 1) == 1) return;

        try
        {
            app.DispatcherUnhandledException += OnDispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
            TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

            try { _log.Info("CrashReporter installed."); } catch { }
        }
        catch (Exception ex)
        {
            try { _log.Error("CrashReporter install failed", ex); } catch { }
        }
    }

    private void OnDispatcherUnhandledException(object? sender, DispatcherUnhandledExceptionEventArgs e)
    {
        try
        {
            WriteCrash("DispatcherUnhandledException", e.Exception);
            try { _log.Error("UI unhandled exception", e.Exception); } catch { }
        }
        catch { }

        try
        {
            if (Interlocked.Exchange(ref _uiCrashDialogShown, 1) == 0)
            {
                MessageBox.Show(
                    "Произошла непредвиденная ошибка. Подробности записаны в crash-лог.\n\n" +
                    "Рекомендуется перезапустить лаунчер.",
                    "LegendBorn Launcher",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }
        catch { }

        e.Handled = true;
    }

    private void OnAppDomainUnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        try
        {
            var ex = e.ExceptionObject as Exception;
            WriteCrash("AppDomainUnhandledException", ex);
            try { _log.Error("AppDomain unhandled exception", ex); } catch { }
        }
        catch { }
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        try
        {
            WriteCrash("UnobservedTaskException", e.Exception);
            try { _log.Error("Unobserved task exception", e.Exception); } catch { }
        }
        catch { }

        try { e.SetObserved(); } catch { }
    }

    private void WriteCrash(string kind, Exception? ex)
    {
        lock (_crashWriteLock)
        {
            try
            {
                LauncherPaths.EnsureDir(LauncherPaths.CrashDir);

                var ts = DateTimeOffset.Now;
                var stamp = ts.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
                var unique = Guid.NewGuid().ToString("N")[..8];

                var safeKind = MakeSafeFileToken(kind);
                var file = Path.Combine(LauncherPaths.CrashDir, $"crash_{stamp}_{safeKind}_{unique}.log");

                var sb = new StringBuilder(16 * 1024);

                sb.AppendLine("LegendBorn Launcher Crash Report");
                sb.AppendLine("--------------------------------");
                sb.AppendLine($"TimeLocal: {ts:O}");
                sb.AppendLine($"TimeUtc:   {DateTimeOffset.UtcNow:O}");
                sb.AppendLine($"Kind:      {kind}");
                sb.AppendLine($"Launcher:  {SafeVersion()}");
                sb.AppendLine($"PID:       {Environment.ProcessId}");
                sb.AppendLine($"64-bit:    {Environment.Is64BitProcess}");
                sb.AppendLine($"OS:        {RuntimeInformation.OSDescription}");
                sb.AppendLine($"Runtime:   {RuntimeInformation.FrameworkDescription}");
                sb.AppendLine($"Culture:   {CultureInfo.CurrentCulture.Name} / UI {CultureInfo.CurrentUICulture.Name}");
                sb.AppendLine($"Exe:       {Environment.ProcessPath ?? "unknown"}");
                sb.AppendLine($"CWD:       {Environment.CurrentDirectory}");
                sb.AppendLine($"Args:      {SafeArgs()}");
                sb.AppendLine();

                sb.AppendLine("Paths:");
                sb.AppendLine($"  AppDir:   {SafePath(() => LauncherPaths.AppDir)}");
                sb.AppendLine($"  LocalDir: {SafePath(() => LauncherPaths.LocalDir)}");
                sb.AppendLine($"  LogsDir:  {SafePath(() => LauncherPaths.LogsDir)}");
                sb.AppendLine($"  CrashDir: {SafePath(() => LauncherPaths.CrashDir)}");
                sb.AppendLine($"  CacheDir: {SafePath(() => LauncherPaths.CacheDir)}");
                sb.AppendLine($"  Config:   {SafePath(() => LauncherPaths.ConfigFile)}");
                sb.AppendLine($"  Tokens:   {SafePath(() => LauncherPaths.TokenFile)}");
                sb.AppendLine($"  LogFile:  {SafePath(() => LauncherPaths.LauncherLogFile)}");
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

                sb.AppendLine();
                sb.AppendLine("Last log tail:");
                sb.AppendLine(ReadLogTailSafe(LauncherPaths.LauncherLogFile, maxBytes: 16_384));

                File.WriteAllText(file, sb.ToString(), Encoding.UTF8);
            }
            catch
            {
            }
        }
    }

    private static string SafeVersion()
    {
        try { return LauncherIdentity.InformationalVersion; }
        catch
        {
            try { return typeof(CrashReporter).Assembly.GetName().Version?.ToString() ?? "?"; }
            catch { return "?"; }
        }
    }

    private static string SafeArgs()
    {
        try
        {
            var args = Environment.GetCommandLineArgs();
            if (args is null || args.Length == 0) return "";
            return string.Join(" ", args);
        }
        catch
        {
            return "";
        }
    }

    private static string SafePath(Func<string> getter)
    {
        try { return getter()?.ToString() ?? ""; }
        catch { return ""; }
    }

    private static string MakeSafeFileToken(string s)
    {
        s = (s ?? "").Trim();
        if (string.IsNullOrWhiteSpace(s)) return "Unknown";

        foreach (var c in Path.GetInvalidFileNameChars())
            s = s.Replace(c, '_');

        return s;
    }

    private static string ReadLogTailSafe(string path, int maxBytes)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path)) return "<no log path>";
            if (!File.Exists(path)) return "<log file not found>";

            maxBytes = Math.Clamp(maxBytes, 512, 256 * 1024);

            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (fs.Length <= 0) return "<empty log>";

            var len = (int)Math.Min(fs.Length, maxBytes);
            fs.Seek(-len, SeekOrigin.End);

            var buf = new byte[len];
            var read = fs.Read(buf, 0, len);

            var text = Encoding.UTF8.GetString(buf, 0, read);
            return text.TrimEnd();
        }
        catch (Exception ex)
        {
            return "<failed to read log tail: " + ex.Message + ">";
        }
    }
}
