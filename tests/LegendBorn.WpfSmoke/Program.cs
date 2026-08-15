using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using LegendBorn.Views.Tabs;

internal static class Program
{
    private sealed class ReadOnlyProfileProbe
    {
        public double ProfileXpProgressPercent => 37.5;
    }

    [STAThread]
    private static int Main()
    {
        // This is a separate executable assembly, so LegendBorn's App.xaml relative
        // ResourceDictionary URIs would otherwise resolve against the smoke-test assembly.
        // Load the exact production dictionaries explicitly from LegendBorn.dll.
        var app = new Application();
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

        if (view.FindName("ProfileXpProgressBar") is not ProgressBar progress)
            throw new InvalidOperationException("ProfileXpProgressBar was not found in the profile view namescope.");

        var binding = BindingOperations.GetBinding(progress, RangeBase.ValueProperty)
                      ?? throw new InvalidOperationException("Profile XP progress binding is missing at runtime.");

        if (binding.Mode != BindingMode.OneWay)
            throw new InvalidOperationException($"Profile XP progress binding mode is {binding.Mode}, expected OneWay.");

        var expression = BindingOperations.GetBindingExpression(progress, RangeBase.ValueProperty)
                         ?? throw new InvalidOperationException("Profile XP progress BindingExpression is missing at runtime.");

        expression.UpdateTarget();

        if (Math.Abs(progress.Value - 37.5) > 0.001)
            throw new InvalidOperationException($"Profile XP progress target value is {progress.Value}, expected 37.5.");

        return 0;
    }
}
