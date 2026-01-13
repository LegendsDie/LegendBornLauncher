using System;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using LegendBorn.Services;
using System.Windows.Controls;

namespace LegendBorn;

public partial class MainWindow : Window
{
    private bool _updatesChecked;
    private bool _isClosing;

    // ===== prefs =====
    private enum LauncherGameUiMode { Hide, Minimize, None }

    private LauncherGameUiMode _gameUiMode = LauncherGameUiMode.Hide; // default
    private bool _settingModeGuard;

    private bool _wasGameRunning;
    private bool _uiChangedForGame;
    private WindowState _preGameWindowState;
    private bool _preGameWasVisible;

    private static readonly string PrefsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "LegendBorn", "launcher_prefs.json");

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

        var vm = new MainViewModel();
        DataContext = vm;

        if (vm is INotifyPropertyChanged inpc)
            inpc.PropertyChanged += VmOnPropertyChanged;

        Loaded += MainWindow_OnLoaded;
        Closing += (_, __) => _isClosing = true;
    }

    private void VmOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainViewModel.CanStop))
            return;

        if (sender is not MainViewModel vm)
            return;

        var running = vm.CanStop;

        if (running && !_wasGameRunning)
            OnGameStarted();

        if (!running && _wasGameRunning)
            OnGameStopped();

        _wasGameRunning = running;
    }

    private void OnGameStarted()
    {
        if (_gameUiMode == LauncherGameUiMode.None)
            return;

        _preGameWindowState = WindowState;
        _preGameWasVisible = IsVisible;
        _uiChangedForGame = true;

        Dispatcher.Invoke(() =>
        {
            if (_gameUiMode == LauncherGameUiMode.Hide)
            {
                Hide();
            }
            else if (_gameUiMode == LauncherGameUiMode.Minimize)
            {
                WindowState = WindowState.Minimized;
            }
        });
    }

    private void OnGameStopped()
    {
        if (!_uiChangedForGame)
            return;

        _uiChangedForGame = false;

        Dispatcher.Invoke(() =>
        {
            try
            {
                if (!IsVisible)
                    Show();

                WindowState = _preGameWindowState == WindowState.Minimized
                    ? WindowState.Normal
                    : _preGameWindowState;

                Activate();
                Topmost = true;
                Topmost = false;
            }
            catch
            {
                // ignore
            }
        });
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
                File.Replace(tmp, PrefsPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
            else
                File.Move(tmp, PrefsPath);
        }
        catch
        {
            // ignore
        }
    }

    private void MainWindow_OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_updatesChecked)
            return;

        _updatesChecked = true;
        _ = RunUpdateCheckSafeAsync();
    }

    private async Task RunUpdateCheckSafeAsync()
    {
        try
        {
            if (_isClosing)
                return;

            await UpdateService.CheckAndUpdateAsync(silent: false, showNoUpdates: false);
        }
        catch
        {
            // updater must not break launch
        }
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

    // ===== One button: Play OR Stop (XAML: Click="PlayOrStop_OnClick") =====
    private void PlayOrStop_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (DataContext is not MainViewModel vm)
                return;

            if (vm.CanStop)
            {
                if (vm.StopGameCommand?.CanExecute(null) == true)
                    vm.StopGameCommand.Execute(null);
                return;
            }

            if (vm.PlayCommand?.CanExecute(null) == true)
                vm.PlayCommand.Execute(null);
        }
        catch
        {
            // ignore; VM logs errors
        }
    }

    // ===== Copy link button also regenerates if missing (XAML: Click="CopyOrRegenLoginLink_OnClick") =====
    private async void CopyOrRegenLoginLink_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (DataContext is not MainViewModel vm)
                return;

            // If link exists -> just copy
            if (vm.HasLoginUrl)
            {
                if (vm.CopyLoginUrlCommand?.CanExecute(null) == true)
                    vm.CopyLoginUrlCommand.Execute(null);
                return;
            }

            // No link: try to re-start login flow, then copy when URL appears.
            if (vm.LoginViaSiteCommand?.CanExecute(null) == true)
                vm.LoginViaSiteCommand.Execute(null);

            // Wait for VM to receive connectUrl (up to ~4.5 sec)
            for (var i = 0; i < 30; i++)
            {
                await Task.Delay(150);

                if (vm.HasLoginUrl)
                {
                    if (vm.CopyLoginUrlCommand?.CanExecute(null) == true)
                        vm.CopyLoginUrlCommand.Execute(null);
                    return;
                }
            }

            MessageBox.Show(
                "Не удалось получить ссылку авторизации. Попробуйте ещё раз.",
                "Авторизация",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch
        {
            // silent; do not break UI
        }
    }

    // ===== Copy logs (XAML: Click="CopyLogs_OnClick") =====
    private void CopyLogs_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (DataContext is not MainViewModel vm)
                return;

            if (vm.LogLines is null || vm.LogLines.Count == 0)
                return;

            var text = string.Join(Environment.NewLine, vm.LogLines);
            Clipboard.SetText(text);
        }
        catch
        {
            // ignore
        }
    }
}
