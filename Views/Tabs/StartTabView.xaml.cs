// File: Views/Tabs/StartTabView.xaml.cs
using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using LegendBorn.ViewModels;

namespace LegendBorn.Views.Tabs;

public partial class StartTabView : UserControl
{
    private const int NewsTabIndex = 4;

    // В XAML/подписках у тебя встречается ru.legendborn.ru, но он иногда недоступен.
    // Для открытия сайта используем основной домен (ru — резерв, если понадобится позже).
    private const string SiteUrlPrimary = "https://legendborn.ru/";

    public StartTabView()
    {
        InitializeComponent();
    }

    // ===================== XAML handlers =====================

    private void OpenNewsTab_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            // 1) Если VM прямо на DataContext этого таба
            if (DataContext is MainViewModel vm1)
            {
                vm1.SelectedMenuIndex = NewsTabIndex;
                return;
            }

            // 2) Если VM на окне (частый сценарий)
            if (Window.GetWindow(this)?.DataContext is MainViewModel vm2)
            {
                vm2.SelectedMenuIndex = NewsTabIndex;
            }
        }
        catch { }
    }

    private void OpenNewsItem_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is FrameworkElement fe && fe.Tag is string url && !string.IsNullOrWhiteSpace(url))
                TryOpenUrl(url);
        }
        catch { }
    }

    private void OpenSite_OnClick(object sender, RoutedEventArgs e)
    {
        try { TryOpenUrl(SiteUrlPrimary); } catch { }
    }

    // One button: Play OR Stop (логика как в старом MainWindow.xaml.cs)
    private void PlayOrStop_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var vm =
                DataContext as MainViewModel ??
                Window.GetWindow(this)?.DataContext as MainViewModel;

            if (vm == null) return;

            if (vm.CanStop)
            {
                if (vm.StopGameCommand?.CanExecute(null) == true)
                    vm.StopGameCommand.Execute(null);
                return;
            }

            if (vm.PlayCommand?.CanExecute(null) == true)
                vm.PlayCommand.Execute(null);
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
}
