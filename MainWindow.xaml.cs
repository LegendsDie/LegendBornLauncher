using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
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
    private bool _updatesChecked;
    private bool _isClosing;

    private readonly MainViewModel _vm;

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

    private static readonly string PrefsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "LegendCraft", "launcher_prefs.json");

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

        LoadPrefs();
        ApplyModeToBindings();

        _vm = new MainViewModel();
        DataContext = _vm;

        _vm.PropertyChanged += VmOnPropertyChanged;

        Loaded += MainWindow_OnLoaded;
        Closing += MainWindow_OnClosing;
        Closed += MainWindow_OnClosed;
    }

    private void MainWindow_OnClosed(object? sender, EventArgs e)
    {
        // Дублируем “снятие крючков” на случай нестандартного завершения.
        TryUnhookLogsUi();
    }

    private void MainWindow_OnClosing(object? sender, CancelEventArgs e)
    {
        if (_isClosing) return;
        _isClosing = true;

        try { _vm.PropertyChanged -= VmOnPropertyChanged; } catch { }
        TryUnhookLogsUi();

        try { _vm.MarkClosing(); } catch { }
        try { SavePrefs(); } catch { }
    }

    private void MainWindow_OnLoaded(object sender, RoutedEventArgs e)
    {
        HookLogsUi();

        if (_updatesChecked)
            return;

        _updatesChecked = true;
        _ = RunUpdateCheckSafeAsync();
    }

    private void HookLogsUi()
    {
        try
        {
            if (_isClosing) return;
            if (LogListBox == null) return;

            LogListBox.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(LogScrollViewer_ScrollChanged));

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
            if (LogListBox != null)
                LogListBox.RemoveHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(LogScrollViewer_ScrollChanged));
        }
        catch { }
    }

    private void LogScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        try
        {
            if (_isClosing) return;

            // Пользователь крутит колесом/ползунком.
            // Если меняется ExtentHeight — значит добавились строки, тогда этот эвент не про user-scroll.
            if (e.ExtentHeightChange != 0)
                return;

            var bottom = Math.Max(0, e.ExtentHeight - e.ViewportHeight);
            _logAutoScroll = e.VerticalOffset >= bottom - 1.0; // "почти внизу"
        }
        catch { }
    }

    private void LogLines_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_isClosing) return;
        if (!_logAutoScroll) return;

        // Не дергаем UI слишком часто: один BeginInvoke достаточно.
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
                if (IsVisible)
                    Hide();
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
            if (!File.Exists(PrefsPath))
            {
                _gameUiMode = LauncherGameUiMode.Hide;
                return;
            }

            var json = File.ReadAllText(PrefsPath);
            var dto = JsonSerializer.Deserialize<PrefsDto>(json);
            var s = (dto?.GameUiMode ?? "").Trim();

            _gameUiMode = s.Equals("Minimize", StringComparison.OrdinalIgnoreCase) ? LauncherGameUiMode.Minimize
                       : s.Equals("None", StringComparison.OrdinalIgnoreCase) ? LauncherGameUiMode.None
                       : LauncherGameUiMode.Hide;
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
            var dir = Path.GetDirectoryName(PrefsPath);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);

            var dto = new PrefsDto { GameUiMode = _gameUiMode.ToString() };
            var json = JsonSerializer.Serialize(dto, new JsonSerializerOptions { WriteIndented = true });

            var tmp = PrefsPath + ".tmp";
            File.WriteAllText(tmp, json);

            if (File.Exists(PrefsPath))
            {
                // КРИТИЧНО: backupFileName = null у некоторых систем ломает Replace.
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
            if (_isClosing)
                return;

            await UpdateService.CheckAndUpdateAsync(silent: false, showNoUpdates: false);
        }
        catch { }
    }

    // ===== XAML handlers =====
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
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            return;
        }

        try { DragMove(); } catch { }
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

    // ===== One button: Play OR Stop =====
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

    // ===== Copy link button also regenerates if missing =====
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

            // ждём до ~4.5 сек, пока VM получит ссылку
            for (var i = 0; i < 30; i++)
            {
                if (_isClosing) return;

                await Task.Delay(150);

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

    // ===== Copy logs =====
    private void CopyLogs_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_isClosing) return;

            if (_vm.LogLines is null || _vm.LogLines.Count == 0)
                return;

            var text = string.Join(Environment.NewLine, _vm.LogLines);
            Clipboard.SetText(text);
        }
        catch { }
    }
}
