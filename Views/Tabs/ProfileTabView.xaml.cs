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
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using LegendBorn.ViewModels;

namespace LegendBorn.Views.Tabs;

public partial class ProfileTabView : UserControl
{
    private const string SiteBaseUrl = "https://legendborn.xyz";

    // The legacy TabControl header has proved visually unstable across real DPI/scaling settings.
    // Keep TabControl only as a content selector and render navigation ourselves as a segmented
    // section bar. The hidden TabPanel keeps WPF item generation conventional, but users never see it.
    private const string ProfileContentOnlyTemplateXaml = """
        <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                         xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                         TargetType="{x:Type TabControl}">
            <Grid>
                <TabPanel IsItemsHost="True"
                          Visibility="Collapsed"/>
                <ContentPresenter ContentSource="SelectedContent"
                                  SnapsToDevicePixels="True"/>
            </Grid>
        </ControlTemplate>
        """;

    private const string SectionButtonTemplateXaml = """
        <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                         xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                         TargetType="{x:Type ToggleButton}">
            <Border x:Name="Body"
                    Margin="2"
                    Padding="14,8"
                    CornerRadius="9"
                    Background="Transparent"
                    BorderBrush="Transparent"
                    BorderThickness="1">
                <ContentPresenter HorizontalAlignment="Center"
                                  VerticalAlignment="Center"/>
            </Border>
            <ControlTemplate.Triggers>
                <Trigger Property="IsMouseOver" Value="True">
                    <Setter TargetName="Body" Property="Background" Value="#551A1730"/>
                    <Setter TargetName="Body" Property="BorderBrush" Value="#4E3A68"/>
                </Trigger>
                <Trigger Property="IsChecked" Value="True">
                    <Setter Property="Foreground" Value="#F0E4FF"/>
                    <Setter TargetName="Body" Property="Background" Value="#A12A1746"/>
                    <Setter TargetName="Body" Property="BorderBrush" Value="#8A57BD"/>
                </Trigger>
                <Trigger Property="IsEnabled" Value="False">
                    <Setter Property="Opacity" Value="0.5"/>
                </Trigger>
            </ControlTemplate.Triggers>
        </ControlTemplate>
        """;

    private MainViewModel? _profileProgressOwner;
    private Grid? _profileSectionHost;
    private UniformGrid? _profileSectionBar;
    private readonly List<ToggleButton> _profileSectionButtons = new();

    public ProfileTabView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        ProfileTabs.SelectionChanged += ProfileTabs_OnSelectionChanged;
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
        ApplyProfileSectionLayout();
        ApplyCompactCurrencyLabel();

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

    private void ApplyProfileSectionLayout()
    {
        try
        {
            if (XamlReader.Parse(ProfileContentOnlyTemplateXaml) is ControlTemplate contentOnlyTemplate)
                ProfileTabs.Template = contentOnlyTemplate;

            // Preserve a generous hidden header margin for the existing WPF regression contract;
            // the real section selector below does not depend on TabItem geometry at all.
            var tabItems = ProfileTabs.Items.OfType<TabItem>().ToArray();
            for (var i = 0; i < tabItems.Length; i++)
            {
                tabItems[i].Margin = i == tabItems.Length - 1
                    ? new Thickness(0)
                    : new Thickness(0, 0, 14, 0);
            }

            if (_profileSectionHost is null && ProfileTabs.Parent is Grid parent)
            {
                var row = Grid.GetRow(ProfileTabs);
                var column = Grid.GetColumn(ProfileTabs);
                var rowSpan = Grid.GetRowSpan(ProfileTabs);
                var columnSpan = Grid.GetColumnSpan(ProfileTabs);

                parent.Children.Remove(ProfileTabs);

                var host = new Grid
                {
                    SnapsToDevicePixels = true,
                    UseLayoutRounding = true
                };
                host.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                host.RowDefinitions.Add(new RowDefinition { Height = new GridLength(14) });
                host.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

                var navigationFrame = new Border
                {
                    Width = 340,
                    Height = 46,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Top,
                    Padding = new Thickness(3),
                    CornerRadius = new CornerRadius(13),
                    Background = Brush("#76090C16"),
                    BorderBrush = Brush("#453258"),
                    BorderThickness = new Thickness(1),
                    SnapsToDevicePixels = true,
                    UseLayoutRounding = true
                };

                var navigation = new UniformGrid
                {
                    Rows = 1,
                    Columns = Math.Max(1, ProfileTabs.Items.Count),
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    VerticalAlignment = VerticalAlignment.Stretch
                };
                navigationFrame.Child = navigation;

                Grid.SetRow(navigationFrame, 0);
                host.Children.Add(navigationFrame);

                Grid.SetRow(ProfileTabs, 2);
                Grid.SetColumn(ProfileTabs, 0);
                Grid.SetRowSpan(ProfileTabs, 1);
                Grid.SetColumnSpan(ProfileTabs, 1);
                host.Children.Add(ProfileTabs);

                Grid.SetRow(host, row);
                Grid.SetColumn(host, column);
                Grid.SetRowSpan(host, rowSpan);
                Grid.SetColumnSpan(host, columnSpan);
                parent.Children.Add(host);

                _profileSectionHost = host;
                _profileSectionBar = navigation;
            }

            RebuildProfileSectionButtons();
            ProfileTabs.ApplyTemplate();
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
    }

    private void RebuildProfileSectionButtons()
    {
        var bar = _profileSectionBar;
        if (bar is null)
            return;

        var tabs = ProfileTabs.Items.OfType<TabItem>().ToArray();
        if (tabs.Length == 0)
            return;

        if (_profileSectionButtons.Count == tabs.Length && bar.Children.Count == tabs.Length)
        {
            bar.Columns = tabs.Length;
            SyncProfileSectionButtons();
            return;
        }

        bar.Children.Clear();
        _profileSectionButtons.Clear();
        bar.Columns = tabs.Length;

        ControlTemplate? template = null;
        try
        {
            template = XamlReader.Parse(SectionButtonTemplateXaml) as ControlTemplate;
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }

        for (var index = 0; index < tabs.Length; index++)
        {
            var tab = tabs[index];
            var button = new ToggleButton
            {
                Content = tab.Header,
                Tag = index,
                Height = 38,
                Padding = new Thickness(12, 0, 12, 0),
                Margin = new Thickness(0),
                Foreground = Brush("#AAB4C8"),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Cursor = Cursors.Hand,
                FocusVisualStyle = null,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Stretch
            };

            if (template is not null)
                button.Template = template;

            button.Click += ProfileSectionButton_OnClick;
            _profileSectionButtons.Add(button);
            bar.Children.Add(button);
        }

        SyncProfileSectionButtons();
    }

    private void ProfileSectionButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton { Tag: int index })
            return;

        if (index >= 0 && index < ProfileTabs.Items.Count)
            ProfileTabs.SelectedIndex = index;

        SyncProfileSectionButtons();
    }

    private void ProfileTabs_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(sender, ProfileTabs))
            return;

        SyncProfileSectionButtons();
    }

    private void SyncProfileSectionButtons()
    {
        var selected = ProfileTabs.SelectedIndex;
        for (var i = 0; i < _profileSectionButtons.Count; i++)
            _profileSectionButtons[i].IsChecked = i == selected;
    }

    private void ApplyCompactCurrencyLabel()
    {
        // The icon will replace this abbreviation later. Until then keep the presentation compact
        // even though the legacy XAML still contains the previous label.
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            try
            {
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

    private static Brush Brush(string value)
    {
        var converter = new BrushConverter();
        var brush = converter.ConvertFromString(value) as Brush ?? Brushes.Transparent;
        if (brush.CanFreeze)
            brush.Freeze();
        return brush;
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
