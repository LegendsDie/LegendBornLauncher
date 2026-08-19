// File: Views/Tabs/StartTabView.xaml.cs
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LegendBorn.Services;
using LegendBorn.ViewModels;

namespace LegendBorn.Views.Tabs;

public partial class StartTabView : UserControl
{
    private const int NewsTabIndex = 4;
    private const int NewsPageSize = 3;
    private const string SiteUrlPrimary = "https://legendborn.xyz/";

    private MainViewModel? _dashboardVm;
    private Button? _playActionButton;
    private LauncherNewsService.NewsItem[] _news = Array.Empty<LauncherNewsService.NewsItem>();
    private int _newsPage;
    private CancellationTokenSource? _newsCts;
    private bool _newsLoaded;

    public StartTabView()
    {
        InitializeComponent();
        Loaded += StartTabView_OnLoaded;
        Unloaded += StartTabView_OnUnloaded;
    }

    private void StartTabView_OnLoaded(object sender, RoutedEventArgs e)
    {
        AttachDashboardVm();
        _playActionButton = FindPlayActionButton(this);
        RefreshPlayActionButton();
        RefreshDashboardFriends();
        RefreshSkin3D();
        RefreshServerNick();
        UpdateNewsPage();

        if (!_newsLoaded)
        {
            _newsLoaded = true;
            _newsCts = new CancellationTokenSource();
            _ = LoadNewsAsync(_newsCts.Token);
        }
    }

    private void StartTabView_OnUnloaded(object sender, RoutedEventArgs e)
    {
        DetachDashboardVm();
        _playActionButton = null;
        try { _newsCts?.Cancel(); } catch { }
        try { _newsCts?.Dispose(); } catch { }
        _newsCts = null;
        _newsLoaded = false;
    }

    private void AttachDashboardVm()
    {
        var vm = GetVm();
        if (ReferenceEquals(vm, _dashboardVm)) return;

        DetachDashboardVm();
        _dashboardVm = vm;
        if (_dashboardVm is null) return;

        _dashboardVm.PropertyChanged += DashboardVm_OnPropertyChanged;
        _dashboardVm.Friends.CollectionChanged += Friends_OnCollectionChanged;
    }

    private void DetachDashboardVm()
    {
        if (_dashboardVm is null) return;
        try { _dashboardVm.PropertyChanged -= DashboardVm_OnPropertyChanged; } catch { }
        try { _dashboardVm.Friends.CollectionChanged -= Friends_OnCollectionChanged; } catch { }
        _dashboardVm = null;
    }

    private void DashboardVm_OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => DashboardVm_OnPropertyChanged(sender, e)));
            return;
        }

        if (e.PropertyName is nameof(MainViewModel.SelectedServer) or nameof(MainViewModel.Username))
            RefreshDashboardFriends();

        if (e.PropertyName is nameof(MainViewModel.Profile) or nameof(MainViewModel.SkinPreviewUrl) or nameof(MainViewModel.SelectedSkinKey))
            RefreshSkin3D();

        if (e.PropertyName is nameof(MainViewModel.IsLaunchInProgress)
            or nameof(MainViewModel.IsLaunchCancellationRequested)
            or nameof(MainViewModel.CanCancelLaunch)
            or nameof(MainViewModel.CanPlay)
            or nameof(MainViewModel.CanStop)
            or nameof(MainViewModel.IsBusy))
        {
            RefreshPlayActionButton();
        }
    }

    private void Friends_OnCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (Dispatcher.CheckAccess())
            RefreshDashboardFriends();
        else
            Dispatcher.BeginInvoke(new Action(RefreshDashboardFriends));
    }

    private void RefreshSkin3D()
    {
        var vm = _dashboardVm ?? GetVm();
        Skin3DPreview.SkinUrl = vm?.DashboardSkinUrl;
    }

    private void RefreshServerNick()
    {
        var vm = _dashboardVm ?? GetVm();
        var command = vm?.RefreshServerNickCommand;
        if (command?.CanExecute(null) == true)
            command.Execute(null);
    }

    private void RefreshDashboardFriends()
    {
        var vm = _dashboardVm ?? GetVm();
        if (vm is null)
        {
            DashboardFriendsList.ItemsSource = null;
            DashboardFriendsEmpty.Visibility = Visibility.Visible;
            return;
        }

        var serverId = (vm.SelectedServer?.Id ?? string.Empty).Trim();
        var minecraftNow = vm.Friends
            .Where(f => f.OnlinePlace == MainViewModel.OnlinePlace.Minecraft)
            .Where(f =>
                string.IsNullOrWhiteSpace(serverId) ||
                string.IsNullOrWhiteSpace(f.MinecraftServerId) ||
                string.Equals(f.MinecraftServerId, serverId, StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(3)
            .ToArray();

        MainViewModel.FriendEntry[] selected;
        if (minecraftNow.Length > 0)
        {
            DashboardFriendsTitle.Text = "Сейчас на сервере";
            selected = minecraftNow;
        }
        else
        {
            DashboardFriendsTitle.Text = "Последняя активность друзей";
            selected = vm.Friends
                .OrderByDescending(f => f.LastSeenUtc ?? DateTimeOffset.MinValue)
                .ThenByDescending(f => (int)f.OnlinePlace)
                .ThenBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase)
                .Take(3)
                .ToArray();
        }

        DashboardFriendsList.ItemsSource = selected;
        DashboardFriendsEmpty.Visibility = selected.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task LoadNewsAsync(CancellationToken ct)
    {
        try
        {
            var items = await LauncherNewsService.GetLatestAsync(ct).ConfigureAwait(false);
            await Dispatcher.InvokeAsync(() =>
            {
                _news = items.ToArray();
                _newsPage = 0;
                UpdateNewsPage();
            });
        }
        catch (OperationCanceledException) { }
        catch
        {
            await Dispatcher.InvokeAsync(() =>
            {
                _news = Array.Empty<LauncherNewsService.NewsItem>();
                _newsPage = 0;
                UpdateNewsPage();
            });
        }
    }

    private void UpdateNewsPage()
    {
        if (_news.Length == 0)
        {
            NewsCarouselItems.ItemsSource = null;
            NewsEmptyText.Visibility = Visibility.Visible;
            NewsPageText.Text = string.Empty;
            NewsPrevButton.IsEnabled = false;
            NewsNextButton.IsEnabled = false;
            return;
        }

        var pageCount = Math.Max(1, (int)Math.Ceiling(_news.Length / (double)NewsPageSize));
        _newsPage = Math.Clamp(_newsPage, 0, pageCount - 1);

        NewsCarouselItems.ItemsSource = _news
            .Skip(_newsPage * NewsPageSize)
            .Take(NewsPageSize)
            .ToArray();

        NewsEmptyText.Visibility = Visibility.Collapsed;
        NewsPageText.Text = pageCount > 1 ? $"{_newsPage + 1} / {pageCount}" : string.Empty;
        NewsPrevButton.IsEnabled = _newsPage > 0;
        NewsNextButton.IsEnabled = _newsPage + 1 < pageCount;
    }

    private void NewsPrev_OnClick(object sender, RoutedEventArgs e)
    {
        if (_newsPage <= 0) return;
        _newsPage--;
        UpdateNewsPage();
    }

    private void NewsNext_OnClick(object sender, RoutedEventArgs e)
    {
        var pageCount = Math.Max(1, (int)Math.Ceiling(_news.Length / (double)NewsPageSize));
        if (_newsPage + 1 >= pageCount) return;
        _newsPage++;
        UpdateNewsPage();
    }

    // ===================== XAML handlers =====================

    private void OpenNewsTab_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var vm = GetVm();
            if (vm is not null)
                vm.SelectedMenuIndex = NewsTabIndex;
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

    private void PlayOrStop_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var vm = GetVm();
            if (vm is null) return;

            if (sender is Button button)
                _playActionButton = button;

            if (vm.CanStop)
            {
                if (vm.StopGameCommand?.CanExecute(null) == true)
                    vm.StopGameCommand.Execute(null);
                RefreshPlayActionButton();
                return;
            }

            if (vm.IsLaunchInProgress)
            {
                if (vm.CancelLaunchCommand.CanExecute(null))
                    vm.CancelLaunchCommand.Execute(null);
                RefreshPlayActionButton();
                return;
            }

            if (vm.CancellablePlayCommand.CanExecute(null))
                vm.CancellablePlayCommand.Execute(null);

            RefreshPlayActionButton();
        }
        catch { }
    }

    // ===================== Helpers =====================

    private void RefreshPlayActionButton()
    {
        var button = _playActionButton;
        var vm = _dashboardVm ?? GetVm();
        if (button is null || vm is null)
            return;

        if (vm.CanStop)
        {
            button.Content = "Остановить";
            button.IsEnabled = true;
            return;
        }

        if (vm.IsLaunchInProgress)
        {
            button.Content = vm.LaunchActionText;
            button.IsEnabled = vm.CanCancelLaunch;
            return;
        }

        button.Content = "Играть";
        button.IsEnabled = vm.CanPlay;
    }

    private Button? FindPlayActionButton(DependencyObject root)
    {
        object? targetStyle = null;
        try { targetStyle = TryFindResource("PlayOrStopButtonStyle"); } catch { }

        return FindVisualDescendant<Button>(root, button =>
            targetStyle is not null && ReferenceEquals(button.Style, targetStyle));
    }

    private static T? FindVisualDescendant<T>(DependencyObject root, Func<T, bool> predicate)
        where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < count; index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T typed && predicate(typed))
                return typed;

            var nested = FindVisualDescendant(child, predicate);
            if (nested is not null)
                return nested;
        }

        return null;
    }

    private MainViewModel? GetVm()
        => DataContext as MainViewModel
           ?? Window.GetWindow(this)?.DataContext as MainViewModel;

    private static void TryOpenUrl(string url)
    {
        try
        {
            url = (url ?? "").Trim();
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
                return;

            Process.Start(new ProcessStartInfo
            {
                FileName = uri.AbsoluteUri,
                UseShellExecute = true
            });
        }
        catch { }
    }
}
