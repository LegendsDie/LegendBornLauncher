using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Threading;
using LegendBorn;
using LegendBorn.ViewModels;
using LegendBorn.Views.Tabs;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        Console.WriteLine($"WPF_SMOKE_ENVIRONMENT_VERSION={Environment.Version}");
        Console.WriteLine($"WPF_SMOKE_FRAMEWORK={RuntimeInformation.FrameworkDescription}");

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

            // Regression contract: ProfileXpProgressPercent must never enter WPF's BindingEngine.
            if (BindingOperations.GetBinding(progress, RangeBase.ValueProperty) is not null ||
                BindingOperations.GetBindingExpression(progress, RangeBase.ValueProperty) is not null)
            {
                throw new InvalidOperationException(
                    "Profile XP progress unexpectedly has a WPF BindingExpression; read-only XP must be mirrored manually.");
            }

            if (host.DataContext is not MainViewModel vm)
                throw new InvalidOperationException("Production MainWindow did not expose MainViewModel as DataContext.");

            var applyProgression = typeof(MainViewModel).GetMethod(
                "ApplyProgression",
                BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("MainViewModel.ApplyProgression was not found for smoke setup.");

            // Drive the real ViewModel progression path. ApplyProgression raises
            // PropertyChanged(ProfileXpProgressPercent); ProfileTabView must mirror that into
            // ProgressBar.Value without creating any BindingExpression.
            applyProgression.Invoke(vm, new object[]
            {
                2,
                375L,
                100L,
                37L,
                100L,
                37.5,
                0L
            });

            host.Dispatcher.Invoke(static () => { }, DispatcherPriority.DataBind);
            host.UpdateLayout();

            if (Math.Abs(progress.Value - 37.5) > 0.001)
                throw new InvalidOperationException(
                    $"Profile XP progress target value is {progress.Value}, expected 37.5 after PropertyChanged.");

            if (BindingOperations.GetBindingExpression(progress, RangeBase.ValueProperty) is not null)
                throw new InvalidOperationException(
                    "Profile XP progress gained a WPF BindingExpression after progression update.");

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
