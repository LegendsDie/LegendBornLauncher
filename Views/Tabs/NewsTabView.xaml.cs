// File: Views/Tabs/NewsTabView.xaml.cs
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using LegendBorn.ViewModels;

namespace LegendBorn.Views.Tabs;

public partial class NewsTabView : UserControl
{
    private const string SiteUrlPrimary = "https://legendborn.ru/";
    private const int StartTabIndex = 0;

    public NewsTabView()
    {
        InitializeComponent();
    }

    private void OpenSite_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            TryOpenUrl(SiteUrlPrimary);
        }
        catch { }
    }

    private void OpenStart_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var vm = GetVm();

            // Если команда есть — используем её (она у тебя есть в MainWindow.xaml).
            if (vm?.OpenStartCommand?.CanExecute(null) == true)
            {
                vm.OpenStartCommand.Execute(null);
                return;
            }

            // Fallback: переключаем таб вручную.
            if (vm != null)
                vm.SelectedMenuIndex = StartTabIndex;
        }
        catch { }
    }

    private void OpenNewsItem_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            // Основной путь: Tag содержит Url.
            if (sender is FrameworkElement fe && fe.Tag is string url && !string.IsNullOrWhiteSpace(url))
            {
                TryOpenUrl(url);
                return;
            }

            // Fallback: пробуем взять Url из DataContext элемента.
            if (sender is FrameworkElement fe2 && fe2.DataContext != null)
            {
                var p = fe2.DataContext.GetType().GetProperty("Url");
                if (p?.GetValue(fe2.DataContext) is string u && !string.IsNullOrWhiteSpace(u))
                    TryOpenUrl(u);
            }
        }
        catch { }
    }

    private MainViewModel? GetVm()
        => DataContext as MainViewModel
           ?? Window.GetWindow(this)?.DataContext as MainViewModel;

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
}
