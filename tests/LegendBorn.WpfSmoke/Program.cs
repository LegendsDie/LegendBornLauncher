using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Threading;
using LegendBorn;
using LegendBorn.Controls;
using LegendBorn.Services;
using LegendBorn.ViewModels;
using LegendBorn.Views.Tabs;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        Console.WriteLine($"WPF_SMOKE_ENVIRONMENT_VERSION={Environment.Version}");
        Console.WriteLine($"WPF_SMOKE_FRAMEWORK={RuntimeInformation.FrameworkDescription}");

        AssertCanonicalLegendBornOrigins();
        AssertManagedCleanupContract();

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

            if (host.ResizeMode == ResizeMode.NoResize)
                throw new InvalidOperationException(
                    "Production MainWindow unexpectedly disables resizing; the refreshed shell must remain resizable.");

            var profileView = FindLogicalDescendant<ProfileTabView>(host)
                              ?? throw new InvalidOperationException(
                                  "ProfileTabView was not found inside the production MainWindow logical tree.");

            if (profileView.FindName("ProfileXpProgressBar") is not ProgressBar progress)
                throw new InvalidOperationException(
                    "ProfileXpProgressBar was not found in the production profile view namescope.");

            if (profileView.FindName("FriendsList") is not ListBox friendsList)
                throw new InvalidOperationException(
                    "FriendsList was not found in the refreshed profile view namescope.");

            if (ScrollViewer.GetVerticalScrollBarVisibility(friendsList) != ScrollBarVisibility.Disabled)
                throw new InvalidOperationException(
                    "FriendsList owns a nested vertical scrollbar; profile should have a single outer scroll owner.");

            var startView = FindLogicalDescendant<StartTabView>(host)
                            ?? throw new InvalidOperationException(
                                "StartTabView was not found inside the production MainWindow logical tree.");

            if (startView.FindName("PackProgressBar") is not ProgressBar)
                throw new InvalidOperationException(
                    "PackProgressBar was not found in the refreshed start view namescope.");

            if (startView.FindName("Skin3DPreview") is not Skin3DView)
                throw new InvalidOperationException(
                    "Skin3DPreview was not found in the production start dashboard.");

            if (startView.FindName("DashboardFriendsList") is not ItemsControl)
                throw new InvalidOperationException(
                    "DashboardFriendsList was not found in the production start dashboard.");

            if (startView.FindName("NewsCarouselItems") is not ItemsControl)
                throw new InvalidOperationException(
                    "NewsCarouselItems was not found in the production start dashboard.");

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

    private static void AssertManagedCleanupContract()
    {
        var temp = Path.Combine(Path.GetTempPath(), "legendborn-managed-cleanup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(temp, "mods"));
        Directory.CreateDirectory(Path.Combine(temp, "config"));

        var wanted = Path.Combine(temp, "mods", "wanted.jar");
        var stale = Path.Combine(temp, "mods", "stale.jar");
        var config = Path.Combine(temp, "config", "user.toml");

        try
        {
            File.WriteAllText(wanted, "wanted");
            File.WriteAllText(stale, "stale");
            File.WriteAllText(config, "user-owned");

            var method = typeof(ManagedPackCleanupService).GetMethod(
                "ReconcileLocalManagedRootsAsync",
                BindingFlags.Static | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Managed cleanup local reconciliation method was not found.");

            var wantedSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "mods/wanted.jar" };
            var task = method.Invoke(null, new object[] { temp, wantedSet, CancellationToken.None }) as Task
                       ?? throw new InvalidOperationException("Managed cleanup did not return a Task.");
            task.GetAwaiter().GetResult();

            if (!File.Exists(wanted))
                throw new InvalidOperationException("Managed cleanup deleted a manifest-owned mod.");
            if (File.Exists(stale))
                throw new InvalidOperationException("Managed cleanup left a stale mod in the managed root.");
            if (!File.Exists(config))
                throw new InvalidOperationException("Managed cleanup touched protected user config.");
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch { }
        }
    }

    private static void AssertCanonicalLegendBornOrigins()
    {
        const string canonicalOrigin = "https://legendborn.xyz";
        var names = new[]
        {
            "SiteBaseUrl",
            "SitePublicUrlPrimary",
            "SitePublicUrlFallback"
        };

        foreach (var name in names)
        {
            var field = typeof(MainViewModel).GetField(name, BindingFlags.Static | BindingFlags.NonPublic)
                        ?? throw new InvalidOperationException($"MainViewModel.{name} origin constant was not found.");

            var value = field.GetRawConstantValue() as string;
            if (!string.Equals(value, canonicalOrigin, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"MainViewModel.{name} must resolve to the canonical LegendBorn origin {canonicalOrigin}, got {value ?? "<null>"}.");
            }
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
