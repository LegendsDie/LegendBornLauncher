using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Threading;
using LegendBorn.Views.Tabs;

public sealed class ReadOnlyProfileProbe
{
    public double ProfileXpProgressPercent => 37.5;
}

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        // This is a separate executable assembly, so LegendBorn's App.xaml relative
        // ResourceDictionary URIs would otherwise resolve against the smoke-test assembly.
        // Load the exact production dictionaries explicitly from LegendBorn.dll.
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

        var view = new ProfileTabView
        {
            DataContext = new ReadOnlyProfileProbe()
        };

        var host = new Window
        {
            Width = 900,
            Height = 700,
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.None,
            Content = view
        };

        try
        {
            // Reproduce the production lifecycle that originally crashed: attach the view to
            // a real Window, create layout, and let WPF process DataBind-priority work.
            host.Show();
            host.UpdateLayout();
            host.Dispatcher.Invoke(static () => { }, DispatcherPriority.DataBind);

            if (view.FindName("ProfileXpProgressBar") is not ProgressBar progress)
                throw new InvalidOperationException("ProfileXpProgressBar was not found in the profile view namescope.");

            var binding = BindingOperations.GetBinding(progress, RangeBase.ValueProperty)
                          ?? throw new InvalidOperationException("Profile XP progress binding is missing at runtime.");

            if (binding.Mode != BindingMode.OneWay)
                throw new InvalidOperationException($"Profile XP progress binding mode is {binding.Mode}, expected OneWay.");

            var expression = BindingOperations.GetBindingExpression(progress, RangeBase.ValueProperty)
                             ?? throw new InvalidOperationException("Profile XP progress BindingExpression is missing at runtime.");

            expression.UpdateTarget();
            host.Dispatcher.Invoke(static () => { }, DispatcherPriority.DataBind);

            if (Math.Abs(progress.Value - 37.5) > 0.001)
                throw new InvalidOperationException($"Profile XP progress target value is {progress.Value}, expected 37.5.");

            return 0;
        }
        finally
        {
            host.Close();
            app.Shutdown();
        }
    }
}
