// File: MainWindow.xaml.cs
using System;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using LegendBorn.Services;
using LegendBorn.ViewModels;

namespace LegendBorn;

public partial class MainWindow : Window
{
    private const int NewsTabIndex = 4;

    private bool _updatesChecked;
    private bool _isClosing;

    private readonly MainViewModel _vm;

    // ===== responsive sizing =====
    private bool _responsiveApplied;

    // ===== maximize/restore =====
    private Rect _restoreBounds;
    private bool _hasRestoreBounds;

    // ===== prefs (game ui mode) =====
    private enum LauncherGameUiMode { Hide, Minimize, None }
    private LauncherGameUiMode _gameUiMode = LauncherGameUiMode.Hide; // default
    private bool _settingModeGuard;

    private bool _wasGameRunning;
    private bool _uiChangedForGame;
    private WindowState _preGameWindowState;
    private bool _preGameWasVisible;

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

        // prefs can be loaded before VM (they target Window DP)
        LoadPrefs();
        ApplyModeToBindings();

        _vm = new MainViewModel();
        DataContext = _vm;

        _vm.PropertyChanged += VmOnPropertyChanged;

        Loaded += MainWindow_OnLoaded;
        Closing += MainWindow_OnClosing;

        // Запоминаем restore bounds только когда окно в Normal.
        StateChanged += (_, __) => OnWindowBoundsPossiblyChanged();
        LocationChanged += (_, __) => OnWindowBoundsPossiblyChanged();
        SizeChanged += (_, __) => OnWindowBoundsPossiblyChanged();
    }

    private void MainWindow_OnClosing(object? sender, CancelEventArgs e)
    {
        if (_isClosing) return;
        _isClosing = true;

        try { _vm.PropertyChanged -= VmOnPropertyChanged; } catch { }

        try { SavePrefs(); } catch { }
        try { _vm.MarkClosing(); } catch { }
    }

    private void MainWindow_OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplyResponsiveWindowSizeOnce();

        if (_updatesChecked) return;
        _updatesChecked = true;

        _ = RunUpdateCheckSafeAsync();
    }

    private async Task RunUpdateCheckSafeAsync()
    {
        try
        {
            if (_isClosing) return;
            await UpdateService.CheckAndUpdateAsync(silent: false, showNoUpdates: false).ConfigureAwait(false);
        }
        catch { }
    }

    // ===================== Responsive Window Size =====================
    private void ApplyResponsiveWindowSizeOnce()
    {
        if (_responsiveApplied) return;
        _responsiveApplied = true;

        try
        {
            var work = SystemParameters.WorkArea;

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

            UpdateRestoreBoundsFromWindow();
        }
        catch
        {
            // keep XAML size
        }
    }

    private void OnWindowBoundsPossiblyChanged()
    {
        try
        {
            if (_isClosing) return;
            if (WindowState == WindowState.Normal)
                UpdateRestoreBoundsFromWindow();
        }
        catch { }
    }

    private void UpdateRestoreBoundsFromWindow()
    {
        try
        {
            _restoreBounds = new Rect(Left, Top, Width, Height);
            _hasRestoreBounds = _restoreBounds.Width > 0 && _restoreBounds.Height > 0;
        }
        catch { }
    }

    private static Rect ClampToWorkArea(Rect r)
    {
        try
        {
            var work = SystemParameters.WorkArea;

            var minW = 600.0;
            var minH = 420.0;

            var w = Math.Max(minW, Math.Min(r.Width, work.Width));
            var h = Math.Max(minH, Math.Min(r.Height, work.Height));

            var left = r.Left;
            var top = r.Top;

            if (left < work.Left) left = work.Left;
            if (top < work.Top) top = work.Top;

            if (left + w > work.Right) left = Math.Max(work.Left, work.Right - w);
            if (top + h > work.Bottom) top = Math.Max(work.Top, work.Bottom - h);

            return new Rect(left, top, w, h);
        }
        catch
        {
            return r;
        }
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

            if (IsVisible)
            {
                Activate();
                Topmost = true;
                Topmost = false;
            }
        }
        catch { }
    }

    private void SetUiMode(LauncherGameUiMode mode)
    {
        if (_gameUiMode == mode) return;
        _gameUiMode = mode;

        ApplyModeToBindings();
        try { SavePrefs(); } catch { }
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

            var json = File.ReadAllText(PrefsPath, Encoding.UTF8);
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
            File.WriteAllText(tmp, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

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

            TryDeleteQuiet(tmp);
        }
        catch { }
    }

    private static void TryDeleteQuiet(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    // ===================== TopBar handlers (MainWindow.xaml) =====================
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
                    var r = ClampToWorkArea(_restoreBounds);
                    Left = r.Left;
                    Top = r.Top;
                    Width = r.Width;
                    Height = r.Height;
                }

                var wc = System.Windows.Shell.WindowChrome.GetWindowChrome(this);
                if (wc != null) wc.CornerRadius = new CornerRadius(16);
            }
            else
            {
                if (WindowState == WindowState.Normal)
                    UpdateRestoreBoundsFromWindow();

                WindowState = WindowState.Maximized;

                var wc = System.Windows.Shell.WindowChrome.GetWindowChrome(this);
                if (wc != null) wc.CornerRadius = new CornerRadius(0);
            }
        }
        catch { }
    }

    private void OpenNewsTab_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_isClosing) return;
            _vm.SelectedMenuIndex = NewsTabIndex;
        }
        catch { }
    }

    private static bool IsClickOnInteractive(DependencyObject? d)
    {
        while (d != null)
        {
            if (d is ButtonBase) return true;
            if (d is TextBoxBase) return true;              // FIX: правильный TextBoxBase (Controls.Primitives)
            if (d is Selector) return true;
            if (d is Thumb) return true;
            if (d is ScrollBar) return true;
            if (d is Slider) return true;
            if (d is PasswordBox) return true;

            d = VisualTreeHelper.GetParent(d);
        }
        return false;
    }
}
