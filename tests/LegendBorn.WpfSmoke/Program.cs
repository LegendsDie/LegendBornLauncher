using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Threading;
using LegendBorn;
using LegendBorn.Views.Tabs;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        // Make pack://application:,,,/... resolve exactly as it does when LegendBorn.exe
        // is the entry assembly, even though this smoke test is a separate executable.
        Application.ResourceAssembly = typeof(MainWindow).Assembly;

        var app = new Application
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown
        };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri(
                "pack://application:,,,/LegendBorn;component/Resources/Themes/LauncherTheme.xaml",
                UriKind.Absolute)
        });
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri(
                "pack://application:,,,/LegendBorn;component/Resources/Themes/MainWindowLocal.xaml",
                UriKind.Absolute)
        });

        // Use the real production window and real MainViewModel. This reproduces the exact
        // DataContext inheritance path used by MainWindow -> TabControl -> ProfileTabView.
        var host = new MainWindow
        {
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.None
        };

        try
        {
            host.Show();
            host.UpdateLayout();
            host.Dispatcher.Invoke(static () => { }, DispatcherPriority.DataBind);

            var profileView = FindLogicalDescendant<ProfileTabView>(host)
                              ?? throw new InvalidOperationException(
                                  "ProfileTabView was not found inside the production MainWindow logical tree.");

            if (profileView.FindName("ProfileXpProgressBar") is not ProgressBar progress)
                throw new InvalidOperationException(
                    "ProfileXpProgressBar was not found in the production profile view namescope.");

            var binding = BindingOperations.GetBinding(progress, RangeBase.ValueProperty)
                          ?? throw new InvalidOperationException(
                              "Profile XP progress binding is missing at runtime.");

            if (binding.Mode != BindingMode.OneWay)
                throw new InvalidOperationException(
                    $"Profile XP progress binding mode is {binding.Mode}, expected OneWay.");

            if (!string.Equals(
                    binding.Path?.Path,
                    "ProfileXpProgressPercent",
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Profile XP progress binding path is '{binding.Path?.Path}', expected ProfileXpProgressPercent.");
            }

            var expression = BindingOperations.GetBindingExpression(progress, RangeBase.ValueProperty)
                             ?? throw new InvalidOperationException(
                                 "Profile XP progress BindingExpression is missing at runtime.");

            // Force the same binding/layout work that was present in the real crash stack.
            expression.UpdateTarget();
            host.UpdateLayout();
            host.Dispatcher.Invoke(static () => { }, DispatcherPriority.DataBind);

            return 0;
        }
        finally
        {
            host.Close();
            app.Shutdown();
        }
    }

    private static T? FindLogicalDescendant<T>(DependencyObject root)
        where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is T match)
                return match;

            if (child is not DependencyObject dependencyObject)
                continue;

            var nested = FindLogicalDescendant<T>(dependencyObject);
            if (nested is not null)
                return nested;
        }

        return null;
    }
}
