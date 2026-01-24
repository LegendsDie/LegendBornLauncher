// File: Views/Tabs/SettingsTabView.xaml.cs
using System;
using System.Collections;
using System.Collections.Specialized;
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

    // ✅ RAM: следим за пересборкой RamOptions и восстанавливаем выбранное значение
    private INotifyCollectionChanged? _ramOptionsNcc;
    private bool _suppressRamSelection;

    public SettingsTabView()
    {
        InitializeComponent();

        Loaded += SettingsTabView_Loaded;
        Unloaded += SettingsTabView_Unloaded;
        DataContextChanged += SettingsTabView_DataContextChanged;

        // UX: автоскролл логов — чекбоксом (не через VM)
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

        HookRamOptions();
        FixRamSelection();

        Dispatcher.BeginInvoke(new Action(() => ScrollLogsToEnd(force: true)));
    }

    private void SettingsTabView_Unloaded(object sender, RoutedEventArgs e)
    {
        UnhookLogsUi();
        UnhookLogsCollection();

        UnhookRamOptions();
    }

    private void SettingsTabView_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        UnhookLogsCollection();
        HookLogsCollection();
        HookLogsUi();

        UnhookRamOptions();
        HookRamOptions();
        FixRamSelection();
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

    private static int? TryGetIntProperty(object vm, string propertyName)
    {
        try
        {
            var prop = vm.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
            if (prop == null) return null;

            var val = prop.GetValue(vm);
            if (val == null) return null;

            // ✅ Надёжно для int / int? / любых числовых типов
            if (val is int i) return i;

            // иногда int? приходит как boxed int (выше уже поймали),
            // но на всякий — пробуем конвертацию
            if (val is IConvertible)
            {
                try { return Convert.ToInt32(val); }
                catch { /* ignore */ }
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    // ===================== RAM (fix: never reset) =====================
    private void HookRamOptions()
    {
        try
        {
            var vm = GetHostDataContext();
            if (vm == null) return;

            var ramOptions = GetPropertyValue(vm, "RamOptions");
            if (ramOptions is INotifyCollectionChanged ncc)
            {
                _ramOptionsNcc = ncc;
                _ramOptionsNcc.CollectionChanged += RamOptions_CollectionChanged;
            }
        }
        catch { }
    }

    private void UnhookRamOptions()
    {
        try
        {
            if (_ramOptionsNcc != null)
                _ramOptionsNcc.CollectionChanged -= RamOptions_CollectionChanged;
        }
        catch { }
        finally
        {
            _ramOptionsNcc = null;
        }
    }

    private void RamOptions_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(FixRamSelection));
    }

    private void FixRamSelection()
    {
        try
        {
            if (RamCombo == null) return;

            var vm = GetHostDataContext();
            if (vm == null) return;

            var ramMb = TryGetIntProperty(vm, "RamMb");
            if (ramMb is null || ramMb.Value <= 0) return;

            var ramOptionsObj = GetPropertyValue(vm, "RamOptions");
            if (ramOptionsObj is IList list)
            {
                // ✅ если выбранное значение отсутствует в списке — добавим
                var exists = false;
                for (int i = 0; i < list.Count; i++)
                {
                    if (list[i] is int v && v == ramMb.Value) { exists = true; break; }
                }

                if (!exists)
                    list.Add(ramMb.Value);
            }

            _suppressRamSelection = true;
            try
            {
                if (RamCombo.SelectedItem is int cur && cur == ramMb.Value)
                    return;

                RamCombo.SelectedItem = ramMb.Value;
            }
            finally
            {
                _suppressRamSelection = false;
            }
        }
        catch { }
    }

    // ✅ Пользователь выбрал значение — записываем в VM вручную
    private void RamCombo_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        try
        {
            if (_suppressRamSelection) return;

            if (sender is not ComboBox cb) return;
            if (cb.SelectedItem is not int mb) return; // null/не int игнорируем

            var vm = GetHostDataContext();
            if (vm == null) return;

            var current = TryGetIntProperty(vm, "RamMb");
            if (current.HasValue && current.Value == mb) return;

            TrySetIntProperty(vm, "RamMb", mb);
        }
        catch { }
    }

    // ===================== Logs collection hook =====================
    private void HookLogsCollection()
    {
        try
        {
            var vm = GetHostDataContext();
            if (vm == null) return;

            var logLines = GetPropertyValue(vm, "LogLines");
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
            if (e.ExtentHeightChange != 0)
                return;

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

            if (TryExecuteCommand(vm, "ClearLogCommand"))
                return;

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
