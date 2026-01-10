using System;
using System.Collections.ObjectModel;
using System.Configuration;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using LegendBorn.Mvvm;
using LegendBorn.Models;
using LegendBorn.Properties;
using LegendBorn.Services;

namespace LegendBorn;

public sealed class MainViewModel : ObservableObject
{
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
                var asm = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();

                var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
                if (!string.IsNullOrWhiteSpace(info))
                    return "v" + info.Split('+')[0];

                var v = asm.GetName().Version;
                if (v is null) return "v?";
                return $"v{v.Major}.{v.Minor}.{v.Build}";
            }
            catch
            {
                return "v?";
            }
        }
    }

    // ===== Pack display (UI) — зависит от сервера =====
    public string PackName => SelectedServer?.Name ?? "LegendBorn";
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

    // ===== Server model (UI) =====
    public sealed class ServerEntry
    {
        public string Id { get; init; } = "";
        public string Name { get; init; } = "";
        public string Address { get; init; } = "";
        public string MinecraftVersion { get; init; } = "1.21.1";

        // Тип: vanilla / neoforge / forge / fabric...
        public string LoaderName { get; init; } = "vanilla";
        public string LoaderVersion { get; init; } = "";

        // installerUrl обязателен для neoforge/forge
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
            if (Set(ref _selectedServer, value))
            {
                if (value is not null)
                {
                    ServerIp = value.Address;

                    var label = MakeAutoVersionLabel(value);
                    SetVersionsUi(label);

                    TrySaveSetting("SelectedServerId", value.Id);
                    Settings.Default.Save();

                    Raise(nameof(PackName));
                    Raise(nameof(MinecraftVersion));
                    Raise(nameof(LoaderName));
                    Raise(nameof(LoaderVersion));
                    Raise(nameof(BuildDisplayName));
                }

                RefreshCanStates();
            }
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
            if (Set(ref _ramMb, value))
                RefreshCanStates();
        }
    }

    // ===== Core =====
    private readonly MinecraftService _mc;
    private readonly ServerListService _servers = new();

    // ===== Site auth =====
    private readonly SiteAuthService _site = new();
    private readonly TokenStore _tokenStore;
    private AuthTokens? _tokens;
    private CancellationTokenSource? _loginCts;

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

    private const string SiteBaseUrl = "https://legendborn.ru";

    public string? AvatarUrl
    {
        get
        {
            var url = Profile?.AvatarUrl;
            if (string.IsNullOrWhiteSpace(url))
                return null;

            url = url.Trim();

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
            if (Set(ref _username, value))
            {
                RefreshCanStates();
            }
        }
    }

    private string _serverIp = "legendcraft.minerent.io";
    public string ServerIp
    {
        get => _serverIp;
        set => Set(ref _serverIp, value);
    }

    private bool _isLoggedIn;
    public bool IsLoggedIn
    {
        get => _isLoggedIn;
        set
        {
            if (Set(ref _isLoggedIn, value))
            {
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

    private Process? _runningProcess;
    public bool CanStop => _runningProcess is { HasExited: false };

    public bool CanPlay =>
        !IsBusy &&
        !IsWaitingSiteConfirm &&
        IsLoggedIn &&
        Profile is not null &&
        Profile.CanPlay &&
        SelectedServer is not null &&
        IsValidMcName(Username);

    public string PlayButtonText => IsBusy ? "..." : "Играть";
    public string LoginButtonText => IsWaitingSiteConfirm ? "Ожидание..." : "Войти через сайт";

    // ===== Commands =====
    public AsyncRelayCommand RefreshVersionsCommand { get; private set; } = null!; // фактически: "Проверить сборку"
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

    private readonly string _gameDir;
    private bool _commandsReady;

    private int _playGuard;

    public MainViewModel()
    {
        SelectedMenuIndex = 0;

        _gameDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "LegendBorn", "Game");
        Directory.CreateDirectory(_gameDir);

        _tokenStore = new TokenStore(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "LegendBorn", "auth.dat"));

        _mc = new MinecraftService(_gameDir);
        _mc.Log += (_, line) => AppendLog(line);

        _mc.ProgressPercent += (_, p) =>
            App.Current?.Dispatcher.Invoke((Action)(() => ProgressPercent = p));

        // ✅ "Проверить сборку" — реально синхронизирует pack по manifest
        RefreshVersionsCommand = new AsyncRelayCommand(CheckPackAsync, () => !IsBusy && !IsWaitingSiteConfirm && SelectedServer is not null);

        PlayCommand = new AsyncRelayCommand(PlayAsync, () => CanPlay);
        OpenGameDirCommand = new RelayCommand(OpenGameDir);
        StopGameCommand = new RelayCommand(StopGame, () => CanStop);

        LoginViaSiteCommand = new AsyncRelayCommand(LoginViaSiteAsync, () => !IsBusy && !IsLoggedIn && !IsWaitingSiteConfirm);
        SiteLogoutCommand = new RelayCommand(SiteLogout, () => IsLoggedIn || IsWaitingSiteConfirm);

        ClearLogCommand = new RelayCommand(() => LogLines.Clear());

        OpenSettingsCommand = new RelayCommand(() => SelectedMenuIndex = 3);
        OpenStartCommand = new RelayCommand(() => SelectedMenuIndex = IsLoggedIn ? 1 : 0);

        OpenProfileCommand = new RelayCommand(() =>
        {
            if (IsLoggedIn) SelectedMenuIndex = 2;
        }, () => IsLoggedIn);

        OpenLoginUrlCommand = new RelayCommand(OpenLoginUrl, () => HasLoginUrl);
        CopyLoginUrlCommand = new RelayCommand(CopyLoginUrl, () => HasLoginUrl);

        CheckLauncherUpdatesCommand = new AsyncRelayCommand(
            async () => await UpdateService.CheckAndUpdateAsync(silent: false, showNoUpdates: true),
            () => !IsBusy);

        _commandsReady = true;

        Username = TryLoadStringSetting("Username", "Player") ?? "Player";
        RamMb = TryLoadIntSetting("RamMb", 4096);
        if (!RamOptions.Contains(RamMb))
            RamMb = 4096;

        _ = InitializeAsync();
        RefreshCanStates();
    }

    private async Task InitializeAsync()
    {
        try
        {
            await LoadServersAsync();
            if (SelectedServer is not null)
                SetVersionsUi(MakeAutoVersionLabel(SelectedServer));

            await TryAutoLoginAsync();
        }
        catch (Exception ex)
        {
            AppendLog(ex.ToString());
        }
    }

    // =========================
    // Servers.json
    // =========================

    private async Task LoadServersAsync()
    {
        try
        {
            AppendLog("Серверы: загрузка списка...");

            // ✅ 0.1.8: пробуем несколько зеркал списка серверов (если в будущем ты зальёшь servers.json на SourceForge)
            var list = await _servers.GetServersOrDefaultAsync(
                mirrors: ServerListService.DefaultServersMirrors,
                ct: CancellationToken.None);

            int count = 0;

            App.Current?.Dispatcher.Invoke((Action)(() =>
            {
                Servers.Clear();

                foreach (var s in list)
                {
                    var loaderType = (s.Loader?.Type ?? s.LoaderName ?? "vanilla").Trim().ToLowerInvariant();
                    var loaderVer = (s.Loader?.Version ?? s.LoaderVersion ?? "").Trim();
                    var installerUrl = (s.Loader?.InstallerUrl ?? "").Trim();

                    Servers.Add(new ServerEntry
                    {
                        Id = s.Id,
                        Name = s.Name,
                        Address = s.Address,
                        MinecraftVersion = s.MinecraftVersion,

                        LoaderName = loaderType,
                        LoaderVersion = loaderVer,
                        LoaderInstallerUrl = installerUrl,

                        PackBaseUrl = EnsureSlash(s.PackBaseUrl),
                        PackMirrors = s.PackMirrors ?? Array.Empty<string>(),
                        SyncPack = s.SyncPack
                    });
                }

                var savedId = TryLoadStringSetting("SelectedServerId", null);
                SelectedServer =
                    Servers.FirstOrDefault(x => x.Id.Equals(savedId ?? "", StringComparison.OrdinalIgnoreCase)) ??
                    Servers.FirstOrDefault();

                if (SelectedServer is not null)
                    ServerIp = SelectedServer.Address;

                count = Servers.Count;
            }));

            AppendLog($"Серверы: загружено {count} шт.");
        }
        catch (Exception ex)
        {
            AppendLog("Серверы: ошибка загрузки.");
            AppendLog(ex.Message);
        }
        finally
        {
            Raise(nameof(PackName));
            Raise(nameof(MinecraftVersion));
            Raise(nameof(LoaderName));
            Raise(nameof(LoaderVersion));
            Raise(nameof(BuildDisplayName));
            RefreshCanStates();
        }
    }

    private static string EnsureSlash(string url)
    {
        url = (url ?? "").Trim();
        if (!url.EndsWith("/")) url += "/";
        return url;
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

    // =========================
    // 0.1.8: Mirror helper (SourceForge -> остальные -> legendborn)
    // =========================

    private static readonly string[] SourceForgePackMirrors =
    {
        "https://master.dl.sourceforge.net/project/legendborn-pack/launcher/pack/",
        "https://downloads.sourceforge.net/project/legendborn-pack/launcher/pack/"
    };

    private static bool IsLegendbornHost(string? url)
        => !string.IsNullOrWhiteSpace(url) &&
           url.Contains("legendborn.ru", StringComparison.OrdinalIgnoreCase);

    private static string[] BuildPackMirrors(ServerEntry s)
    {
        var baseUrl = EnsureSlash(s.PackBaseUrl);

        var extra = (s.PackMirrors ?? Array.Empty<string>())
            .Select(EnsureSlash)
            .Where(u => !string.IsNullOrWhiteSpace(u))
            .ToArray();

        // Собираем кандидатов
        var all = extra
            .Concat(new[] { baseUrl })
            .Where(u => !string.IsNullOrWhiteSpace(u))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Если основной/кто-то из зеркал legendborn.ru — добавляем SourceForge как страховку
        if (all.Any(IsLegendbornHost) && !all.Any(u => u.Contains("sourceforge.net", StringComparison.OrdinalIgnoreCase)))
        {
            all.InsertRange(0, SourceForgePackMirrors.Select(EnsureSlash));
        }

        // 0.1.8: порядок приоритета (важно для России)
        // 1) master.dl.sourceforge
        // 2) остальные sourceforge
        // 3) любые не-legendborn
        // 4) legendborn в конец
        var ordered = all
            .OrderBy(u =>
            {
                var lu = u.ToLowerInvariant();
                if (lu.Contains("master.dl.sourceforge.net")) return 0;
                if (lu.Contains("sourceforge.net")) return 1;
                if (!lu.Contains("legendborn.ru")) return 2;
                return 3;
            })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return ordered;
    }

    // =========================
    // Проверка сборки (pack sync)
    // =========================

    private async Task CheckPackAsync()
    {
        if (SelectedServer is null)
        {
            StatusText = "Сервер не выбран.";
            return;
        }

        try
        {
            IsBusy = true;
            StatusText = "Проверка обновлений сборки...";
            ProgressPercent = 0;

            var mirrors = BuildPackMirrors(SelectedServer);

            if (SelectedServer.SyncPack)
                await _mc.SyncPackAsync(mirrors, CancellationToken.None);

            StatusText = "Сборка актуальна.";
        }
        catch (Exception ex)
        {
            StatusText = "Ошибка проверки сборки.";
            AppendLog(ex.ToString());
        }
        finally
        {
            IsBusy = false;
            RefreshCanStates();
        }
    }

    // =========================
    // Auth
    // =========================

    private void CancelLoginWait()
    {
        var cts = _loginCts;
        _loginCts = null;

        if (cts is null) return;

        try { cts.Cancel(); } catch { }
        try { cts.Dispose(); } catch { }
    }

    private static bool IsExpired(AuthTokens t)
    {
        if (t.ExpiresAtUnix <= 0) return false;

        try
        {
            var exp = DateTimeOffset.FromUnixTimeSeconds(t.ExpiresAtUnix).AddSeconds(-30);
            return DateTimeOffset.UtcNow >= exp;
        }
        catch
        {
            return false;
        }
    }

    private async Task TrySendDailyLauncherLoginEventAsync()
    {
        try
        {
            if (_tokens is null || string.IsNullOrWhiteSpace(_tokens.AccessToken))
                return;

            var key = "launcher_login";
            var idem = $"launcher_login:{DateTime.UtcNow:yyyy-MM-dd}";

            await _site.SendLauncherEventAsync(
                _tokens.AccessToken,
                key,
                idem,
                payload: new { client = "LegendBornLauncher", v = "1" },
                ct: CancellationToken.None);
        }
        catch { }
    }

    private async Task TryAutoLoginAsync()
    {
        var saved = _tokenStore.Load();
        if (saved is null || string.IsNullOrWhiteSpace(saved.AccessToken))
            return;

        if (IsExpired(saved))
        {
            _tokenStore.Clear();
            return;
        }

        try
        {
            IsBusy = true;
            StatusText = "Проверка входа на сайте...";

            _tokens = saved;

            var me = await _site.GetMeAsync(_tokens.AccessToken, CancellationToken.None);
            Profile = me;

            SiteUserName = me.UserName;
            IsLoggedIn = true;

            var mcName = string.IsNullOrWhiteSpace(me.MinecraftName) ? me.UserName : me.MinecraftName;
            Username = MakeValidMcName(mcName);

            await TrySendDailyLauncherLoginEventAsync();

            if (!me.CanPlay)
            {
                StatusText = string.IsNullOrWhiteSpace(me.Reason) ? "Доступ к игре ограничен." : me.Reason!;
                AppendLog(StatusText);
            }
            else
            {
                StatusText = "Вход выполнен.";
                AppendLog($"Сайт: вошли как {SiteUserName}");
            }
        }
        catch
        {
            _tokens = null;
            _tokenStore.Clear();

            Profile = null;
            Rezonite = 0;

            IsLoggedIn = false;
            SiteUserName = "Не вошли";

            StatusText = "Требуется вход.";
        }
        finally
        {
            IsBusy = false;

            if (string.Equals(StatusText, "Проверка входа на сайте...", StringComparison.Ordinal))
                StatusText = "Готово.";

            RefreshCanStates();
        }
    }

    private async Task LoginViaSiteAsync()
    {
        CancelLoginWait();
        _loginCts = new CancellationTokenSource();

        try
        {
            IsWaitingSiteConfirm = true;
            LoginUrl = null;

            StatusText = "Запрос входа...";
            ProgressPercent = 0;

            IsBusy = true;
            var (deviceId, connectUrl, expiresAtUnix) = await _site.StartLauncherLoginAsync(_loginCts.Token);
            IsBusy = false;

            var path = string.IsNullOrWhiteSpace(connectUrl) ? "/launcher/connect" : connectUrl;

            var fullUrl = path.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? path
                : SiteBaseUrl + path;

            if (!fullUrl.Contains("deviceId=", StringComparison.OrdinalIgnoreCase) &&
                !fullUrl.Contains("deviceid=", StringComparison.OrdinalIgnoreCase))
            {
                fullUrl += (fullUrl.Contains("?") ? "&" : "?") + "deviceId=" + Uri.EscapeDataString(deviceId);
            }

            LoginUrl = fullUrl;
            AppendLog($"Ссылка для входа: {fullUrl}");

            if (!TryOpenUrlInBrowser(fullUrl, out var openError))
            {
                AppendLog(openError);
                StatusText = "Если сайт не открылся — нажми «Открыть принудительно» или «Скопировать ссылку».";
            }
            else
            {
                StatusText = "Открой сайт и нажми «В путь». Если не открылся — используй кнопки ниже.";
            }

            var hardDeadline = expiresAtUnix > 0
                ? DateTimeOffset.FromUnixTimeSeconds(expiresAtUnix)
                : DateTimeOffset.UtcNow.AddMinutes(10);

            while (!_loginCts.IsCancellationRequested)
            {
                if (DateTimeOffset.UtcNow > hardDeadline)
                {
                    AppendLog("Время ожидания подтверждения истекло.");
                    StatusText = "Не подтверждено. Попробуй снова.";
                    return;
                }

                await Task.Delay(1200, _loginCts.Token);

                var tokens = await _site.PollLauncherLoginAsync(deviceId, _loginCts.Token);
                if (tokens is null)
                    continue;

                _tokens = tokens;
                _tokenStore.Save(tokens);

                var me = await _site.GetMeAsync(tokens.AccessToken, _loginCts.Token);
                Profile = me;

                SiteUserName = me.UserName;
                IsLoggedIn = true;

                var mcName = string.IsNullOrWhiteSpace(me.MinecraftName) ? me.UserName : me.MinecraftName;
                Username = MakeValidMcName(mcName);

                await TrySendDailyLauncherLoginEventAsync();

                if (!me.CanPlay)
                {
                    StatusText = string.IsNullOrWhiteSpace(me.Reason) ? "Доступ к игре ограничен." : me.Reason!;
                    AppendLog(StatusText);
                }
                else
                {
                    StatusText = "Вход выполнен.";
                    AppendLog($"Сайт: вошли как {SiteUserName}");
                }

                return;
            }
        }
        catch (TaskCanceledException)
        {
            AppendLog("Ожидание входа отменено.");
            StatusText = "Вход отменён.";
        }
        catch (Exception ex)
        {
            AppendLog(ex.ToString());
            StatusText = "Ошибка входа.";
        }
        finally
        {
            IsBusy = false;
            IsWaitingSiteConfirm = false;
            LoginUrl = null;

            CancelLoginWait();
            RefreshCanStates();
        }
    }

    private void OpenLoginUrl()
    {
        var url = LoginUrl;
        if (string.IsNullOrWhiteSpace(url))
            return;

        if (!TryOpenUrlInBrowser(url, out var err))
        {
            AppendLog(err);
            StatusText = "Не удалось открыть ссылку. Скопируй и открой вручную.";
        }
        else
        {
            StatusText = "Открыл ссылку в браузере.";
        }
    }

    private void CopyLoginUrl()
    {
        var url = LoginUrl;
        if (string.IsNullOrWhiteSpace(url))
            return;

        try
        {
            App.Current?.Dispatcher.Invoke((Action)(() => Clipboard.SetText(url)));
            StatusText = "Ссылка скопирована в буфер обмена.";
            AppendLog("Ссылка скопирована.");
        }
        catch (Exception ex)
        {
            AppendLog(ex.ToString());
            StatusText = "Не удалось скопировать ссылку.";
        }
    }

    private static bool TryOpenUrlInBrowser(string url, out string error)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
            error = "";
            return true;
        }
        catch (Exception ex1)
        {
            try
            {
                Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = url, UseShellExecute = true });
                error = "";
                return true;
            }
            catch (Exception ex2)
            {
                try
                {
                    var escaped = url.Replace("\"", "\\\"");
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "cmd.exe",
                        Arguments = $"/c start \"\" \"{escaped}\"",
                        CreateNoWindow = true,
                        UseShellExecute = false
                    });
                    error = "";
                    return true;
                }
                catch (Exception ex3)
                {
                    error =
                        "Не удалось открыть браузер автоматически.\n" +
                        $"1) {ex1.Message}\n" +
                        $"2) {ex2.Message}\n" +
                        $"3) {ex3.Message}";
                    return false;
                }
            }
        }
    }

    private void SiteLogout()
    {
        try
        {
            CancelLoginWait();

            _tokens = null;
            _tokenStore.Clear();

            Profile = null;
            Rezonite = 0;

            IsLoggedIn = false;
            IsWaitingSiteConfirm = false;
            SiteUserName = "Не вошли";

            LoginUrl = null;

            StatusText = "Вы вышли.";
            AppendLog("Сайт: выход выполнен.");
        }
        finally
        {
            RefreshCanStates();
        }
    }

    // =========================
    // Play flow
    // =========================

    private async Task PlayAsync()
    {
        if (SelectedServer is null)
        {
            StatusText = "Сервер не выбран.";
            return;
        }

        if (Interlocked.Exchange(ref _playGuard, 1) == 1)
            return;

        try
        {
            IsBusy = true;
            StatusText = $"Подготовка {BuildDisplayName}...";
            ProgressPercent = 0;

            // ✅ 0.1.8: зеркала паков с приоритетом SourceForge
            var mirrors = BuildPackMirrors(SelectedServer);

            var loader = CreateLoaderSpecFromServer(SelectedServer);

            var launchVersionId = await _mc.PrepareAsync(
                minecraftVersion: SelectedServer.MinecraftVersion,
                loader: loader,
                packMirrors: mirrors,
                syncPack: SelectedServer.SyncPack,
                ct: CancellationToken.None);

            Versions.Clear();
            Versions.Add(launchVersionId);
            SelectedVersion = launchVersionId;

            TrySaveSetting("Username", Username);
            TrySaveSetting("RamMb", RamMb);
            TrySaveSetting("SelectedServerId", SelectedServer.Id);
            Settings.Default.Save();

            StatusText = "Запуск игры...";

            _runningProcess = await _mc.BuildAndLaunchAsync(
                version: launchVersionId,
                username: Username.Trim(),
                ramMb: RamMb,
                serverIp: string.IsNullOrWhiteSpace(ServerIp) ? null : ServerIp.Trim());

            _runningProcess.EnableRaisingEvents = true;

            Raise(nameof(CanStop));
            StopGameCommand.RaiseCanExecuteChanged();

            _runningProcess.Exited += (_, __) =>
            {
                App.Current?.Dispatcher.Invoke((Action)(() =>
                {
                    AppendLog("Игра закрыта.");
                    _runningProcess = null;
                    Raise(nameof(CanStop));
                    StopGameCommand.RaiseCanExecuteChanged();
                    RefreshCanStates();
                }));
            };

            AppendLog("Игра запущена.");
            StatusText = "Игра запущена.";
        }
        catch (Exception ex)
        {
            StatusText = "Ошибка запуска.";
            AppendLog(ex.ToString());
        }
        finally
        {
            IsBusy = false;
            Interlocked.Exchange(ref _playGuard, 0);
            RefreshCanStates();
        }
    }

    private MinecraftService.LoaderSpec CreateLoaderSpecFromServer(ServerEntry s)
    {
        var loaderType = (s.LoaderName ?? "vanilla").Trim().ToLowerInvariant();
        var loaderVer = (s.LoaderVersion ?? "").Trim();
        var installerUrl = (s.LoaderInstallerUrl ?? "").Trim();

        if (loaderType == "vanilla" || string.IsNullOrWhiteSpace(loaderType))
            return new MinecraftService.LoaderSpec("vanilla", "", "");

        if (string.IsNullOrWhiteSpace(loaderVer))
            throw new InvalidOperationException($"Loader '{loaderType}' требует версию (loader.version).");

        // ✅ 0.1.8: если installerUrl пустой — ставим официальный Maven
        if (string.IsNullOrWhiteSpace(installerUrl))
        {
            if (loaderType == "neoforge")
                installerUrl = $"https://maven.neoforged.net/releases/net/neoforged/neoforge/{loaderVer}/neoforge-{loaderVer}-installer.jar";
            else if (loaderType == "forge")
                installerUrl = $"https://maven.minecraftforge.net/net/minecraftforge/forge/{s.MinecraftVersion}-{loaderVer}/forge-{s.MinecraftVersion}-{loaderVer}-installer.jar";
            else
                throw new InvalidOperationException($"Неизвестный loader '{loaderType}' (нет installerUrl).");
        }

        return new MinecraftService.LoaderSpec(loaderType, loaderVer, installerUrl);
    }

    private void OpenGameDir()
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = _gameDir, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppendLog(ex.ToString());
        }
    }

    private void StopGame()
    {
        try
        {
            if (_runningProcess is null || _runningProcess.HasExited) return;
            _runningProcess.Kill(entireProcessTree: true);
            AppendLog("Процесс игры остановлен.");
        }
        catch (Exception ex)
        {
            AppendLog(ex.ToString());
        }
        finally
        {
            Raise(nameof(CanStop));
            StopGameCommand.RaiseCanExecuteChanged();
            RefreshCanStates();
        }
    }

    // =========================
    // Misc
    // =========================

    private void AppendLog(string text)
    {
        App.Current?.Dispatcher.Invoke((Action)(() =>
        {
            LogLines.Add($"[{DateTime.Now:HH:mm:ss}] {text}");
            if (LogLines.Count > 500)
                LogLines.RemoveAt(0);
        }));
    }

    private void RefreshCanStates()
    {
        Raise(nameof(CanPlay));
        Raise(nameof(CanStop));
        Raise(nameof(PlayButtonText));
        Raise(nameof(LoginButtonText));

        Raise(nameof(IsAuthPage));
        Raise(nameof(IsStartPage));
        Raise(nameof(IsProfilePage));
        Raise(nameof(IsSettingsPage));

        Raise(nameof(HasLoginUrl));

        Raise(nameof(PackName));
        Raise(nameof(MinecraftVersion));
        Raise(nameof(LoaderName));
        Raise(nameof(LoaderVersion));
        Raise(nameof(BuildDisplayName));

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
