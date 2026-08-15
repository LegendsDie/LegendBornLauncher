using System;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LegendBorn.ViewModels;

namespace LegendBorn.Views.Tabs;

public partial class ProfileTabView : UserControl
{
    private const string SiteBaseUrl = "https://legendborn.xyz";

    public ProfileTabView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm || !vm.IsLoggedIn)
            return;

        var command = vm.RefreshProfileExperienceCommand;
        if (command.CanExecute(null))
            command.Execute(null);
    }

    private void OpenSite_OnClick(object sender, RoutedEventArgs e)
        => OpenUrl(SiteBaseUrl + "/profile");

    private void Logout_OnClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm)
            return;
        if (vm.SiteLogoutCommand.CanExecute(null))
            vm.SiteLogoutCommand.Execute(null);
    }

    private void FriendsList_OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || sender is not ListBox list)
            return;

        var source = e.OriginalSource as DependencyObject;
        if (source is null)
            return;

        var container = ItemsControl.ContainerFromElement(list, source) as ListBoxItem;
        if (container?.DataContext is not MainViewModel.FriendEntry friend)
            return;

        var id = ResolveProfileId(friend);
        if (id.Length == 0 || id.StartsWith("mock-", StringComparison.OrdinalIgnoreCase))
            return;

        OpenUrl(SiteBaseUrl + "/profile/" + Uri.EscapeDataString(id));
    }

    private static string ResolveProfileId(MainViewModel.FriendEntry friend)
    {
        if (friend.PublicId is > 0)
            return friend.PublicId.Value.ToString(CultureInfo.InvariantCulture);

        var userId = (friend.UserId ?? string.Empty).Trim();
        if (userId.Length > 0) return userId;

        var internalId = (friend.InternalId ?? string.Empty).Trim();
        if (internalId.Length > 0) return internalId;

        return (friend.Id ?? string.Empty).Trim();
    }

    private static void OpenUrl(string url)
    {
        try
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
                return;

            Process.Start(new ProcessStartInfo
            {
                FileName = uri.AbsoluteUri,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
    }
}
