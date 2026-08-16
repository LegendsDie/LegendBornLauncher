using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using LegendBorn.ViewModels;

namespace LegendBorn.Views.Tabs;

public partial class ProfileTabView : UserControl
{
    private const string SiteBaseUrl = "https://legendborn.xyz";
    private MainViewModel? _profileProgressOwner;

    public ProfileTabView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    /// <summary>
    /// ProfileXpProgressPercent is intentionally read-only server-owned presentation state.
    /// RangeBase.Value binds TwoWay by default, and the real launcher has reproduced a WPF
    /// read-only-source crash even after an explicit OneWay binding was installed. Do not put
    /// this property through the WPF binding engine at all: mirror it into ProgressBar.Value
    /// from MainViewModel.PropertyChanged instead.
    /// </summary>
    private void AttachProfileProgressOwner(MainViewModel? owner)
    {
        if (ReferenceEquals(_profileProgressOwner, owner))
        {
            UpdateProfileProgressValue();
            return;
        }

        if (_profileProgressOwner is not null)
            _profileProgressOwner.PropertyChanged -= OnProfileProgressPropertyChanged;

        _profileProgressOwner = owner;

        if (_profileProgressOwner is not null)
            _profileProgressOwner.PropertyChanged += OnProfileProgressPropertyChanged;

        UpdateProfileProgressValue();
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        => AttachProfileProgressOwner(e.NewValue as MainViewModel);

    private void OnProfileProgressPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!string.IsNullOrEmpty(e.PropertyName) &&
            !string.Equals(e.PropertyName, nameof(MainViewModel.ProfileXpProgressPercent), StringComparison.Ordinal))
        {
            return;
        }

        UpdateProfileProgressValue();
    }

    private void UpdateProfileProgressValue()
    {
        void Apply()
        {
            var value = _profileProgressOwner?.ProfileXpProgressPercent ?? 0.0;
            ProfileXpProgressBar.Value = Math.Clamp(value, 0.0, 100.0);
        }

        if (Dispatcher.CheckAccess())
        {
            Apply();
            return;
        }

        _ = Dispatcher.BeginInvoke(DispatcherPriority.DataBind, (Action)Apply);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplyProfileVisualPolish();

        if (DataContext is not MainViewModel vm)
        {
            AttachProfileProgressOwner(null);
            return;
        }

        AttachProfileProgressOwner(vm);

        // Once profile-only account data may be loaded, keep a logout observer for the lifetime
        // of this launcher window so a second account can never inherit the first account's UI state.
        vm.EnsureProfileExperienceAccountScope();

        if (!vm.IsLoggedIn)
            return;

        RefreshAll(vm);
    }

    /// <summary>
    /// PR21 introduced a shared rail around the two profile tabs. On the real launcher DPI/layout
    /// this made the buttons visually merge into one control. Strip only that rail at runtime and
    /// keep the individual TabItem templates, which gives Status/Community their own clear pills.
    /// This is applied after the template is materialized so it behaves identically at any DPI.
    /// </summary>
    private void ApplyProfileVisualPolish()
    {
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            try
            {
                ProfileTabs.ApplyTemplate();

                var tabs = ProfileTabs.Items.OfType<TabItem>().ToArray();
                for (var i = 0; i < tabs.Length; i++)
                {
                    tabs[i].MinWidth = 90;
                    tabs[i].Margin = i == tabs.Length - 1
                        ? new Thickness(0)
                        : new Thickness(0, 0, 12, 0);
                }

                var panel = FindVisualDescendant<TabPanel>(ProfileTabs);
                if (panel is not null)
                {
                    panel.Margin = new Thickness(0);

                    DependencyObject? parent = VisualTreeHelper.GetParent(panel);
                    while (parent is not null && !ReferenceEquals(parent, ProfileTabs))
                    {
                        if (parent is Border rail)
                        {
                            rail.Padding = new Thickness(0);
                            rail.BorderThickness = new Thickness(0);
                            rail.Background = Brushes.Transparent;
                            break;
                        }

                        parent = VisualTreeHelper.GetParent(parent);
                    }
                }

                // Keep the compact currency label until the dedicated RZN icon lands.
                foreach (var text in FindVisualDescendants<TextBlock>(this))
                {
                    if (string.Equals(text.Text?.Trim(), "РЕЗОН", StringComparison.OrdinalIgnoreCase))
                        text.Text = "РЕЗ  ";
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
            }
        }));
    }

    private static T? FindVisualDescendant<T>(DependencyObject root)
        where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
                return match;

            var nested = FindVisualDescendant<T>(child);
            if (nested is not null)
                return nested;
        }

        return null;
    }

    private static IEnumerable<T> FindVisualDescendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
                yield return match;

            foreach (var nested in FindVisualDescendants<T>(child))
                yield return nested;
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
        => AttachProfileProgressOwner(null);

    private static void RefreshAll(MainViewModel vm)
    {
        var profile = vm.RefreshProfileExperienceCommand;
        if (profile.CanExecute(null))
            profile.Execute(null);

        var minecraft = vm.RefreshMinecraftStatusCommand;
        if (minecraft.CanExecute(null))
            minecraft.Execute(null);
    }

    private void RefreshAll_OnClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
            RefreshAll(vm);
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
