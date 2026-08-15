using System;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using LegendBorn;
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
        var app = new App();
        app.InitializeComponent();

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
