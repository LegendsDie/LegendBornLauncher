using System;
using System.Collections.ObjectModel;
using System.Configuration;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
    // 0 = Auth, 1 = Start, 2 = Profile, 3 = Settings
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

    // ===== Pack display (UI) =====
    public string PackName => "LegendBorn";
    public string MinecraftVersion => "1.21.1";
    public string LoaderName => "NeoForge";
    public string LoaderVersion => "21.1.34";
    public string BuildDisplayName => $"{PackName} • {LoaderName} {MinecraftVersion}";

    // ===== One server =====
    public sealed class ServerEntry
    {
        public string Name { get; init; } = "";
        public string Address { get; init; } = "";
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
                    ServerIp = value.Address;
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
                // ✅ Резонит берём из /api/launcher/me (там он рассчитан из wallet/ledger)
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

    // База сайта, нужна чтобы корректно собрать абсолютный URL аватарки
    private const string SiteBaseUrl = "https://legendborn.ru";

    // ===== АВАТАРКА: поддержка абсолютных и относительных ссылок =====
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
                RefreshCanStates();
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

    // ===== Login URL (NEW) =====
    private string? _loginUrl;
    public string? LoginUrl
    {
        get => _loginUrl;
        set
        {
            if (Set(ref _loginUrl, value))
            {
                Raise(nameof(HasLoginUrl));
                Raise(nameof(LoginUrlVisibility));
                RefreshCanStates();
            }
        }
    }

    public bool HasLoginUrl => !string.IsNullOrWhiteSpace(LoginUrl);
    public Visibility LoginUrlVisibility => HasLoginUrl ? Visibility.Visible : Visibility.Collapsed;

    private Process? _runningProcess;
    public bool CanStop => _runningProcess is { HasExited: false };

    public bool CanPlay =>
        !IsBusy &&
        !IsWaitingSiteConfirm &&
        IsLoggedIn &&
        Profile is not null &&
        Profile.CanPlay &&
        !string.IsNullOrWhiteSpace(SelectedVersion) &&
        !string.IsNullOrWhiteSpace(Username);

    public string PlayButtonText => IsBusy ? "..." : "Играть";
    public string LoginButtonText => IsWaitingSiteConfirm ? "Ожидание..." : "Войти через сайт";

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

    // NEW: user actions for login link
    public RelayCommand OpenLoginUrlCommand { get; private set; } = null!;
    public RelayCommand CopyLoginUrlCommand { get; private set; } = null!;

    private readonly string _gameDir;
    private bool _commandsReady;

    private const string DefaultVersionName = "LegendBorn";

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
        {
            App.Current?.Dispatcher.Invoke(() => ProgressPercent = p);
        };

        // Commands
        RefreshVersionsCommand = new AsyncRelayCommand(RefreshVersionsAsync, () => !IsBusy && !IsWaitingSiteConfirm);
        PlayCommand = new AsyncRelayCommand(PlayAsync, () => CanPlay);
        OpenGameDirCommand = new RelayCommand(OpenGameDir);
        StopGameCommand = new RelayCommand(StopGame, () => CanStop);

        LoginViaSiteCommand = new AsyncRelayCommand(LoginViaSiteAsync, () => !IsBusy && !IsLoggedIn && !IsWaitingSiteConfirm);
        SiteLogoutCommand = new RelayCommand(SiteLogout, () => IsLoggedIn || IsWaitingSiteConfirm);

        ClearLogCommand = new RelayCommand(() => LogLines.Clear());

        OpenSettingsCommand = new RelayCommand(() => SelectedMenuIndex = 3);

        // кнопка "LegendBorn Launcher" -> Start/Auth
        OpenStartCommand = new RelayCommand(() =>
        {
            SelectedMenuIndex = IsLoggedIn ? 1 : 0;
        });

        OpenProfileCommand = new RelayCommand(() =>
        {
            if (IsLoggedIn) SelectedMenuIndex = 2;
        }, () => IsLoggedIn);

        // NEW: commands for login URL
        OpenLoginUrlCommand = new RelayCommand(OpenLoginUrl, () => HasLoginUrl);
        CopyLoginUrlCommand = new RelayCommand(CopyLoginUrl, () => HasLoginUrl);

        _commandsReady = true;

        // settings
        Username = TryLoadStringSetting("Username", "Player") ?? "Player";
        ServerIp = TryLoadStringSetting("ServerIp", "legendcraft.minerent.io") ?? "legendcraft.minerent.io";

        SelectedVersion = TryLoadStringSetting("SelectedVersion", null);
        if (string.IsNullOrWhiteSpace(SelectedVersion))
            SelectedVersion = DefaultVersionName;

        RamMb = TryLoadIntSetting("RamMb", 4096);
        if (!RamOptions.Contains(RamMb))
            RamMb = 4096;

        RebuildServers();

        _ = RefreshVersionsAsync();
        _ = TryAutoLoginAsync();

        RefreshCanStates();
    }

    private void RebuildServers()
    {
        Servers.Clear();
        Servers.Add(new ServerEntry { Name = "LegendBorn", Address = "legendcraft.minerent.io" });

        SelectedServer = Servers.FirstOrDefault();
        if (SelectedServer is not null)
            ServerIp = SelectedServer.Address;
    }

    private async Task RefreshVersionsAsync()
    {
        try
        {
            IsBusy = true;
            StatusText = "Проверка сборки...";
            ProgressPercent = 0;

            Versions.Clear();

            try
            {
                var all = await _mc.GetAllVersionNamesAsync();
                var ours = all.FirstOrDefault(v => v.Equals(DefaultVersionName, StringComparison.OrdinalIgnoreCase))
                           ?? all.FirstOrDefault(v => v.Contains("LegendBorn", StringComparison.OrdinalIgnoreCase));

                if (!string.IsNullOrWhiteSpace(ours))
                {
                    Versions.Add(ours);
                    SelectedVersion = ours;
                }
                else
                {
                    Versions.Add(DefaultVersionName);
                    SelectedVersion = DefaultVersionName;
                }
            }
            catch
            {
                Versions.Add(DefaultVersionName);
                SelectedVersion = DefaultVersionName;
            }

            StatusText = "Готово.";
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

    private async Task TrySendDailyLauncherLoginEventAsync()
    {
        try
        {
            if (_tokens is null || string.IsNullOrWhiteSpace(_tokens.AccessToken))
                return;

            // 1 раз в день на пользователя (идемпотентно)
            var key = "launcher_login";
            var idem = $"launcher_login:{DateTime.UtcNow:yyyy-MM-dd}";

            await _site.SendLauncherEventAsync(
                _tokens.AccessToken,
                key,
                idem,
                payload: new { client = "LegendBornLauncher", v = "1" },
                ct: CancellationToken.None);
        }
        catch
        {
            // не критично
        }
    }

    private async Task TryAutoLoginAsync()
    {
        var saved = _tokenStore.Load();
        if (saved is null || string.IsNullOrWhiteSpace(saved.AccessToken))
            return;

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
        }
        finally
        {
            IsBusy = false;
            StatusText = "Готово.";
            RefreshCanStates();
        }
    }

    private async Task LoginViaSiteAsync()
    {
        _loginCts?.Cancel();
        _loginCts = new CancellationTokenSource();

        try
        {
            IsWaitingSiteConfirm = true;
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

            // NEW: сохраняем ссылку, чтобы пользователь мог открыть/скопировать вручную
            LoginUrl = fullUrl;

            AppendLog($"Ссылка для входа: {fullUrl}");

            // NEW: пытаемся открыть более надёжно (несколько fallback-методов)
            if (!TryOpenUrlInBrowser(fullUrl, out var openError))
            {
                AppendLog(openError);
                StatusText = "Не удалось открыть браузер автоматически. Нажми «Открыть ссылку» или «Копировать ссылку».";
            }
            else
            {
                StatusText = "Открой сайт и нажми «В путь». Если сайт не открылся — нажми «Открыть ссылку» или «Копировать ссылку».";
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

            // NEW: очищаем ссылку, когда ожидание закончилось
            LoginUrl = null;

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
            StatusText = "Не удалось открыть ссылку. Скопируй её и открой вручную (например через VPN/другой браузер).";
        }
        else
        {
            StatusText = "Открыл ссылку в браузере. Если сайт блокируется — открой через VPN.";
        }
    }

    private void CopyLoginUrl()
    {
        var url = LoginUrl;
        if (string.IsNullOrWhiteSpace(url))
            return;

        try
        {
            App.Current?.Dispatcher.Invoke(() => Clipboard.SetText(url));
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
        // 1) самый стандартный путь
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
            error = "";
            return true;
        }
        catch (Exception ex1)
        {
            // 2) через explorer.exe (часто работает, когда default browser сломан)
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = url,
                    UseShellExecute = true
                });
                error = "";
                return true;
            }
            catch (Exception ex2)
            {
                // 3) через cmd start (очень надёжный fallback)
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
            _loginCts?.Cancel();
            _loginCts = null;

            _tokens = null;
            _tokenStore.Clear();

            Profile = null;
            Rezonite = 0;

            IsLoggedIn = false;
            IsWaitingSiteConfirm = false;
            SiteUserName = "Не вошли";

            LoginUrl = null;

            AppendLog("Сайт: выход выполнен.");
        }
        finally
        {
            RefreshCanStates();
        }
    }

    private async Task PlayAsync()
    {
        if (string.IsNullOrWhiteSpace(SelectedVersion))
            SelectedVersion = DefaultVersionName;

        try
        {
            IsBusy = true;
            StatusText = $"Подготовка {BuildDisplayName}...";
            ProgressPercent = 0;

            await _mc.InstallAsync(SelectedVersion);

            TrySaveSetting("Username", Username);
            TrySaveSetting("ServerIp", ServerIp);
            TrySaveSetting("SelectedVersion", SelectedVersion ?? "");
            TrySaveSetting("RamMb", RamMb);
            Settings.Default.Save();

            StatusText = "Запуск игры...";

            _runningProcess = await _mc.BuildAndLaunchAsync(
                SelectedVersion,
                Username.Trim(),
                ramMb: RamMb,
                serverIp: string.IsNullOrWhiteSpace(ServerIp) ? null : ServerIp.Trim());

            _runningProcess.EnableRaisingEvents = true;

            Raise(nameof(CanStop));
            StopGameCommand.RaiseCanExecuteChanged();

            _runningProcess.Exited += (_, __) =>
            {
                App.Current?.Dispatcher.Invoke(() =>
                {
                    AppendLog("Игра закрыта.");
                    _runningProcess = null;
                    Raise(nameof(CanStop));
                    StopGameCommand.RaiseCanExecuteChanged();
                    RefreshCanStates();
                });
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
            RefreshCanStates();
        }
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

    private void AppendLog(string text)
    {
        App.Current?.Dispatcher.Invoke(() =>
        {
            LogLines.Add($"[{DateTime.Now:HH:mm:ss}] {text}");
            if (LogLines.Count > 500)
                LogLines.RemoveAt(0);
        });
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
        Raise(nameof(LoginUrlVisibility));

        if (!_commandsReady) return;

        RefreshVersionsCommand.RaiseCanExecuteChanged();
        PlayCommand.RaiseCanExecuteChanged();
        StopGameCommand.RaiseCanExecuteChanged();

        LoginViaSiteCommand.RaiseCanExecuteChanged();
        SiteLogoutCommand.RaiseCanExecuteChanged();

        OpenProfileCommand.RaiseCanExecuteChanged();

        OpenLoginUrlCommand.RaiseCanExecuteChanged();
        CopyLoginUrlCommand.RaiseCanExecuteChanged();
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
        catch (SettingsPropertyNotFoundException)
        {
            // ignore
        }
        catch
        {
            // ignore
        }
    }
}
