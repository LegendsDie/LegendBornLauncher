using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using LegendBorn.Services;

namespace LegendBorn;

public sealed partial class MainViewModel
{
    // ===== Release-safety flags =====
    private volatile bool _isClosing;

    // общий lifetime токен для любых длительных операций
    private readonly CancellationTokenSource _lifetimeCts = new();

    /// <summary>
    /// Вызови в MainWindow.Closing, чтобы остановить любые фоновые операции/обновления.
    /// </summary>
    public void MarkClosing()
    {
        _isClosing = true;

        try { _lifetimeCts.Cancel(); } catch { }

        CancelLoginWait();

        // ВАЖНО: не Dispose'им CTS здесь — чтобы фоновые таски не ловили ObjectDisposedException.
        // CLR сам заберёт ресурсы при закрытии процесса.
    }

    // ===== merged login strip text (for Start->Nick block) =====
    public string LoginStateText
    {
        get
        {
            try
            {
                if (IsLoggedIn) return "Вход выполнен.";
                if (IsWaitingSiteConfirm) return "Ожидаю подтверждение входа на сайте...";
                return "Требуется вход.";
            }
            catch
            {
                return "—";
            }
        }
    }

    // ===== UI helpers =====
    private void PostToUi(Action action, DispatcherPriority priority = DispatcherPriority.Background)
    {
        try
        {
            if (_isClosing) return;

            var disp = Application.Current?.Dispatcher;
            if (disp is null) return;

            if (disp.CheckAccess()) action();
            else disp.BeginInvoke(action, priority);
        }
        catch { }
    }

    private void InvokeOnUi(Action action, DispatcherPriority priority = DispatcherPriority.Send)
    {
        try
        {
            if (_isClosing) return;

            var disp = Application.Current?.Dispatcher;
            if (disp is null) return;

            if (disp.CheckAccess()) action();
            else disp.Invoke(action, priority);
        }
        catch { }
    }

    // ===== Progress throttle (release-safe) =====
    private const int ProgressUiMinIntervalMs = 80; // ~12.5 fps

    private int _pendingProgress = -1;
    private long _lastProgressUiTick;
    private int _progressPumpScheduled; // 0/1

    private void OnMinecraftProgress(int p)
    {
        if (_isClosing) return;

        if (p < 0) p = 0;
        if (p > 100) p = 100;

        Interlocked.Exchange(ref _pendingProgress, p);

        var now = Environment.TickCount64;
        var elapsed = now - Interlocked.Read(ref _lastProgressUiTick);

        if (elapsed >= ProgressUiMinIntervalMs)
        {
            PumpProgressToUi();
            return;
        }

        if (Interlocked.CompareExchange(ref _progressPumpScheduled, 1, 0) != 0)
            return;

        var delay = (int)Math.Max(1, ProgressUiMinIntervalMs - elapsed);

        _ = Task.Run(async () =>
        {
            try { await Task.Delay(delay).ConfigureAwait(false); }
            catch { }
            finally
            {
                Interlocked.Exchange(ref _progressPumpScheduled, 0);
                PumpProgressToUi();
            }
        });
    }

    private void PumpProgressToUi()
    {
        if (_isClosing) return;

        var p = Interlocked.Exchange(ref _pendingProgress, -1);
        if (p < 0) return;

        Interlocked.Exchange(ref _lastProgressUiTick, Environment.TickCount64);

        PostToUi(() => ProgressPercent = p);
    }

    // ===== Misc helpers =====
    private static string EnsureSlash(string url)
    {
        url = (url ?? "").Trim();
        if (string.IsNullOrWhiteSpace(url))
            return "";

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return "";

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            return "";

        var normalized = uri.ToString();
        if (!normalized.EndsWith("/", StringComparison.Ordinal))
            normalized += "/";

        return normalized;
    }

    private static string FormatLoaderName(string? loaderType)
    {
        var t = (loaderType ?? "vanilla").Trim().ToLowerInvariant();
        return t switch
        {
            "vanilla" => "Vanilla",
            "neoforge" => "NeoForge",
            "forge" => "Forge",
            "fabric" => "Fabric",
            "quilt" => "Quilt",
            _ => string.IsNullOrWhiteSpace(loaderType) ? "Vanilla" : loaderType.Trim()
        };
    }

    private void SetVersionsUi(string label)
    {
        Versions.Clear();
        Versions.Add(label);
        SelectedVersion = label;
    }

    private static string MakeAutoVersionLabel(ServerEntry s)
    {
        var loaderType = (s.LoaderName ?? "vanilla").Trim().ToLowerInvariant();
        var lver = (s.LoaderVersion ?? "").Trim();
        var mc = (s.MinecraftVersion ?? "1.21.1").Trim();

        string L(string? t) => FormatLoaderName(t);

        if (loaderType == "vanilla" || string.IsNullOrWhiteSpace(loaderType))
            return $"AUTO • {L(loaderType)} {mc}";

        if (string.IsNullOrWhiteSpace(lver))
            return $"AUTO • {L(loaderType)} ({mc})";

        return $"AUTO • {L(loaderType)} {lver} ({mc})";
    }

    // ===== Logs =====
    private const int MaxLogLines = 100;

    private static readonly string LogDir = LauncherPaths.LogsDir;
    private static readonly string LogFile = Path.Combine(LogDir, "launcher.log");

    private long _lastFileLogTick;

    private void AppendLog(string text)
    {
        if (_isClosing) return;

        var line = $"[{DateTime.Now:HH:mm:ss}] {text}";

        PostToUi(() =>
        {
            if (_isClosing) return;

            LogLines.Add(line);

            while (LogLines.Count > MaxLogLines)
                LogLines.RemoveAt(0);
        });

        TryAppendLogToFile(line);
    }

    private void TryAppendLogToFile(string line)
    {
        try
        {
            if (_isClosing) return;

            var now = Environment.TickCount64;
            var elapsed = now - Interlocked.Read(ref _lastFileLogTick);
            if (elapsed < 10) return;
            Interlocked.Exchange(ref _lastFileLogTick, now);

            Directory.CreateDirectory(LogDir);

            try
            {
                if (File.Exists(LogFile))
                {
                    var fi = new FileInfo(LogFile);
                    if (fi.Length > 2_000_000)
                    {
                        var bak = Path.Combine(LogDir, $"launcher_{DateTime.Now:yyyyMMdd_HHmmss}.log");
                        File.Move(LogFile, bak, overwrite: true);
                    }
                }
            }
            catch { }

            File.AppendAllText(LogFile, line + Environment.NewLine);
        }
        catch { }
    }

    // ===== Config helpers =====
    private void TrySetConfigValueSafe(Action updateAction)
    {
        try
        {
            updateAction();
            ScheduleConfigSave();
        }
        catch
        {
            // ignore
        }
    }

    // ===== CanExecute refresh =====
    private void RefreshCanStates()
    {
        if (_isClosing) return;

        Raise(nameof(CanPlay));
        Raise(nameof(CanStop));
        Raise(nameof(PlayButtonText));
        Raise(nameof(LoginButtonText));
        Raise(nameof(HasLoginUrl));
        Raise(nameof(LoginStateText));

        if (!_commandsReady) return;

        RefreshVersionsCommand.RaiseCanExecuteChanged();
        PlayCommand.RaiseCanExecuteChanged();
        StopGameCommand.RaiseCanExecuteChanged();

        LoginViaSiteCommand.RaiseCanExecuteChanged();
        SiteLogoutCommand.RaiseCanExecuteChanged();

        OpenProfileCommand.RaiseCanExecuteChanged();

        OpenLoginUrlCommand.RaiseCanExecuteChanged();
        CopyLoginUrlCommand.RaiseCanExecuteChanged();

        CheckLauncherUpdatesCommand.RaiseCanExecuteChanged();
    }

    private void RaisePackPresentation()
    {
        Raise(nameof(PackName));
        Raise(nameof(MinecraftVersion));
        Raise(nameof(LoaderName));
        Raise(nameof(LoaderVersion));
        Raise(nameof(BuildDisplayName));
    }

    private void UpdateRezoniteFromProfile()
    {
        try
        {
            var v = Profile?.Rezonite ?? 0;
            if (v < 0) v = 0;
            Rezonite = v;
        }
        catch
        {
            Rezonite = 0;
        }
    }

    // ===== MC name helpers =====
    private static bool IsValidMcName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        name = name.Trim();
        if (name.Length is < 3 or > 16) return false;

        foreach (var ch in name)
        {
            var ok =
                (ch >= 'a' && ch <= 'z') ||
                (ch >= 'A' && ch <= 'Z') ||
                (ch >= '0' && ch <= '9') ||
                ch == '_';
            if (!ok) return false;
        }

        return true;
    }

    private static string MakeValidMcName(string name)
    {
        var cleaned = new string((name ?? "").Where(ch =>
            (ch >= 'a' && ch <= 'z') ||
            (ch >= 'A' && ch <= 'Z') ||
            (ch >= '0' && ch <= '9') ||
            ch == '_').ToArray());

        if (cleaned.Length < 3) cleaned = (cleaned + "___").Substring(0, 3);
        if (cleaned.Length > 16) cleaned = cleaned.Substring(0, 16);
        return cleaned;
    }

    // ===== init wrapper =====
    private async Task InitializeAsyncSafe()
    {
        try
        {
            await InitializeAsync(_lifetimeCts.Token);
        }
        catch (OperationCanceledException)
        {
            AppendLog("Инициализация: отменено.");
        }
        catch (Exception ex)
        {
            AppendLog("Инициализация: ошибка.");
            AppendLog(ex.Message);
        }
    }
}
