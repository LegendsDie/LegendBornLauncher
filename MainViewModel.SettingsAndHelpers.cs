using System;
using System.Configuration;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using LegendBorn.Properties;

namespace LegendBorn;

public sealed partial class MainViewModel
{
    // 0.1.10: config schema version (НЕ равно версии лаунчера)
    private const string ConfigSchemaVersion = "0.1.10";

    // ===== Settings migration =====
    private static void EnsureSettingsMigrated_019()
    {
        try
        {
            if (!Settings.Default.SettingsUpgraded)
            {
                try { Settings.Default.Upgrade(); } catch { }
                Settings.Default.SettingsUpgraded = true;
            }

            var cv = (Settings.Default.ConfigVersion ?? "").Trim();
            if (!cv.Equals(ConfigSchemaVersion, StringComparison.OrdinalIgnoreCase))
                Settings.Default.ConfigVersion = ConfigSchemaVersion;

            SaveSettingsSafe();
        }
        catch { }
    }

    private static void SaveSettingsSafe()
    {
        try { Settings.Default.Save(); }
        catch { }
    }

    // ===== UI helpers =====
    private void PostToUi(Action action, DispatcherPriority priority = DispatcherPriority.Background)
    {
        try
        {
            var disp = App.Current?.Dispatcher;
            if (disp is null) return;

            if (disp.CheckAccess()) action();
            else disp.BeginInvoke(action, priority);
        }
        catch { }
    }

    // ===== Progress throttle (0.1.10) =====
    private const int ProgressUiMinIntervalMs = 80; // ~12.5 fps

    private int _pendingProgress = -1;
    private long _lastProgressUiTick;
    private int _progressPumpScheduled; // 0/1

    private void OnMinecraftProgress(int p)
    {
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
        var p = Interlocked.Exchange(ref _pendingProgress, -1);
        if (p < 0) return;

        Interlocked.Exchange(ref _lastProgressUiTick, Environment.TickCount64);

        PostToUi(() => ProgressPercent = p);
    }

    // ===== Misc helpers =====
    private static string EnsureSlash(string url)
    {
        url = (url ?? "").Trim();
        if (!url.EndsWith("/")) url += "/";
        return url;
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

    private void AppendLog(string text)
    {
        // 0.1.10: BeginInvoke instead of Invoke (do not block background threads)
        PostToUi(() =>
        {
            LogLines.Add($"[{DateTime.Now:HH:mm:ss}] {text}");
            while (LogLines.Count > 500)
                LogLines.RemoveAt(0);
        });
    }

    // 0.1.10: lighter refresh (no tabs / no pack fields every time)
    private void RefreshCanStates()
    {
        Raise(nameof(CanPlay));
        Raise(nameof(CanStop));
        Raise(nameof(PlayButtonText));
        Raise(nameof(LoginButtonText));
        Raise(nameof(HasLoginUrl));

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
        var cleaned = new string(name.Where(ch =>
            (ch >= 'a' && ch <= 'z') ||
            (ch >= 'A' && ch <= 'Z') ||
            (ch >= '0' && ch <= '9') ||
            ch == '_').ToArray());

        if (cleaned.Length < 3) cleaned = (cleaned + "___").Substring(0, 3);
        if (cleaned.Length > 16) cleaned = cleaned.Substring(0, 16);
        return cleaned;
    }

    private static string? TryLoadStringSetting(string key, string? fallback)
    {
        try
        {
            var v = Settings.Default[key];
            if (v is string s) return string.IsNullOrWhiteSpace(s) ? fallback : s;
            return fallback;
        }
        catch (SettingsPropertyNotFoundException) { return fallback; }
        catch { return fallback; }
    }

    private static int TryLoadIntSetting(string key, int fallback)
    {
        try
        {
            var v = Settings.Default[key];
            if (v is int i) return i;
            if (v is string s && int.TryParse(s, out var parsed)) return parsed;
        }
        catch (SettingsPropertyNotFoundException) { }
        catch { }
        return fallback;
    }

    private static void TrySaveSetting(string key, object value)
    {
        try
        {
            Settings.Default[key] = value;
        }
        catch (SettingsPropertyNotFoundException) { }
        catch { }
    }
}
