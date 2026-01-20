using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using LegendBorn.Mvvm;
using LegendBorn.Models;
using LegendBorn.Services;

namespace LegendBorn;

public sealed partial class MainViewModel : ObservableObject
{
    private const string SiteBaseUrl = "https://legendborn.ru";

    // fallback если нет данных сервера/конфига
    private const string DefaultServerIpFallback = "legendcraft.minerent.io";

    // ===== Services / core =====
    private readonly MinecraftService _mc;
    private readonly ServerListService _servers = new();
    private readonly SiteAuthService _site = new();
    private readonly TokenStore _tokenStore;

    private readonly string _gameDir;

    // game process state
    private Process? _runningProcess;
    private int _playGuard; // interlocked

    // guards
    private bool _commandsReady;

    // used to prevent side-effects during initial server load
    private bool _suppressSelectedServerSideEffects;

    // ===== Config save debounce =====
    private int _configSaveVersion;

    private void ScheduleConfigSave()
    {
        // debounce, чтобы не писать конфиг на каждый символ
        var v = Interlocked.Increment(ref _configSaveVersion);
        _ = Task.Run(async () =>
        {
            await Task.Delay(250).ConfigureAwait(false);
            if (v != _configSaveVersion) return;
            SaveConfigSafe();
        });
    }

    private void SaveConfigSafe()
    {
        try
        {
            App.Config.Save();
        }
        catch
        {
            // release-safe: не валим UI
        }
    }

    // ===== UI: Tabs (Auth / Start / Profile / Settings) =====
    private int _selectedMenuIndex;
    public int SelectedMenuIndex
    {
        get => _selectedMenuIndex;
        set
        {
            if (Set(ref _selectedMenuIndex, value))
            {
                Raise(nameof(IsAuthPage));
                Raise(nameof(IsStartPage));
                Raise(nameof(IsProfilePage));
                Raise(nameof(IsSettingsPage));
            }
        }
    }

    public bool IsAuthPage { get => SelectedMenuIndex == 0; set { if (value) SelectedMenuIndex = 0; } }
    public bool IsStartPage { get => SelectedMenuIndex == 1; set { if (value) SelectedMenuIndex = 1; } }
    public bool IsProfilePage { get => SelectedMenuIndex == 2; set { if (value) SelectedMenuIndex = 2; } }
    public bool IsSettingsPage { get => SelectedMenuIndex == 3; set { if (value) SelectedMenuIndex = 3; } }

    // ===== LAUNCHER VERSION =====
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

    // ===== Server model (UI) =====
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

            if (!_suppressSelectedServerSideEffects)
                OnSelectedServerChanged(value);

            // обновляем зависимые UI поля
            Raise(nameof(PackName));
            Raise(nameof(MinecraftVersion));
            Raise(nameof(LoaderName));
            Raise(nameof(LoaderVersion));
            Raise(nameof(BuildDisplayName));

            // сохраняем выбор сервера в конфиг
            try
            {
                if (value is not null)
                {
                    App.Config.Current.LastServerId = value.Id;
                    ScheduleConfigSave();
                }
            }
            catch { }

            RefreshCanStates();
        }
    }

    // ===== Pack display (UI) — зависит от сервера =====
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

    // ===== RAM =====
    public ObservableCollection<int> RamOptions { get; } = new()
    {
        2048, 3072, 4096, 6144, 8192, 12288, 16384
    };

    private int _ramMb = 4096;
    public int RamMb
    {
        get => _ramMb;
        set
        {
            var normalized = RamOptions.Contains(value) ? value : 4096;

            if (!Set(ref _ramMb, normalized))
                return;

            // config
            try
            {
                App.Config.Current.RamMb = _ramMb;
                ScheduleConfigSave();
            }
            catch { }

            RefreshCanStates();
        }
    }

    // ===== UI collections =====
    public ObservableCollection<string> Versions { get; } = new();
    public ObservableCollection<string> LogLines { get; } = new();

    // ===== Profile / auth UI =====
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

            if (url.StartsWith("/", StringComparison.Ordinal))
                return SiteBaseUrl + url;

            return SiteBaseUrl + "/" + url;
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
    public string Username
    {
        get => _username;
        set
        {
            var v = string.IsNullOrWhiteSpace(value) ? "Player" : value.Trim();

            if (!Set(ref _username, v))
                return;

            // config
            try
            {
                // если хочешь хранить ник в конфиге — добавь поле в LauncherConfig:
                // public string? Username {get;set;}
                // Тогда раскомментируй:
                // App.Config.Current.Username = _username;
                ScheduleConfigSave();
            }
            catch { }

            RefreshCanStates();
        }
    }

    private string _serverIp = DefaultServerIpFallback;
    public string ServerIp
    {
        get => _serverIp;
        set
        {
            var v = (value ?? "").Trim();

            if (!Set(ref _serverIp, v))
                return;

            // config
            try
            {
                App.Config.Current.LastServerIp = _serverIp;
                ScheduleConfigSave();
            }
            catch { }

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
            }
            else
            {
                if (SelectedMenuIndex != 3)
                    SelectedMenuIndex = 0;
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

    // ===== Login URL =====
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

    // ===== Derived flags =====
    public bool CanStop => _runningProcess is { HasExited: false };

    // _isClosing объявлен в другом partial? (оставляем как есть)
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

    // ===== Commands =====
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

    public MainViewModel()
    {
        SelectedMenuIndex = 0;

        // берём из конфига или дефолт
        string gameDir;
        try
        {
            gameDir = (App.Config.Current.GameRootPath ?? "").Trim();
            if (string.IsNullOrWhiteSpace(gameDir))
                gameDir = LauncherPaths.DefaultGameDir;
        }
        catch
        {
            gameDir = LauncherPaths.DefaultGameDir;
        }

        _gameDir = gameDir;
        try { Directory.CreateDirectory(_gameDir); } catch { }

        // токены — единые из App
        _tokenStore = App.Tokens;

        _mc = new MinecraftService(_gameDir);

        _mc.Log += (_, line) =>
        {
            if (_isClosing) return;
            SafeUi(() => AppendLog(line));
        };

        _mc.ProgressPercent += (_, p) =>
        {
            if (_isClosing) return;
            SafeUi(() => OnMinecraftProgress(p));
        };

        InitCommands();
        _commandsReady = true;

        // ===== Load Config (релиз-safe) =====
        try
        {
            // Username (если добавишь поле в LauncherConfig — подхватим)
            // var u = (App.Config.Current.Username ?? "Player").Trim();
            // _username = string.IsNullOrWhiteSpace(u) ? "Player" : u;
            _username = "Player";
            Raise(nameof(Username));

            var ip = (App.Config.Current.LastServerIp ?? DefaultServerIpFallback).Trim();
            _serverIp = string.IsNullOrWhiteSpace(ip) ? DefaultServerIpFallback : ip;
            Raise(nameof(ServerIp));

            var ram = App.Config.Current.RamMb;
            _ramMb = RamOptions.Contains(ram) ? ram : 4096;
            Raise(nameof(RamMb));
        }
        catch
        {
            // ignore
        }

        _ = InitializeAsyncSafe();
        RefreshCanStates();
    }

    private void InitCommands()
    {
        RefreshVersionsCommand = new AsyncRelayCommand(
            CheckPackAsync,
            () => !_isClosing && !IsBusy && !IsWaitingSiteConfirm && SelectedServer is not null);

        PlayCommand = new AsyncRelayCommand(PlayAsync, () => CanPlay);
        OpenGameDirCommand = new RelayCommand(OpenGameDir);
        StopGameCommand = new RelayCommand(StopGame, () => !_isClosing && CanStop);

        LoginViaSiteCommand = new AsyncRelayCommand(
            LoginViaSiteAsync,
            () => !_isClosing && !IsBusy && !IsLoggedIn && !IsWaitingSiteConfirm);

        SiteLogoutCommand = new RelayCommand(
            SiteLogout,
            () => !_isClosing && (IsLoggedIn || IsWaitingSiteConfirm));

        ClearLogCommand = new RelayCommand(() => LogLines.Clear());

        OpenSettingsCommand = new RelayCommand(() => SelectedMenuIndex = 3);
        OpenStartCommand = new RelayCommand(() => SelectedMenuIndex = IsLoggedIn ? 1 : 0);

        OpenProfileCommand = new RelayCommand(
            () =>
            {
                if (IsLoggedIn) SelectedMenuIndex = 2;
            },
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
    }

    private static void SafeUi(Action action)
    {
        try
        {
            var d = Application.Current?.Dispatcher;
            if (d is null || d.CheckAccess())
                action();
            else
                d.Invoke(action);
        }
        catch
        {
            // ignore
        }
    }
}
