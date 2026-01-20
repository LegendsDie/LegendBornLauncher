using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using LegendBorn.Services;

namespace LegendBorn;

public partial class MainWindow : Window
{
    private const int NewsTabIndex = 4;
    private const string SiteUrl = "https://ru.legendborn.ru/";

    private bool _updatesChecked;
    private bool _isClosing;

    private readonly MainViewModel _vm;

    // ===== responsive sizing =====
    private bool _responsiveApplied;

    // ===== maximize/restore =====
    private Rect _restoreBounds;
    private bool _hasRestoreBounds;

    // ===== prefs =====
    private enum LauncherGameUiMode { Hide, Minimize, None }
    private LauncherGameUiMode _gameUiMode = LauncherGameUiMode.Hide; // default
    private bool _settingModeGuard;

    private bool _wasGameRunning;
    private bool _uiChangedForGame;
    private WindowState _preGameWindowState;
    private bool _preGameWasVisible;

    // ===== logs autoscroll =====
    private bool _logAutoScroll = true;
    private ScrollChangedEventHandler? _logScrollHandler;

    // ===== news models (UI-level, no VM dependency) =====
    public sealed class NewsItem
    {
        public string Title { get; init; } = "";
        public string Date { get; init; } = "";
        public string Summary { get; init; } = "";
        public string Url { get; init; } = "";
    }

    // XAML binds via ElementName=RootWindow
    public ObservableCollection<NewsItem> ServerNewsTop2 { get; } = new();
    public ObservableCollection<NewsItem> ProjectNews { get; } = new();

    // prefs location (0.2.6+): %AppData%\LegendBorn\launcher.prefs.json
    private static readonly string PrefsPath = Path.Combine(LauncherPaths.AppDir, "launcher.prefs.json");

    // migration path (older builds used "LegendCraft")
    private static readonly string OldPrefsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "LegendCraft",
        "launcher_prefs.json");

    public bool GameUiModeHide
    {
        get => (bool)GetValue(GameUiModeHideProperty);
        set => SetValue(GameUiModeHideProperty, value);
    }

    public static readonly DependencyProperty GameUiModeHideProperty =
        DependencyProperty.Register(nameof(GameUiModeHide), typeof(bool), typeof(MainWindow),
            new PropertyMetadata(false, OnGameUiModeFlagChanged));

    public bool GameUiModeMinimize
    {
        get => (bool)GetValue(GameUiModeMinimizeProperty);
        set => SetValue(GameUiModeMinimizeProperty, value);
    }

    public static readonly DependencyProperty GameUiModeMinimizeProperty =
        DependencyProperty.Register(nameof(GameUiModeMinimize), typeof(bool), typeof(MainWindow),
            new PropertyMetadata(false, OnGameUiModeFlagChanged));

    public bool GameUiModeNone
    {
        get => (bool)GetValue(GameUiModeNoneProperty);
        set => SetValue(GameUiModeNoneProperty, value);
    }

    public static readonly DependencyProperty GameUiModeNoneProperty =
        DependencyProperty.Register(nameof(GameUiModeNone), typeof(bool), typeof(MainWindow),
            new PropertyMetadata(false, OnGameUiModeFlagChanged));

    private static void OnGameUiModeFlagChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var w = (MainWindow)d;
        if (w._settingModeGuard) return;
        if (e.NewValue is not bool b || !b) return;

        if (ReferenceEquals(e.Property, GameUiModeHideProperty))
            w.SetUiMode(LauncherGameUiMode.Hide);
        else if (ReferenceEquals(e.Property, GameUiModeMinimizeProperty))
            w.SetUiMode(LauncherGameUiMode.Minimize);
        else if (ReferenceEquals(e.Property, GameUiModeNoneProperty))
            w.SetUiMode(LauncherGameUiMode.None);
    }

    public MainWindow()
    {
        InitializeComponent();

        SeedNews();

        // prefs can be loaded before VM (they target Window DP)
        LoadPrefs();
        ApplyModeToBindings();

        _vm = new MainViewModel();
        DataContext = _vm;

        _vm.PropertyChanged += VmOnPropertyChanged;

        Loaded += MainWindow_OnLoaded;
        Closing += MainWindow_OnClosing;
        Closed += MainWindow_OnClosed;

        // keep restore bounds current
        StateChanged += (_, __) =>
        {
            try
            {
                if (_isClosing) return;
                if (WindowState == WindowState.Normal)
                {
                    _restoreBounds = new Rect(Left, Top, Width, Height);
                    _hasRestoreBounds = true;
                }
            }
            catch { }
        };
    }

    private void SeedNews()
    {
        var now = DateTime.Now;

        ServerNewsTop2.Clear();
        ServerNewsTop2.Add(new NewsItem
        {
            Title = "Технические работы",
            Date = now.ToString("dd.MM"),
            Summary = "Сегодня возможны краткие перезапуски сервера. Спасибо за понимание.",
            Url = SiteUrl
        });
        ServerNewsTop2.Add(new NewsItem
        {
            Title = "Обновление сборки",
            Date = now.AddDays(-1).ToString("dd.MM"),
            Summary = "Исправления стабильности и подготовка к новым механикам.",
            Url = SiteUrl
        });

        ProjectNews.Clear();
        ProjectNews.Add(new NewsItem
        {
            Title = "LegendBorn: Дорожная карта",
            Date = now.ToString("dd.MM.yyyy"),
            Summary = "Публикуем ближайшие цели и приоритеты разработки.",
            Url = SiteUrl
        });
        ProjectNews.Add(new NewsItem
        {
            Title = "Launcher: улучшения интерфейса",
            Date = now.ToString("dd.MM.yyyy"),
            Summary = "Новый блок новостей, быстрые кнопки и улучшенный top-bar.",
            Url = SiteUrl
        });
    }

    private void MainWindow_OnClosed(object? sender, EventArgs e)
    {
        TryUnhookLogsUi();
    }

    private void MainWindow_OnClosing(object? sender, CancelEventArgs e)
    {
        if (_isClosing) return;
        _isClosing = true;

        try { _vm.PropertyChanged -= VmOnPropertyChanged; } catch { }

        TryUnhookLogsUi();

        try { SavePrefs(); } catch { }
        try { _vm.MarkClosing(); } catch { }
    }

    private void MainWindow_OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplyResponsiveWindowSizeOnce();
        HookLogsUi();

        if (_updatesChecked) return;
        _updatesChecked = true;

        _ = RunUpdateCheckSafeAsync();
    }

    // ===================== Responsive Window Size =====================
    private void ApplyResponsiveWindowSizeOnce()
    {
        if (_responsiveApplied) return;
        _responsiveApplied = true;

        try
        {
            var work = SystemParameters.WorkArea;

            // safe margins
            var maxW = Math.Max(800, work.Width - 80);
            var maxH = Math.Max(540, work.Height - 80);

            var presets = new (double w, double h)[]
            {
                (1280, 860),
                (1200, 800),
                (1100, 740),
                (1020, 700),
                (980, 660),
                (920, 600),
            };

            (double w, double h) chosen = (Width, Height);
            foreach (var p in presets)
            {
                if (p.w <= maxW && p.h <= maxH)
                {
                    chosen = p;
                    break;
                }
            }

            Width = Math.Min(chosen.w, maxW);
            Height = Math.Min(chosen.h, maxH);

            Left = work.Left + (work.Width - Width) / 2;
            Top = work.Top + (work.Height - Height) / 2;

            _restoreBounds = new Rect(Left, Top, Width, Height);
            _hasRestoreBounds = true;
        }
        catch
        {
            // keep XAML size
        }
    }

    // ===================== Logs UI =====================
    private void HookLogsUi()
    {
        try
        {
            if (_isClosing) return;
            if (LogListBox == null) return;

            _logScrollHandler ??= LogScrollViewer_ScrollChanged;
            LogListBox.AddHandler(ScrollViewer.ScrollChangedEvent, _logScrollHandler);

            if (_vm.LogLines is INotifyCollectionChanged ncc)
                ncc.CollectionChanged += LogLines_CollectionChanged;

            ScrollLogsToEnd(force: true);
        }
        catch { }
    }

    private void TryUnhookLogsUi()
    {
        try
        {
            if (_vm.LogLines is INotifyCollectionChanged ncc)
                ncc.CollectionChanged -= LogLines_CollectionChanged;
        }
        catch { }

        try
        {
            if (LogListBox != null && _logScrollHandler != null)
                LogListBox.RemoveHandler(ScrollViewer.ScrollChangedEvent, _logScrollHandler);
        }
        catch { }
    }

    private void LogScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        try
        {
            if (_isClosing) return;

            // new items appended; not user scroll
            if (e.ExtentHeightChange != 0)
                return;

            var bottom = Math.Max(0, e.ExtentHeight - e.ViewportHeight);
            _logAutoScroll = e.VerticalOffset >= bottom - 1.0;
        }
        catch { }
    }

    private void LogLines_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_isClosing) return;
        if (!_logAutoScroll) return;

        Dispatcher.BeginInvoke(new Action(() => ScrollLogsToEnd(force: false)));
    }

    private void ScrollLogsToEnd(bool force)
    {
        try
        {
            if (_isClosing) return;
            if (LogListBox == null) return;

            var count = LogListBox.Items.Count;
            if (count <= 0) return;

            if (!force && !_logAutoScroll)
                return;

            LogListBox.ScrollIntoView(LogListBox.Items[count - 1]);
        }
        catch { }
    }

    // ===================== Game running / mode =====================
    private void VmOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_isClosing) return;

        if (e.PropertyName != nameof(MainViewModel.CanStop))
            return;

        var running = _vm.CanStop;

        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_isClosing) return;

            if (running && !_wasGameRunning)
                OnGameStarted();

            if (!running && _wasGameRunning)
                OnGameStopped();

            _wasGameRunning = running;
        }));
    }

    private void OnGameStarted()
    {
        if (_gameUiMode == LauncherGameUiMode.None)
            return;

        _preGameWindowState = WindowState;
        _preGameWasVisible = IsVisible;
        _uiChangedForGame = true;

        try
        {
            if (_gameUiMode == LauncherGameUiMode.Hide)
            {
                if (IsVisible) Hide();
            }
            else if (_gameUiMode == LauncherGameUiMode.Minimize)
            {
                if (WindowState != WindowState.Minimized)
                    WindowState = WindowState.Minimized;
            }
        }
        catch { }
    }

    private void OnGameStopped()
    {
        if (!_uiChangedForGame)
            return;

        _uiChangedForGame = false;

        try
        {
            if (_gameUiMode == LauncherGameUiMode.Hide && _preGameWasVisible && !IsVisible)
                Show();

            WindowState = _preGameWindowState == WindowState.Minimized
                ? WindowState.Normal
                : _preGameWindowState;

            Activate();
            Topmost = true;
            Topmost = false;
        }
        catch { }
    }

    private void SetUiMode(LauncherGameUiMode mode)
    {
        if (_gameUiMode == mode) return;
        _gameUiMode = mode;

        ApplyModeToBindings();
        SavePrefs();
    }

    private void ApplyModeToBindings()
    {
        _settingModeGuard = true;
        try
        {
            GameUiModeHide = _gameUiMode == LauncherGameUiMode.Hide;
            GameUiModeMinimize = _gameUiMode == LauncherGameUiMode.Minimize;
            GameUiModeNone = _gameUiMode == LauncherGameUiMode.None;
        }
        finally
        {
            _settingModeGuard = false;
        }
    }

    private sealed class PrefsDto
    {
        public string? GameUiMode { get; set; }
    }

    private void LoadPrefs()
    {
        try
        {
            // migrate from old location if needed
            if (!File.Exists(PrefsPath) && File.Exists(OldPrefsPath))
            {
                try
                {
                    LauncherPaths.EnsureParentDirForFile(PrefsPath);
                    File.Copy(OldPrefsPath, PrefsPath, overwrite: true);
                }
                catch { }
            }

            if (!File.Exists(PrefsPath))
            {
                _gameUiMode = LauncherGameUiMode.Hide;
                return;
            }

            var json = File.ReadAllText(PrefsPath);
            var dto = JsonSerializer.Deserialize<PrefsDto>(json);

            var s = (dto?.GameUiMode ?? "").Trim();
            _gameUiMode =
                s.Equals(nameof(LauncherGameUiMode.Minimize), StringComparison.OrdinalIgnoreCase) ? LauncherGameUiMode.Minimize :
                s.Equals(nameof(LauncherGameUiMode.None), StringComparison.OrdinalIgnoreCase) ? LauncherGameUiMode.None :
                LauncherGameUiMode.Hide;
        }
        catch
        {
            _gameUiMode = LauncherGameUiMode.Hide;
        }
    }

    private void SavePrefs()
    {
        try
        {
            LauncherPaths.EnsureParentDirForFile(PrefsPath);

            var dto = new PrefsDto { GameUiMode = _gameUiMode.ToString() };
            var json = JsonSerializer.Serialize(dto, new JsonSerializerOptions { WriteIndented = true });

            var tmp = PrefsPath + ".tmp";
            File.WriteAllText(tmp, json);

            if (File.Exists(PrefsPath))
            {
                var bak = PrefsPath + ".bak";
                try { if (File.Exists(bak)) File.Delete(bak); } catch { }

                File.Replace(tmp, PrefsPath, bak, ignoreMetadataErrors: true);
                try { if (File.Exists(bak)) File.Delete(bak); } catch { }
            }
            else
            {
                File.Move(tmp, PrefsPath);
            }

            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
        catch { }
    }

    private async Task RunUpdateCheckSafeAsync()
    {
        try
        {
            if (_isClosing) return;
            await UpdateService.CheckAndUpdateAsync(silent: false, showNoUpdates: false);
        }
        catch { }
    }

    // ===================== Helpers =====================
    private static void TryOpenUrl(string url)
    {
        try
        {
            url = (url ?? "").Trim();
            if (string.IsNullOrWhiteSpace(url)) return;

            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch { }
    }

    private static bool IsClickOnInteractive(DependencyObject? d)
    {
        while (d != null)
        {
            if (d is ButtonBase) return true;
            if (d is TextBoxBase) return true;
            if (d is Selector) return true;
            if (d is Thumb) return true;
            if (d is ScrollBar) return true;
            if (d is Slider) return true;

            d = VisualTreeHelper.GetParent(d);
        }
        return false;
    }

    // ===================== XAML handlers =====================
    private void Close_OnClick(object sender, RoutedEventArgs e) => Close();

    private void Minimize_OnClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void TitleBar_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
            return;

        if (IsClickOnInteractive(e.OriginalSource as DependencyObject))
            return;

        if (e.ClickCount == 2)
        {
            ToggleMaximizeRestore();
            return;
        }

        try { DragMove(); } catch { }
    }

    private void ToggleMaximizeRestore()
    {
        try
        {
            if (WindowState == WindowState.Maximized)
            {
                WindowState = WindowState.Normal;

                if (_hasRestoreBounds)
                {
                    Left = _restoreBounds.Left;
                    Top = _restoreBounds.Top;
                    Width = _restoreBounds.Width;
                    Height = _restoreBounds.Height;
                }

                var wc = System.Windows.Shell.WindowChrome.GetWindowChrome(this);
                if (wc != null) wc.CornerRadius = new CornerRadius(16);
            }
            else
            {
                if (WindowState == WindowState.Normal)
                {
                    _restoreBounds = new Rect(Left, Top, Width, Height);
                    _hasRestoreBounds = true;
                }

                WindowState = WindowState.Maximized;

                var wc = System.Windows.Shell.WindowChrome.GetWindowChrome(this);
                if (wc != null) wc.CornerRadius = new CornerRadius(0);
            }
        }
        catch { }
    }

    // One button: Play OR Stop
    private void PlayOrStop_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_isClosing) return;

            if (_vm.CanStop)
            {
                if (_vm.StopGameCommand?.CanExecute(null) == true)
                    _vm.StopGameCommand.Execute(null);
                return;
            }

            if (_vm.PlayCommand?.CanExecute(null) == true)
                _vm.PlayCommand.Execute(null);
        }
        catch { }
    }

    private void OpenSite_OnClick(object sender, RoutedEventArgs e)
    {
        if (_isClosing) return;
        TryOpenUrl(SiteUrl);
    }

    // Top bar / buttons: open News tab
    private void OpenNewsTab_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_isClosing) return;

            try { _vm.SelectedMenuIndex = NewsTabIndex; } catch { }

            if (MainTabs != null)
                MainTabs.SelectedIndex = NewsTabIndex;
        }
        catch { }
    }

    // News item click (Button.Tag contains URL)
    private void OpenNewsItem_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_isClosing) return;

            if (sender is FrameworkElement fe && fe.Tag is string url && !string.IsNullOrWhiteSpace(url))
                TryOpenUrl(url);
        }
        catch { }
    }

    // Copy link button also regenerates if missing
    private async void CopyOrRegenLoginLink_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_isClosing) return;

            if (_vm.HasLoginUrl)
            {
                if (_vm.CopyLoginUrlCommand?.CanExecute(null) == true)
                    _vm.CopyLoginUrlCommand.Execute(null);
                return;
            }

            if (_vm.LoginViaSiteCommand?.CanExecute(null) == true)
                _vm.LoginViaSiteCommand.Execute(null);

            // Wait up to ~4.5 sec for URL to appear in VM.
            for (var i = 0; i < 30; i++)
            {
                if (_isClosing) return;

                await Task.Delay(150).ConfigureAwait(true);

                if (_vm.HasLoginUrl)
                {
                    if (_vm.CopyLoginUrlCommand?.CanExecute(null) == true)
                        _vm.CopyLoginUrlCommand.Execute(null);
                    return;
                }
            }

            if (_isClosing) return;

            MessageBox.Show(
                "Не удалось получить ссылку авторизации. Попробуйте ещё раз.",
                "Авторизация",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch { }
    }

    private void CopyLogs_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_isClosing) return;

            if (_vm.LogLines is null || _vm.LogLines.Count == 0)
                return;

            // UI says "последние 100"
            var lines = _vm.LogLines.Count <= 100
                ? _vm.LogLines.ToArray()
                : _vm.LogLines.Skip(Math.Max(0, _vm.LogLines.Count - 100)).ToArray();

            Clipboard.SetText(string.Join(Environment.NewLine, lines));
        }
        catch { }
    }
}
