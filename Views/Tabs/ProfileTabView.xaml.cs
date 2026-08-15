using System;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using LegendBorn.ViewModels;

namespace LegendBorn.Views.Tabs;

public partial class ProfileTabView : UserControl
{
    private const string SiteBaseUrl = "https://legendborn.xyz";

    public ProfileTabView()
    {
        InitializeComponent();
        InstallProfileProgressBinding();
        Loaded += OnLoaded;
    }

    /// <summary>
    /// RangeBase.Value binds TwoWay by default. Keep the XP presentation property read-only
    /// and install its display-only binding in code so compiled BAML never owns a binding that
    /// can fall back to RangeBase.Value's TwoWay default during runtime attachment.
    /// </summary>
    private void InstallProfileProgressBinding()
    {
        BindingOperations.SetBinding(
            ProfileXpProgressBar,
            RangeBase.ValueProperty,
            new Binding(nameof(MainViewModel.ProfileXpProgressPercent))
            {
                Mode = BindingMode.OneWay
            });

        var installed = BindingOperations.GetBinding(ProfileXpProgressBar, RangeBase.ValueProperty);
        if (installed?.Mode != BindingMode.OneWay)
            throw new InvalidOperationException("Profile XP progress binding was not installed as OneWay.");
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm)
            return;

        // Once profile-only account data may be loaded, keep a logout observer for the lifetime
        // of this launcher window so a second account can never inherit the first account's UI state.
        vm.EnsureProfileExperienceAccountScope();

        if (!vm.IsLoggedIn)
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
