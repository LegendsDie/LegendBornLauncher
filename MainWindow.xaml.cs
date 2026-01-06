using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using LegendBorn.Services; // UpdateService в LegendBorn.Services

namespace LegendBorn;

public partial class MainWindow : Window
{
    private bool _updatesChecked;
    private bool _isClosing;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainViewModel();

        Loaded += MainWindow_OnLoaded;
        Closing += (_, __) => _isClosing = true;
    }

    private void MainWindow_OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_updatesChecked)
            return;

        _updatesChecked = true;

        // В фоне, чтобы не подвисал UI
        _ = RunUpdateCheckSafeAsync();
    }

    private async Task RunUpdateCheckSafeAsync()
    {
        try
        {
            if (_isClosing)
                return;

            // ✅ На старте НЕ показываем "обновлений нет", но если обновление есть — спросим
            await UpdateService.CheckAndUpdateAsync(silent: false, showNoUpdates: false);
        }
        catch
        {
            // Любая ошибка апдейта не должна ломать запуск лаунчера.
        }
    }

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
            d = VisualTreeHelper.GetParent(d);
        }
        return false;
    }
}
