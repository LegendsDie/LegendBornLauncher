// File: Views/Tabs/SettingsTabView.xaml.cs
using System;
using System.Collections;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace LegendBorn.Views.Tabs;

public partial class SettingsTabView : UserControl
{
    private const int CopyLogsMaxLines = 120;
    private const int StartTabIndex = 0;

    private bool _logAutoScroll = true;

    private INotifyCollectionChanged? _logLinesNcc;
    private ScrollChangedEventHandler? _logScrollHandler;

    public SettingsTabView()
    {
        InitializeComponent();

        Loaded += SettingsTabView_Loaded;
        Unloaded += SettingsTabView_Unloaded;
        DataContextChanged += SettingsTabView_DataContextChanged;

        // UX: автоскролл контролируем чекбоксом (не через VM)
        if (RootAutoScroll != null)
        {
            RootAutoScroll.Checked += (_, __) => _logAutoScroll = true;
            RootAutoScroll.Unchecked += (_, __) => _logAutoScroll = false;
        }
    }

    private void SettingsTabView_Loaded(object sender, RoutedEventArgs e)
    {
        HookLogsCollection();
        HookLogsUi();

        Dispatcher.BeginInvoke(new Action(() => ScrollLogsToEnd(force: true)));
    }

    private void SettingsTabView_Unloaded(object sender, RoutedEventArgs e)
    {
        UnhookLogsUi();
        UnhookLogsCollection();
    }

    private void SettingsTabView_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        // DataContext мог смениться — переподцепим коллекцию логов.
        UnhookLogsCollection();
        HookLogsCollection();
        HookLogsUi();
    }

    // ===================== Data access (no hard dependency on MainViewModel) =====================
    private object? GetHostDataContext()
        => DataContext ?? Window.GetWindow(this)?.DataContext;

    private static object? GetPropertyValue(object vm, string propertyName)
        => vm.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public)?.GetValue(vm);

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

    // ===================== Logs collection hook =====================
    private void HookLogsCollection()
    {
        try
        {
            var vm = GetHostDataContext();
            if (vm == null) return;

            var logLines = GetPropertyValue(vm, "LogLines");

            // LogLines должен быть коллекцией. Если ещё и INotifyCollectionChanged — подпишемся.
            if (logLines is INotifyCollectionChanged ncc)
            {
                _logLinesNcc = ncc;
                _logLinesNcc.CollectionChanged += LogLines_CollectionChanged;
            }
        }
        catch { }
    }

    private void UnhookLogsCollection()
    {
        try
        {
            if (_logLinesNcc != null)
                _logLinesNcc.CollectionChanged -= LogLines_CollectionChanged;
        }
        catch { }
        finally
        {
            _logLinesNcc = null;
        }
    }

    // ===================== Logs UI (autoscroll) =====================
    private void HookLogsUi()
    {
        try
        {
            if (LogListBox == null) return;

            if (_logScrollHandler == null)
                _logScrollHandler = LogScrollViewer_ScrollChanged;

            LogListBox.RemoveHandler(ScrollViewer.ScrollChangedEvent, _logScrollHandler);
            LogListBox.AddHandler(ScrollViewer.ScrollChangedEvent, _logScrollHandler);
        }
        catch { }
    }

    private void UnhookLogsUi()
    {
        try
        {
            if (LogListBox != null && _logScrollHandler != null)
                LogListBox.RemoveHandler(ScrollViewer.ScrollChangedEvent, _logScrollHandler);
        }
        catch { }
    }

    private void LogScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        try
        {
            // Если меняется Extent — это добавили строки (не пользовательский скролл)
            if (e.ExtentHeightChange != 0)
                return;

            // Если пользователь вручную ушёл вверх — отключаем автоскролл, но синхронизируем UI чекбокса.
            var bottom = Math.Max(0, e.ExtentHeight - e.ViewportHeight);
            var follow = e.VerticalOffset >= bottom - 1.0;

            _logAutoScroll = follow;
            if (RootAutoScroll != null)
                RootAutoScroll.IsChecked = follow;
        }
        catch { }
    }

    private void LogLines_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (!_logAutoScroll) return;
        Dispatcher.BeginInvoke(new Action(() => ScrollLogsToEnd(force: false)));
    }

    private void ScrollLogsToEnd(bool force)
    {
        try
        {
            if (LogListBox == null) return;

            var count = LogListBox.Items.Count;
            if (count <= 0) return;

            if (!force && !_logAutoScroll)
                return;

            LogListBox.ScrollIntoView(LogListBox.Items[count - 1]);
        }
        catch { }
    }

    // ===================== XAML handlers =====================
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

    private void ClearLogs_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var vm = GetHostDataContext();
            if (vm == null) return;

            // Если есть ClearLogCommand — используем
            if (TryExecuteCommand(vm, "ClearLogCommand"))
                return;

            // Fallback: если LogLines — IList, чистим напрямую
            var logLines = GetPropertyValue(vm, "LogLines");
            if (logLines is IList list)
                list.Clear();
        }
        catch { }
    }

    private void CopyLogs_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var vm = GetHostDataContext();
            if (vm == null) return;

            var logLinesObj = GetPropertyValue(vm, "LogLines");
            if (logLinesObj is not IEnumerable enumerable) return;

            var lines = enumerable.Cast<object>()
                                  .Select(x => x?.ToString() ?? "")
                                  .Where(s => !string.IsNullOrWhiteSpace(s))
                                  .ToList();

            if (lines.Count == 0) return;

            var tail = lines.Count <= CopyLogsMaxLines
                ? lines
                : lines.Skip(Math.Max(0, lines.Count - CopyLogsMaxLines)).ToList();

            var sb = new StringBuilder();
            sb.AppendLine($"LegendBorn Launcher Logs (last {tail.Count})");
            sb.AppendLine($"Copied at: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine(new string('-', 46));

            foreach (var l in tail)
                sb.AppendLine(l);

            Clipboard.SetText(sb.ToString());
        }
        catch { }
    }
}
