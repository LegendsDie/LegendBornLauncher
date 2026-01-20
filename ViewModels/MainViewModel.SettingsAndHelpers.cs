using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace LegendBorn.ViewModels;

public sealed partial class MainViewModel
{
    private volatile bool _isClosing;
    private readonly CancellationTokenSource _lifetimeCts = new();

    public void MarkClosing()
    {
        _isClosing = true;

        try { _lifetimeCts.Cancel(); } catch { }

        CancelLoginWait();
    }

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
            catch { return "—"; }
        }
    }

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

    private const int ProgressUiMinIntervalMs = 80;

    private int _pendingProgress = -1;
    private long _lastProgressUiTick;
    private int _progressPumpScheduled;

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
        InvokeOnUi(() =>
        {
            Versions.Clear();
            Versions.Add(label);
            SelectedVersion = label;
        });
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

    private const int MaxLogLines = 120;

    private void AppendLog(string text)
    {
        if (_isClosing) return;

        var uiLine = $"[{DateTime.Now:HH:mm:ss}] {text}";

        PostToUi(() =>
        {
            if (_isClosing) return;

            LogLines.Add(uiLine);

            while (LogLines.Count > MaxLogLines)
                LogLines.RemoveAt(0);
        });

        try { _log.Info(text); } catch { }
    }

    private void RefreshCanStates()
    {
        if (_isClosing) return;

        Raise(nameof(CanPlay));
        Raise(nameof(CanStop));
        Raise(nameof(PlayButtonText));
        Raise(nameof(LoginButtonText));
        Raise(nameof(HasLoginUrl));
        Raise(nameof(LoginStateText));
        Raise(nameof(RamMbText));

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

    // ===== RAM helpers (Windows) =====

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    private static long GetTotalPhysicalMemoryMb()
    {
        try
        {
            var ms = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            if (!GlobalMemoryStatusEx(ref ms))
                return 0;

            return (long)(ms.ullTotalPhys / (1024UL * 1024UL));
        }
        catch
        {
            return 0;
        }
    }

    private static int ComputeMaxAllowedRamMb(long totalMb)
    {
        if (totalMb <= 0)
            return 16384;

        // оставляем системе минимум 2GB, но не меньше 25% RAM (на слабых ПК)
        var reserve = Math.Max(2048, (int)(totalMb * 0.25));
        var max = (int)Math.Max(1024, totalMb - reserve);

        // не даём улетать слишком высоко
        max = Math.Min(max, RamMaxHardCapMb);
        return Math.Max(max, RamMinMb);
    }

    private static int ComputeRecommendedRamMb(long totalMb, int maxAllowedMb)
    {
        // простая, но рабочая шкала по типичным конфигам ПК
        int rec;
        if (totalMb <= 0) rec = 4096;
        else if (totalMb <= 6144) rec = 2048;
        else if (totalMb <= 10240) rec = 4096;
        else if (totalMb <= 14336) rec = 6144;
        else if (totalMb <= 20480) rec = 8192;
        else if (totalMb <= 28672) rec = 12288;
        else rec = 16384;

        rec = Math.Clamp(rec, RamMinMb, Math.Max(RamMinMb, maxAllowedMb));
        rec = (rec / 256) * 256;
        if (rec < RamMinMb) rec = RamMinMb;
        return rec;
    }
}
