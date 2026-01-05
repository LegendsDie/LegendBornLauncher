using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace LegendBorn;

public partial class MainWindow : Window
{
    private bool _updatesChecked;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainViewModel();

        // Запускаем проверку обновлений после загрузки окна
        Loaded += MainWindow_OnLoaded;
    }

    private async void MainWindow_OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_updatesChecked)
            return;

        _updatesChecked = true;

        // Тихая проверка обновлений (без MessageBox)
        await UpdateService.CheckAndUpdateAsync(silent: true);
    }

    private void Close_OnClick(object sender, RoutedEventArgs e) => Close();

    private void Minimize_OnClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void TitleBar_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
            return;

        // если клик по кнопке/интерактиву — не начинаем DragMove
        if (IsClickOnInteractive(e.OriginalSource as DependencyObject))
            return;

        // double click = maximize/restore
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