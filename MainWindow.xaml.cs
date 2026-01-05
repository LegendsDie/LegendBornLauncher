using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace LegendBorn;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainViewModel();
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