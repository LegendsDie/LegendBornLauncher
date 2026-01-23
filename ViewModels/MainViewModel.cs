// File: ViewModels/MainViewModel.cs
using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LegendBorn.Launching;
using LegendBorn.Models;
using LegendBorn.Mvvm;
using LegendBorn.Services;

namespace LegendBorn.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    // API-база (логин/профиль/ивенты). Оставляем ru, чтобы не сломать существующие эндпоинты.
    internal const string SiteBaseUrl = "https://ru.legendborn.ru";

    // Публичный сайт (для ссылок/картинок).
    internal const string SitePublicUrlPrimary = "https://legendborn.ru";
    internal const string SitePublicUrlFallback = "https://ru.legendborn.ru";

    private const string DefaultServerIp = "legendcraft.minerent.io";

    private const int MenuMinIndex = 0;
    private const int MenuMaxIndex = 4; // 0..4 (включая News)

    // ✅ ТЗ: RAM 4..16 GB (берём из схемы конфига)
    private const int RamMinMb = LauncherConfig.RamMinMb;          // 4096
    private const int RamMaxHardCapMb = LauncherConfig.RamMaxMb;   // 16384

    private readonly ConfigService _config;
    private readonly LogService _log;

    private readonly MinecraftService _mc;
    private readonly ServerListService _servers = new();
    private readonly SiteAuthService _site = new();
    private readonly TokenStore _tokenStore;

    private readonly string _gameDir;

    private Process? _runningProcess;
    private int _playGuard;

    private bool _commandsReady;
    private bool _suppressSelectedServerSideEffects;

    private int _configSaveVersion;
    private readonly SemaphoreSlim _configSaveLock = new(1, 1);

    private long _totalSystemRamMb;
    private int _maxAllowedRamMb;
    private int _recommendedRamMb;

    private void ScheduleConfigSave()
    {
        if (_isClosing) return;

        var v = Interlocked.Increment(ref _configSaveVersion);

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(300, _lifetimeCts.Token).ConfigureAwait(false);
            }
            catch { /* ignore */ }

            if (_isClosing) return;
            if (v != _configSaveVersion) return;

            await SaveConfigSafeAsync().ConfigureAwait(false);
        });
    }

    private async Task SaveConfigSafeAsync()
    {
        if (_isClosing) return;

        try
        {
            await _configSaveLock.WaitAsync(_lifetimeCts.Token).ConfigureAwait(false);
        }
        catch
        {
            return;
        }

        try
        {
            if (_isClosing) return;

            try { _config.Current.Normalize(); } catch { /* ignore */ }

            try
            {
                _config.Save();
            }
            catch (Exception ex)
            {
                try { _log.Error("Config save failed", ex); } catch { /* ignore */ }
            }
        }
        finally
        {
            try { _configSaveLock.Release(); } catch { /* ignore */ }
        }
    }

    private static string ResolveGameDir(string raw)
        => LauncherPaths.NormalizePathOr(raw, LauncherPaths.DefaultGameDir);

    private int _selectedMenuIndex;
    public int SelectedMenuIndex
    {
        get => _selectedMenuIndex;
        set
        {
            var normalized = value;
            if (normalized < MenuMinIndex) normalized = MenuMinIndex;
            if (normalized > MenuMaxIndex) normalized = MenuMaxIndex;

            if (!Set(ref _selectedMenuIndex, normalized))
                return;

            try
            {
                _config.Current.LastMenuIndex = _selectedMenuIndex;
                ScheduleConfigSave();
            }
            catch { /* ignore */ }

            Raise(nameof(IsAuthPage));
            Raise(nameof(IsStartPage));
            Raise(nameof(IsProfilePage));
            Raise(nameof(IsSettingsPage));
            Raise(nameof(IsNewsPage));
        }
    }

    public bool IsAuthPage { get => SelectedMenuIndex == 0; set { if (value) SelectedMenuIndex = 0; } }
    public bool IsStartPage { get => SelectedMenuIndex == 1; set { if (value) SelectedMenuIndex = 1; } }
    public bool IsProfilePage { get => SelectedMenuIndex == 2; set { if (value) SelectedMenuIndex = 2; } }
    public bool IsSettingsPage { get => SelectedMenuIndex == 3; set { if (value) SelectedMenuIndex = 3; } }
    public bool IsNewsPage { get => SelectedMenuIndex == 4; set { if (value) SelectedMenuIndex = 4; } }

    public string LauncherVersion
    {
        get
        {
            try
            {
                var v = LauncherIdentity.InformationalVersion;
                if (string.IsNullOrWhiteSpace(v)) return "v?";
                return "v" + v.Split('+')[0].Trim();
            }
            catch
            {
                return "v?";
            }
        }
    }

    public sealed class ServerEntry
    {
        public string Id { get; init; } = "";
        public string Name { get; init; } = "";
        public string Address { get; init; } = "";
        public string MinecraftVersion { get; init; } = "1.21.1";

        public string LoaderName { get; init; } = "vanilla";
        public string LoaderVersion { get; init; } = "";
        public string LoaderInstallerUrl { get; init; } = "";

        public string ClientVersionId { get; init; } = "";
        public string PackBaseUrl { get; init; } = "https://legendborn.ru/launcher/pack/";
        public string[] PackMirrors { get; init; } = Array.Empty<string>();
        public bool SyncPack { get; init; } = true;

        public override string ToString() => Name;
    }

    public ObservableCollection<ServerEntry> Servers { get; } = new();

    private ServerEntry? _selectedServer;
    public ServerEntry? SelectedServer
    {
        get => _selectedServer;
        set
        {
            if (!Set(ref _selectedServer, value))
                return;

            if (_suppressSelectedServerSideEffects)
            {
                RaisePackPresentation();
                RefreshCanStates();
                return;
            }

            OnSelectedServerChanged(value);
        }
    }

    public string PackName => SelectedServer?.Name ?? "LegendCraft";
    public string MinecraftVersion => SelectedServer?.MinecraftVersion ?? "1.21.1";
    public string LoaderName => FormatLoaderName(SelectedServer?.LoaderName);
    public string LoaderVersion => SelectedServer?.LoaderVersion ?? "";

    public string BuildDisplayName
    {
        get
        {
            var mc = MinecraftVersion;
            var ln = LoaderName;
            var lv = (SelectedServer?.LoaderVersion ?? "").Trim();

            if (string.IsNullOrWhiteSpace(lv) || ln.Equals("Vanilla", StringComparison.OrdinalIgnoreCase))
                return $"{PackName} • {ln} {mc}";

            return $"{PackName} • {ln} {lv} ({mc})";
        }
    }

    public ObservableCollection<int> RamOptions { get; } = new();

    public long TotalSystemRamMb => _totalSystemRamMb;
    public int RecommendedRamMb => _recommendedRamMb;
    public int MaxAllowedRamMb => _maxAllowedRamMb;

    private int _ramMb;
    public int RamMb
    {
        get => _ramMb;
        set
        {
            var normalized = NormalizeRamMb(value);

            if (!Set(ref _ramMb, normalized))
                return;

            EnsureRamOptionExists(normalized);
            Raise(nameof(RamMbText));

            try
            {
                _config.Current.RamMb = _ramMb;
                ScheduleConfigSave();
            }
            catch { /* ignore */ }

            RefreshCanStates();
        }
    }

    public string RamMbText
    {
        get => RamMb.ToString();
        set
        {
            var parsed = ParseDigitsToInt(value);
            if (parsed is null)
                return;

            RamMb = parsed.Value;
        }
    }

    public ObservableCollection<string> Versions { get; } = new();
    public ObservableCollection<string> LogLines { get; } = new();

    private UserProfile? _profile;
    public UserProfile? Profile
    {
        get => _profile;
        set
        {
            if (Set(ref _profile, value))
            {
                UpdateRezoniteFromProfile();
                Raise(nameof(AvatarUrl));
                Raise(nameof(UserInitial));
                RefreshCanStates();
            }
        }
    }

    private long _rezonite;
    public long Rezonite
    {
        get => _rezonite;
        set => Set(ref _rezonite, value);
    }

    public string? AvatarUrl
    {
        get
        {
            var url = (Profile?.AvatarUrl ?? "").Trim();
            if (string.IsNullOrWhiteSpace(url))
                return null;

            if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return url;

            if (url.StartsWith("//", StringComparison.Ordinal))
                return "https:" + url;

            var primary = string.IsNullOrWhiteSpace(SitePublicUrlPrimary) ? SitePublicUrlFallback : SitePublicUrlPrimary;

            if (url.StartsWith("/", StringComparison.Ordinal))
                return primary + url;

            return primary + "/" + url;
        }
    }

    public string UserInitial
    {
        get
        {
            var name = SiteUserName;
            if (string.IsNullOrWhiteSpace(name) || name == "Не вошли") return "?";
            return name.Trim().Substring(0, 1).ToUpperInvariant();
        }
    }

    private string? _selectedVersion;
    public string? SelectedVersion
    {
        get => _selectedVersion;
        set
        {
            if (Set(ref _selectedVersion, value))
                RefreshCanStates();
        }
    }

    private string _username = "Player";

    /// <summary>
    /// ✅ Важно: защита от перетирания ника сайтом.
    /// Если конфиг уже содержит нормальный ник (не "Player"),
    /// то попытки выставить ник, совпадающий с профилем сайта, игнорируются.
    /// </summary>
    public string Username
    {
        get => _username;
        set
        {
            var raw = string.IsNullOrWhiteSpace(value) ? "Player" : value.Trim();
            var v = MakeValidMcName(raw);

            var siteSuggested = GetSiteSuggestedMcName();

            string savedCfg = "";
            try { savedCfg = (_config.Current.LastUsername ?? "").Trim(); } catch { /* ignore */ }

            var savedCfgValid = string.IsNullOrWhiteSpace(savedCfg) ? "" : MakeValidMcName(savedCfg);

            var hasUserNickInConfig =
                !string.IsNullOrWhiteSpace(savedCfgValid) &&
                !savedCfgValid.Equals("Player", StringComparison.OrdinalIgnoreCase);

            var isSitePush =
                !string.IsNullOrWhiteSpace(siteSuggested) &&
                v.Equals(siteSuggested, StringComparison.OrdinalIgnoreCase);

            if (isSitePush && hasUserNickInConfig && !savedCfgValid.Equals(siteSuggested, StringComparison.OrdinalIgnoreCase))
            {
                if (_username.Equals(savedCfgValid, StringComparison.OrdinalIgnoreCase))
                    return;

                if (!Set(ref _username, savedCfgValid))
                    return;

                Raise(nameof(UserInitial));
                RefreshCanStates();
                return;
            }

            if (!Set(ref _username, v))
                return;

            try
            {
                _config.Current.LastUsername = _username;
                ScheduleConfigSave();
            }
            catch { /* ignore */ }

            Raise(nameof(UserInitial));
            RefreshCanStates();
        }
    }

    private string? GetSiteSuggestedMcName()
    {
        try
        {
            if (!IsLoggedIn || Profile is null)
                return null;

            var candidate =
                !string.IsNullOrWhiteSpace(Profile.MinecraftName) ? Profile.MinecraftName :
                !string.IsNullOrWhiteSpace(SiteUserName) && SiteUserName != "Не вошли" ? SiteUserName :
                !string.IsNullOrWhiteSpace(Profile.UserName) ? Profile.UserName :
                null;

            if (string.IsNullOrWhiteSpace(candidate))
                return null;

            return MakeValidMcName(candidate);
        }
        catch
        {
            return null;
        }
    }

    private string _serverIp = DefaultServerIp;
    public string ServerIp
    {
        get => _serverIp;
        set
        {
            var v = (value ?? "").Trim();
            if (!Set(ref _serverIp, v))
                return;

            try
            {
                _config.Current.LastServerIp = _serverIp;
                ScheduleConfigSave();
            }
            catch { /* ignore */ }

            RefreshCanStates();
        }
    }

    private bool _isLoggedIn;
    public bool IsLoggedIn
    {
        get => _isLoggedIn;
        set
        {
            if (!Set(ref _isLoggedIn, value))
                return;

            if (_isLoggedIn)
            {
                if (SelectedMenuIndex == 0) SelectedMenuIndex = 1;

                // ✅ после успешного входа — подтягиваем друзей/заявки (реализация в partial Social)
                ScheduleSocialRefresh();
            }
            else
            {
                if (SelectedMenuIndex != 3 && SelectedMenuIndex != 4)
                    SelectedMenuIndex = 0;

                // реализация в partial Social
                ClearSocialUi();
            }

            Raise(nameof(UserInitial));
            Raise(nameof(AvatarUrl));
            RefreshCanStates();
        }
    }

    private bool _isWaitingSiteConfirm;
    public bool IsWaitingSiteConfirm
    {
        get => _isWaitingSiteConfirm;
        set
        {
            if (Set(ref _isWaitingSiteConfirm, value))
            {
                Raise(nameof(LoginButtonText));
                RefreshCanStates();
            }
        }
    }

    private string _siteUserName = "Не вошли";
    public string SiteUserName
    {
        get => _siteUserName;
        set
        {
            if (Set(ref _siteUserName, value))
            {
                Raise(nameof(UserInitial));
                RefreshCanStates();
            }
        }
    }

    private string _statusText = "Готово.";
    public string StatusText
    {
        get => _statusText;
        set => Set(ref _statusText, value);
    }

    private int _progressPercent;
    public int ProgressPercent
    {
        get => _progressPercent;
        set => Set(ref _progressPercent, value);
    }

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (Set(ref _isBusy, value))
            {
                Raise(nameof(PlayButtonText));
                RefreshCanStates();
            }
        }
    }

    private string? _loginUrl;
    public string? LoginUrl
    {
        get => _loginUrl;
        set
        {
            if (Set(ref _loginUrl, value))
                RefreshCanStates();
        }
    }

    public bool HasLoginUrl => !string.IsNullOrWhiteSpace(LoginUrl);

    public bool CanStop => _runningProcess is { HasExited: false };

    public bool CanPlay =>
        !_isClosing &&
        !IsBusy &&
        !IsWaitingSiteConfirm &&
        IsLoggedIn &&
        Profile is not null &&
        Profile.CanPlay &&
        SelectedServer is not null &&
        IsValidMcName(Username);

    public string PlayButtonText => IsBusy ? "..." : "Играть";
    public string LoginButtonText => IsWaitingSiteConfirm ? "Ожидание..." : "Авторизация";

    public AsyncRelayCommand RefreshVersionsCommand { get; private set; } = null!;
    public AsyncRelayCommand PlayCommand { get; private set; } = null!;
    public RelayCommand OpenGameDirCommand { get; private set; } = null!;
    public RelayCommand StopGameCommand { get; private set; } = null!;

    public AsyncRelayCommand LoginViaSiteCommand { get; private set; } = null!;
    public RelayCommand SiteLogoutCommand { get; private set; } = null!;
    public RelayCommand ClearLogCommand { get; private set; } = null!;

    public RelayCommand OpenSettingsCommand { get; private set; } = null!;
    public RelayCommand OpenStartCommand { get; private set; } = null!;
    public RelayCommand OpenProfileCommand { get; private set; } = null!;

    public RelayCommand OpenLoginUrlCommand { get; private set; } = null!;
    public RelayCommand CopyLoginUrlCommand { get; private set; } = null!;

    public AsyncRelayCommand CheckLauncherUpdatesCommand { get; private set; } = null!;

    // Social команды/свойства определены в partial Social:
    // RefreshFriendsCommand, SendFriendRequestCommand, AcceptFriendRequestCommand, DeclineFriendRequestCommand, RemoveFriendCommand

    private static ConfigService? _fallbackConfig;
    private static TokenStore? _fallbackTokens;

    public MainViewModel()
    {
        _selectedMenuIndex = 0;

        _log = SafeGetAppLog();
        _config = SafeGetAppConfig();
        _tokenStore = SafeGetAppTokens();

        InitializeRamModel();

        _gameDir = ResolveGameDir(_config.Current.GameRootPath ?? "");
        try { Directory.CreateDirectory(_gameDir); } catch { /* ignore */ }

        _mc = new MinecraftService(_gameDir);

        _mc.Log += (_, line) =>
        {
            if (_isClosing) return;
            AppendLog(line);
        };

        _mc.ProgressPercent += (_, p) =>
        {
            if (_isClosing) return;
            OnMinecraftProgress(p);
        };

        InitCommands();
        _commandsReady = true;

        try
        {
            var ip = (_config.Current.LastServerIp ?? DefaultServerIp).Trim();
            _serverIp = string.IsNullOrWhiteSpace(ip) ? DefaultServerIp : ip;
            Raise(nameof(ServerIp));

            var ramFromCfg = _config.Current.RamMb;
            var initialRam = ramFromCfg > 0 ? ramFromCfg : _recommendedRamMb;
            _ramMb = NormalizeRamMb(initialRam);
            EnsureRamOptionExists(_ramMb);
            Raise(nameof(RamMb));
            Raise(nameof(RamMbText));

            var u = (_config.Current.LastUsername ?? "Player").Trim();
            _username = string.IsNullOrWhiteSpace(u) ? "Player" : MakeValidMcName(u);
            Raise(nameof(Username));

            var menu = _config.Current.LastMenuIndex;
            if (menu < MenuMinIndex || menu > MenuMaxIndex) menu = 0;
            _selectedMenuIndex = menu;
            Raise(nameof(SelectedMenuIndex));
            Raise(nameof(IsAuthPage));
            Raise(nameof(IsStartPage));
            Raise(nameof(IsProfilePage));
            Raise(nameof(IsSettingsPage));
            Raise(nameof(IsNewsPage));

            _config.Current.LastLauncherStartUtc = DateTimeOffset.UtcNow;
        }
        catch { /* ignore */ }

        ScheduleConfigSave();

        _ = InitializeAsyncSafe();
        RefreshCanStates();
    }

    private void InitializeRamModel()
    {
        _totalSystemRamMb = GetTotalPhysicalMemoryMb();

        _maxAllowedRamMb = Math.Clamp(ComputeMaxAllowedRamMb(_totalSystemRamMb), RamMinMb, RamMaxHardCapMb);
        _recommendedRamMb = Math.Clamp(ComputeRecommendedRamMb(_totalSystemRamMb, _maxAllowedRamMb), RamMinMb, _maxAllowedRamMb);

        BuildRamOptions(_maxAllowedRamMb, _recommendedRamMb);
    }

    private void BuildRamOptions(int maxAllowedMb, int recommendedMb)
    {
        RamOptions.Clear();

        // UI: показываем стандартные ступени 4..16 GB
        var steps = new[] { 4096, 6144, 8192, 10240, 12288, 16384 };
        foreach (var s in steps)
            RamOptions.Add(s);

        // рекомендация — безопасная под текущий ПК
        var safeMax = Math.Clamp(maxAllowedMb, RamMinMb, RamMaxHardCapMb);
        recommendedMb = Math.Clamp(recommendedMb, RamMinMb, safeMax);

        EnsureRamOptionExists(recommendedMb);
    }

    private void EnsureRamOptionExists(int value)
    {
        value = Math.Clamp(value, RamMinMb, RamMaxHardCapMb);

        if (RamOptions.Contains(value))
            return;

        RamOptions.Add(value);

        var ordered = RamOptions.Distinct().OrderBy(x => x).ToArray();
        RamOptions.Clear();
        foreach (var v in ordered) RamOptions.Add(v);
    }

    private int NormalizeRamMb(int requestedMb)
    {
        var max = _maxAllowedRamMb > 0
            ? _maxAllowedRamMb
            : Math.Clamp(ComputeMaxAllowedRamMb(GetTotalPhysicalMemoryMb()), RamMinMb, RamMaxHardCapMb);

        max = Math.Clamp(max, RamMinMb, RamMaxHardCapMb);

        if (requestedMb <= 0)
            requestedMb = _recommendedRamMb > 0 ? _recommendedRamMb : RamMinMb;

        requestedMb = Math.Clamp(requestedMb, RamMinMb, max);

        // округление до 256МБ
        requestedMb = (requestedMb / 256) * 256;
        if (requestedMb < RamMinMb) requestedMb = RamMinMb;

        return requestedMb;
    }

    private static int? ParseDigitsToInt(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;

        var digits = new string(s.Where(char.IsDigit).ToArray());
        if (digits.Length == 0) return null;

        if (!int.TryParse(digits, out var v)) return null;
        return v;
    }

    private void InitCommands()
    {
        RefreshVersionsCommand = new AsyncRelayCommand(
            CheckPackAsync,
            () => !_isClosing && !IsBusy && !IsWaitingSiteConfirm && SelectedServer is not null);

        PlayCommand = new AsyncRelayCommand(PlayAsync, () => CanPlay);
        OpenGameDirCommand = new RelayCommand(OpenGameDir, () => !_isClosing);
        StopGameCommand = new RelayCommand(StopGame, () => !_isClosing && CanStop);

        LoginViaSiteCommand = new AsyncRelayCommand(
            LoginViaSiteAsync,
            () => !_isClosing && !IsBusy && !IsLoggedIn && !IsWaitingSiteConfirm);

        SiteLogoutCommand = new RelayCommand(
            SiteLogout,
            () => !_isClosing && (IsLoggedIn || IsWaitingSiteConfirm));

        ClearLogCommand = new RelayCommand(() => PostToUi(() => LogLines.Clear()), () => !_isClosing);

        OpenSettingsCommand = new RelayCommand(() => SelectedMenuIndex = 3, () => !_isClosing);
        OpenStartCommand = new RelayCommand(() => SelectedMenuIndex = IsLoggedIn ? 1 : 0, () => !_isClosing);

        OpenProfileCommand = new RelayCommand(
            () => { if (IsLoggedIn) SelectedMenuIndex = 2; },
            () => !_isClosing && IsLoggedIn);

        OpenLoginUrlCommand = new RelayCommand(OpenLoginUrl, () => !_isClosing && HasLoginUrl);
        CopyLoginUrlCommand = new RelayCommand(CopyLoginUrl, () => !_isClosing && HasLoginUrl);

        CheckLauncherUpdatesCommand = new AsyncRelayCommand(
            async () =>
            {
                if (_isClosing) return;
                await UpdateService.CheckAndUpdateAsync(silent: false, showNoUpdates: true);
            },
            () => !_isClosing && !IsBusy);

        // Social/Friends команды — в partial Social
        InitSocialCommands();
    }

    private static LogService SafeGetAppLog()
    {
        try { return App.Log ?? LogService.Noop; }
        catch { return LogService.Noop; }
    }

    private static ConfigService SafeGetAppConfig()
    {
        try
        {
            if (App.Config is not null) return App.Config;
        }
        catch { /* ignore */ }

        _fallbackConfig ??= new ConfigService(LauncherPaths.ConfigFile);
        try { _fallbackConfig.LoadOrCreate(); } catch { /* ignore */ }
        return _fallbackConfig;
    }

    private static TokenStore SafeGetAppTokens()
    {
        try
        {
            if (App.Tokens is not null) return App.Tokens;
        }
        catch { /* ignore */ }

        _fallbackTokens ??= new TokenStore(LauncherPaths.TokenFile);
        return _fallbackTokens;
    }
}
