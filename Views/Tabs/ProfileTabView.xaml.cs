// File: Views/Tabs/ProfileTabView.xaml.cs
using System;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
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
        try
        {
            var vm = GetHostDataContext();
            if (vm is null) return;

            var hasToken = TryGetBoolProperty(vm, "HasSiteToken") ?? false;

            if (hasToken)
            {
                TryExecuteMethod(vm, "StartOnlinePresence");

                if (!TryExecuteMethod(vm, "ScheduleFriendsRefresh"))
                    TryExecuteCommand(vm, "RefreshFriendsCommand");
            }
        }
        catch (Exception ex)
        {
            DebugLog(ex, "ProfileTabView_Loaded");
        }
    }

    private void ProfileTabView_Unloaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var vm = GetHostDataContext();
            if (vm is null) return;

            // ✅ FIX: Unloaded бывает при простом переключении табов.
            // Не гасим presence, если пользователь всё ещё залогинен.
            var isLoggedIn = TryGetBoolProperty(vm, "IsLoggedIn") ?? false;
            var hasToken = TryGetBoolProperty(vm, "HasSiteToken") ?? false;

            if (!isLoggedIn || !hasToken)
                TryExecuteMethod(vm, "StopOnlinePresence");
        }
        catch (Exception ex)
        {
            DebugLog(ex, "ProfileTabView_Unloaded");
        }
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
        catch (Exception ex)
        {
            DebugLog(ex, "OpenSite_OnClick");
        }
    }

    private void Logout_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var vm = GetHostDataContext();
            if (vm == null) return;

            TryExecuteCommand(vm, "SiteLogoutCommand");
        }
        catch (Exception ex)
        {
            DebugLog(ex, "Logout_OnClick");
        }
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
        catch (Exception ex)
        {
            DebugLog(ex, "OpenStart_OnClick");
        }
    }

    // ===== Двойной клик по другу => открыть профиль (ТОЛЬКО по айтему) =====
    private void FriendsList_OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        try
        {
            if (e.ChangedButton != MouseButton.Left) return;
            if (sender is not ListBox lb) return;

            var dep = e.OriginalSource as DependencyObject;
            if (dep is null) return;

            var container = ItemsControl.ContainerFromElement(lb, dep) as ListBoxItem;
            if (container?.DataContext is null) return;

            TryOpenProfileFromItem(container.DataContext);
        }
        catch (Exception ex)
        {
            DebugLog(ex, "FriendsList_OnMouseDoubleClick");
        }
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
        catch (Exception ex)
        {
            DebugLog(ex, "OpenFriendProfile_OnClick");
        }
    }

    private void CopyFriendId_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is not MenuItem mi) return;
            var item = mi.DataContext;
            if (item is null) return;

            var id = GetBestProfileId(item);
            if (string.IsNullOrWhiteSpace(id)) return;

            Clipboard.SetText(id);
        }
        catch (Exception ex)
        {
            DebugLog(ex, "CopyFriendId_OnClick");
        }
    }

    private static void TryOpenProfileFromItem(object item)
    {
        var id = GetBestProfileId(item);
        if (string.IsNullOrWhiteSpace(id)) return;

        if (id.StartsWith("mock-", StringComparison.OrdinalIgnoreCase))
            return;

        TryOpenProfile(id);
    }

    /// <summary>
    /// ✅ Фикс "рандомных айди":
    /// 1) PublicId (если > 0)
    /// 2) Id
    /// 3) UserId (fallback)
    /// </summary>
    private static string? GetBestProfileId(object item)
    {
        // 1) PublicId
        var publicIdRaw =
            TryGetStringProperty(item, "PublicId") ??
            TryGetStringProperty(item, "publicId");

        if (!string.IsNullOrWhiteSpace(publicIdRaw) &&
            int.TryParse(publicIdRaw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid) &&
            pid > 0)
        {
            return pid.ToString(CultureInfo.InvariantCulture);
        }

        // 2) Id
        var id =
            TryGetStringProperty(item, "Id") ??
            TryGetStringProperty(item, "id");

        id = (id ?? "").Trim();
        if (!string.IsNullOrWhiteSpace(id))
            return id;

        // 3) UserId fallback
        var userId =
            TryGetStringProperty(item, "UserId") ??
            TryGetStringProperty(item, "userId");

        userId = (userId ?? "").Trim();
        if (!string.IsNullOrWhiteSpace(userId))
            return userId;

        return null;
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

    private static bool? TryGetBoolProperty(object obj, string propertyName)
    {
        try
        {
            var p = obj.GetType().GetProperty(propertyName,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            if (p is null) return null;

            var vObj = p.GetValue(obj);
            if (vObj is null) return null;

            if (vObj is bool b) return b;

            if (vObj is string s && bool.TryParse(s, out var parsed)) return parsed;
            if (vObj is int i) return i != 0;

            return null;
        }
        catch
        {
            return null;
        }
    }

    private static string? TryGetStringProperty(object obj, string propertyName)
    {
        try
        {
            var p = obj.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var vObj = p?.GetValue(obj);
            if (vObj is null) return null;

            var v = vObj.ToString();
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

            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
                return false;

            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
                return false;

            Process.Start(new ProcessStartInfo
            {
                FileName = uri.AbsoluteUri,
                UseShellExecute = true
            });

            return true;
        }
        catch
        {
            return false;
        }
    }

    [Conditional("DEBUG")]
    private static void DebugLog(Exception ex, string where)
    {
        try
        {
            Debug.WriteLine($"[{where}] {ex}");
        }
        catch { }
    }
}

/// <summary>
/// ✅ Формирует строку: онлайн/офлайн + где/когда был.
/// values:
/// [0] OnlinePlace (Site/Launcher/Offline)
/// [1] LastSeenAt (DateTime / string / null)
/// [2] LastSeenSource (Site/Launcher / null)
/// [3] PresenceLine fallback (string)
/// </summary>
public sealed class FriendPresenceLineConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        var onlinePlace = SafeStr(values, 0);
        var lastSeenAt = values.Length > 1 ? values[1] : null;
        var lastSeenSource = SafeStr(values, 2);
        var fallback = SafeStr(values, 3);

        var place = (onlinePlace ?? "").Trim();

        // Онлайн
        if (IsPlace(place, "Site"))
            return "онлайн • на сайте";

        if (IsPlace(place, "Launcher"))
            return "онлайн • в лаунчере";

        // Офлайн: попробуем собрать "был в ... dd.MM HH:mm"
        var dt = TryParseDate(lastSeenAt);
        var src = (lastSeenSource ?? "").Trim();

        if (dt.HasValue)
        {
            var when = dt.Value.ToLocalTime().ToString("dd.MM HH:mm", CultureInfo.GetCultureInfo("ru-RU"));
            if (IsPlace(src, "Launcher")) return $"был в лаунчере {when}";
            if (IsPlace(src, "Site")) return $"был на сайте {when}";
            return $"был {when}";
        }

        // fallback: если у тебя раньше приходила строка
        if (!string.IsNullOrWhiteSpace(fallback))
        {
            var f = fallback.Trim();
            if (!f.Equals("НЕ В СЕТИ", StringComparison.OrdinalIgnoreCase) &&
                !f.Equals("Не в сети", StringComparison.OrdinalIgnoreCase))
                return f;

            return "оффлайн";
        }

        return "оффлайн";
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();

    private static bool IsPlace(string? v, string expected)
        => string.Equals((v ?? "").Trim(), expected, StringComparison.OrdinalIgnoreCase);

    private static string? SafeStr(object[] values, int i)
    {
        if (values == null || i < 0 || i >= values.Length) return null;
        var v = values[i];
        if (v == null || v == DependencyProperty.UnsetValue) return null;
        return v.ToString();
    }

    private static DateTime? TryParseDate(object? v)
    {
        if (v == null || v == DependencyProperty.UnsetValue) return null;

        if (v is DateTime dt) return dt;

        var s = v.ToString();
        if (string.IsNullOrWhiteSpace(s)) return null;

        if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
            return parsed;

        if (DateTime.TryParse(s, CultureInfo.GetCultureInfo("ru-RU"), DateTimeStyles.None, out parsed))
            return parsed;

        return null;
    }
}
