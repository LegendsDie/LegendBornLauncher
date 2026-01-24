// File: Views/Tabs/ProfileTabView.xaml.cs
using System;
using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace LegendBorn.Views.Tabs;

public partial class ProfileTabView : UserControl
{
    private const string SiteUrlPrimary = "https://legendborn.ru/";
    private const string SiteUrlFallback = "https://ru.legendborn.ru/";
    private const int StartTabIndex = 0;

    public ProfileTabView()
    {
        InitializeComponent();
        Loaded += ProfileTabView_Loaded;
        Unloaded += ProfileTabView_Unloaded;
    }

    private void ProfileTabView_Loaded(object sender, RoutedEventArgs e)
    {
        // Авто-обновление при входе на вкладку (дебаунс-метод предпочтительнее)
        try
        {
            var vm = GetHostDataContext();
            if (vm is null) return;

            if (!TryExecuteMethod(vm, "ScheduleFriendsRefresh"))
                TryExecuteCommand(vm, "RefreshFriendsCommand");
        }
        catch { }
    }

    private void ProfileTabView_Unloaded(object sender, RoutedEventArgs e)
    {
        // ничего
    }

    private void OpenSite_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var vm = GetHostDataContext();

            if (vm != null && TryExecuteCommand(vm, "OpenSiteCommand"))
                return;

            TryOpenUrl(SiteUrlPrimary);
        }
        catch { }
    }

    private void Logout_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var vm = GetHostDataContext();
            if (vm == null) return;

            TryExecuteCommand(vm, "SiteLogoutCommand");
        }
        catch { }
    }

    private void OpenStart_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var vm = GetHostDataContext();
            if (vm == null) return;

            if (TryExecuteCommand(vm, "OpenStartCommand"))
                return;

            TrySetIntProperty(vm, "SelectedMenuIndex", StartTabIndex);
        }
        catch { }
    }

    // ===== Двойной клик по другу => открыть профиль =====
    private void FriendsList_OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        try
        {
            if (sender is not ListBox lb || lb.SelectedItem is null) return;
            TryOpenProfileFromItem(lb.SelectedItem);
        }
        catch { }
    }

    // Контекстное меню
    private void OpenFriendProfile_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is not MenuItem mi) return;
            var item = mi.DataContext;
            if (item is null) return;

            TryOpenProfileFromItem(item);
        }
        catch { }
    }

    private void CopyFriendId_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is not MenuItem mi) return;
            var item = mi.DataContext;
            if (item is null) return;

            // пользователям publicId не показываем, но копирование пусть берёт Id (fallback на PublicId если внезапно Id пустой)
            var id = TryGetStringProperty(item, "Id") ?? TryGetStringProperty(item, "PublicId");
            id = (id ?? "").Trim();

            if (id.Length == 0) return;

            Clipboard.SetText(id);
        }
        catch { }
    }

    private static void TryOpenProfileFromItem(object item)
    {
        // Приоритет: Id (то, что понимает /profile/{id}), fallback PublicId
        var id = TryGetStringProperty(item, "Id") ?? TryGetStringProperty(item, "PublicId");
        id = (id ?? "").Trim();
        if (id.Length == 0) return;

        TryOpenProfile(id);
    }

    private static void TryOpenProfile(string id)
    {
        id = (id ?? "").Trim();
        if (id.Length == 0) return;

        var baseUrl = SiteUrlPrimary;
        if (!baseUrl.EndsWith("/")) baseUrl += "/";

        var url = baseUrl + "profile/" + Uri.EscapeDataString(id);

        if (!TryOpenUrl(url))
        {
            var fb = SiteUrlFallback;
            if (!fb.EndsWith("/")) fb += "/";
            TryOpenUrl(fb + "profile/" + Uri.EscapeDataString(id));
        }
    }

    // ===== helpers =====

    private object? GetHostDataContext()
        => DataContext ?? Window.GetWindow(this)?.DataContext;

    private static bool TryExecuteCommand(object vm, string commandPropertyName, object? parameter = null)
    {
        try
        {
            var prop = vm.GetType().GetProperty(commandPropertyName, BindingFlags.Instance | BindingFlags.Public);
            if (prop?.GetValue(vm) is not ICommand cmd)
                return false;

            if (!cmd.CanExecute(parameter))
                return false;

            cmd.Execute(parameter);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryExecuteMethod(object vm, string methodName)
    {
        try
        {
            var m = vm.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (m == null) return false;
            if (m.GetParameters().Length != 0) return false;

            m.Invoke(vm, null);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TrySetIntProperty(object vm, string propertyName, int value)
    {
        try
        {
            var prop = vm.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
            if (prop == null || !prop.CanWrite)
                return false;

            if (prop.PropertyType == typeof(int))
            {
                prop.SetValue(vm, value);
                return true;
            }

            if (prop.PropertyType == typeof(int?))
            {
                prop.SetValue(vm, (int?)value);
                return true;
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    private static string? TryGetStringProperty(object obj, string propertyName)
    {
        try
        {
            var p = obj.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var v = p?.GetValue(obj) as string;
            v = (v ?? "").Trim();
            return v.Length == 0 ? null : v;
        }
        catch
        {
            return null;
        }
    }

    private static bool TryOpenUrl(string url)
    {
        try
        {
            url = (url ?? "").Trim();
            if (url.Length == 0) return false;

            if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return false;

            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
            return true;
        }
        catch
        {
            return false;
        }
    }
}
