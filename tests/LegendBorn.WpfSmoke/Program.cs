using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using LegendBorn;
using LegendBorn.Controls;
using LegendBorn.Services;
using LegendBorn.ViewModels;
using LegendBorn.Views;
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
        AssertPackStateFinalReconciliation();

        var launcherInfoVersion = typeof(MainWindow).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion ?? string.Empty;
        if (!launcherInfoVersion.StartsWith("0.4.2", StringComparison.Ordinal))
            throw new InvalidOperationException($"Launcher 0.4.2 smoke is running against {launcherInfoVersion}.");

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
            host.Dispatcher.Invoke(static () => { }, DispatcherPriority.ApplicationIdle);

            if (host.ResizeMode == ResizeMode.NoResize)
                throw new InvalidOperationException(
                    "Production MainWindow unexpectedly disables resizing; the refreshed shell must remain resizable.");

            if (host.DataContext is not MainViewModel vm)
                throw new InvalidOperationException("Production MainWindow did not expose MainViewModel as DataContext.");

            vm.SelectedMenuIndex = 2;
            host.UpdateLayout();
            host.Dispatcher.Invoke(static () => { }, DispatcherPriority.ApplicationIdle);

            var profileView = FindLogicalDescendant<ProfileTabView>(host)
                              ?? throw new InvalidOperationException(
                                  "ProfileTabView was not found inside the production MainWindow logical tree.");

            if (profileView.FindName("ProfileXpProgressBar") is not ProgressBar progress)
                throw new InvalidOperationException(
                    "ProfileXpProgressBar was not found in the production profile view namescope.");

            if (profileView.FindName("FriendsList") is not ListBox friendsList)
                throw new InvalidOperationException(
                    "FriendsList was not found in the profile Community tab namescope.");

            if (ScrollViewer.GetVerticalScrollBarVisibility(friendsList) != ScrollBarVisibility.Disabled)
                throw new InvalidOperationException(
                    "FriendsList owns a nested vertical scrollbar; profile should have a single outer scroll owner.");

            if (profileView.FindName("ProfileSkin3DPreview") is not Skin3DView profileSkin)
                throw new InvalidOperationException(
                    "ProfileSkin3DPreview was not found in the profile Status tab.");

            if (profileView.FindName("ProfileTabs") is not TabControl profileTabs || profileTabs.Items.Count != 2)
                throw new InvalidOperationException(
                    "Profile status/community tabs are missing or no longer contain exactly two sections.");

            profileTabs.ApplyTemplate();
            host.UpdateLayout();
            var profileTabItems = profileTabs.Items.OfType<TabItem>().ToArray();
            if (profileTabItems.Length != 2 || profileTabItems[0].Margin.Right < 12)
                throw new InvalidOperationException(
                    "Status and Community tabs no longer have a clear visual gap between them.");

            var headerPanel = FindVisualDescendant<TabPanel>(profileTabs)
                              ?? throw new InvalidOperationException("Profile TabPanel was not materialized.");
            var headerParent = VisualTreeHelper.GetParent(headerPanel);
            if (headerParent is Border)
                throw new InvalidOperationException(
                    "Profile tabs regained the shared Border rail that visually merges the two pills.");

            if (ContainsTextFragment(profileView, "РЕЗОН"))
                throw new InvalidOperationException("Legacy РЕЗОН abbreviation is still visible in the profile.");

            if (!ContainsTextFragment(profileView, "РЕЗ"))
                throw new InvalidOperationException("Compact РЕЗ currency label was not rendered in the profile.");

            if (profileView.FindName("StatusCharacterCard") is not Border characterCard ||
                profileView.FindName("StatusSkinCard") is not Border skinCard)
            {
                throw new InvalidOperationException(
                    "Profile character/status alignment anchors were not found.");
            }

            if (Grid.GetRow(characterCard) != Grid.GetRow(skinCard) ||
                Grid.GetRow(characterCard) != 2)
            {
                throw new InvalidOperationException(
                    "Current character image is no longer aligned to the same status row as character telemetry.");
            }

            AssertSkinRenderer(profileSkin);

            var startView = FindLogicalDescendant<StartTabView>(host)
                            ?? throw new InvalidOperationException(
                                "StartTabView was not found inside the production MainWindow logical tree.");

            if (startView.FindName("PackProgressBar") is not ProgressBar)
                throw new InvalidOperationException(
                    "PackProgressBar was not found in the refreshed start view namescope.");

            if (startView.FindName("Skin3DPreview") is not Skin3DView skinPreview)
                throw new InvalidOperationException(
                    "Skin3DPreview was not found in the production start dashboard.");

            AssertSkinRenderer(skinPreview);

            if (startView.FindName("DashboardFriendsList") is not ItemsControl)
                throw new InvalidOperationException(
                    "DashboardFriendsList was not found in the production start dashboard.");

            if (ContainsVisibleLabel(startView, "PRESENCE"))
                throw new InvalidOperationException(
                    "Internal PRESENCE terminology leaked into the user-facing start dashboard.");

            if (startView.FindName("NewsCarouselItems") is not ItemsControl)
                throw new InvalidOperationException(
                    "NewsCarouselItems was not found in the production start dashboard.");

            if (startView.FindName("NewsPanel") is not Border newsPanel || newsPanel.Parent is not Grid dashboardGrid)
                throw new InvalidOperationException(
                    "NewsPanel was not found in the production start dashboard.");

            var newsRow = Grid.GetRow(newsPanel);
            if (newsRow < 0 || newsRow >= dashboardGrid.RowDefinitions.Count ||
                dashboardGrid.RowDefinitions[newsRow].Height.GridUnitType != GridUnitType.Pixel ||
                dashboardGrid.RowDefinitions[newsRow].Height.Value < 180)
            {
                throw new InvalidOperationException(
                    "Start dashboard news area regressed to the overly compressed height.");
            }

            var settingsView = FindLogicalDescendant<SettingsTabView>(host)
                               ?? throw new InvalidOperationException(
                                   "SettingsTabView was not found inside the production MainWindow logical tree.");

            if (settingsView.FindName("AutomaticRamCheck") is not CheckBox)
                throw new InvalidOperationException("Automatic RAM control is missing from Settings.");
            if (settingsView.FindName("GameDirectoryText") is not TextBlock)
                throw new InvalidOperationException("Game directory presentation is missing from Settings.");
            if (settingsView.FindName("RepairPackButton") is not Button)
                throw new InvalidOperationException("Pack repair action is missing from Settings.");
            if (ContainsTextFragment(settingsView, "Launcher v") ||
                ContainsTextFragment(settingsView, "join-ticket") ||
                ContainsTextFragment(settingsView, "BMCLAPI"))
            {
                throw new InvalidOperationException(
                    "Technical implementation details or duplicate launcher version leaked into Settings.");
            }

            var updateDialog = new LauncherUpdateDialog(
                "Доступно обновление",
                "Smoke",
                "0.4.1",
                "0.4.2",
                "LegendBorn CDN",
                progressMode: true);
            try
            {
                if (updateDialog.FindName("UpdateProgressBar") is not ProgressBar updateProgress ||
                    updateDialog.FindName("ProgressArea") is not Grid progressArea ||
                    progressArea.Visibility != Visibility.Visible)
                {
                    throw new InvalidOperationException("Branded launcher update progress UI was not materialized.");
                }

                updateDialog.SetProgress(64, "Smoke");
                if (Math.Abs(updateProgress.Value - 64) > 0.001)
                    throw new InvalidOperationException("Launcher update dialog does not expose real download progress.");
            }
            finally
            {
                updateDialog.Close();
            }

            if (BindingOperations.GetBinding(progress, RangeBase.ValueProperty) is not null ||
                BindingOperations.GetBindingExpression(progress, RangeBase.ValueProperty) is not null)
            {
                throw new InvalidOperationException(
                    "Profile XP progress unexpectedly has a WPF BindingExpression; read-only XP must be mirrored manually.");
            }

            if (vm.MinecraftStatusHealth != "—" || vm.MinecraftStatusDimension != "—")
                throw new InvalidOperationException("Minecraft status must default to unknown instead of fabricated telemetry.");

            if (string.IsNullOrWhiteSpace(vm.GameDirectoryPath))
                throw new InvalidOperationException("Settings game directory path is empty.");

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

    private static void AssertSkinRenderer(Skin3DView view)
    {
        var buildPlayer = typeof(Skin3DView).GetMethod(
            "BuildPlayer",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Skin3DView.BuildPlayer was not found for runtime smoke.");
        var sceneField = typeof(Skin3DView).GetField(
            "_scene",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Skin3DView scene field was not found for runtime smoke.");

        var modern = CreateSkinBitmap(64);
        buildPlayer.Invoke(view, new object[] { modern });
        var scene = sceneField.GetValue(view) as Model3DGroup
                    ?? throw new InvalidOperationException("Skin3DView scene is unavailable.");
        if (scene.Children.Count < 4 || scene.Children[^1] is not Model3DGroup modernPlayer || modernPlayer.Children.Count != 12)
            throw new InvalidOperationException("Modern Minecraft skin did not render all six base and six outer-layer cuboids.");

        var legacy = CreateSkinBitmap(32);
        buildPlayer.Invoke(view, new object[] { legacy });
        if (scene.Children.Count < 4 || scene.Children[^1] is not Model3DGroup legacyPlayer || legacyPlayer.Children.Count != 6)
            throw new InvalidOperationException("Legacy Minecraft skin renderer no longer matches its six base cuboids.");
    }

    private static WriteableBitmap CreateSkinBitmap(int height)
    {
        var bitmap = new WriteableBitmap(64, height, 96, 96, PixelFormats.Bgra32, null);
        var stride = 64 * 4;
        var pixels = new byte[stride * height];

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < 64; x++)
            {
                var i = y * stride + x * 4;
                pixels[i + 0] = (byte)((x * 3) % 255);
                pixels[i + 1] = (byte)((y * 5) % 255);
                pixels[i + 2] = (byte)(120 + (x + y) % 120);
                pixels[i + 3] = 255;
            }
        }

        bitmap.WritePixels(new Int32Rect(0, 0, 64, height), pixels, stride, 0);
        bitmap.Freeze();
        return bitmap;
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
                throw new InvalidOperationException("Managed cleanup removed a manifest-owned mod.");
            if (File.Exists(stale))
                throw new InvalidOperationException("Managed cleanup left a stale mod in the managed root.");
            if (!File.Exists(config))
                throw new InvalidOperationException("Managed cleanup touched protected user config.");

            var trashRoot = Path.Combine(temp, ".trash", "pack-cleanup");
            if (!Directory.Exists(trashRoot) ||
                !Directory.EnumerateFiles(trashRoot, "stale.jar", SearchOption.AllDirectories).Any())
            {
                throw new InvalidOperationException("Stale managed file was not preserved in the SAFE .trash quarantine.");
            }
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch { }
        }
    }

    private static void AssertPackStateFinalReconciliation()
    {
        var temp = Path.Combine(Path.GetTempPath(), "legendborn-pack-state-" + Guid.NewGuid().ToString("N"));
        var mods = Path.Combine(temp, "mods");
        var launcher = Path.Combine(temp, "launcher");
        Directory.CreateDirectory(mods);
        Directory.CreateDirectory(launcher);

        try
        {
            File.WriteAllText(Path.Combine(mods, "current.jar"), "current");
            File.WriteAllText(Path.Combine(mods, "old.jar"), "old");
            File.WriteAllText(
                Path.Combine(launcher, "pack_state.json"),
                "{\"PackId\":\"smoke\",\"Files\":{\"mods/current.jar\":{\"Size\":7,\"Sha256\":\"x\",\"LastWriteUtcTicks\":0}}}");

            ManagedPackStateVerifier.ReconcileAsync(temp).GetAwaiter().GetResult();

            if (File.Exists(Path.Combine(mods, "old.jar")))
                throw new InvalidOperationException("Final pack-state reconciliation left an old mod active.");
            if (!File.Exists(Path.Combine(mods, "current.jar")))
                throw new InvalidOperationException("Final pack-state reconciliation removed the current mod.");

            File.WriteAllText(Path.Combine(mods, "current.jar.pending"), "replacement");
            try
            {
                ManagedPackStateVerifier.ReconcileAsync(temp).GetAwaiter().GetResult();
                throw new InvalidOperationException("A managed .pending file did not block mixed-version launch.");
            }
            catch (IOException)
            {
                // Expected: new bytes are downloaded but the active file could not be replaced.
            }
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

    private static bool ContainsVisibleLabel(DependencyObject root, string label)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is TextBlock textBlock &&
                string.Equals(textBlock.Text, label, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (child is DependencyObject dependencyObject && ContainsVisibleLabel(dependencyObject, label))
                return true;
        }

        return false;
    }

    private static bool ContainsTextFragment(DependencyObject root, string fragment)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is TextBlock textBlock &&
                (textBlock.Text ?? string.Empty).Contains(fragment, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (child is ContentControl contentControl &&
                contentControl.Content is string content &&
                content.Contains(fragment, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (child is DependencyObject dependencyObject && ContainsTextFragment(dependencyObject, fragment))
                return true;
        }

        return false;
    }

    private static T? FindVisualDescendant<T>(DependencyObject root)
        where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
                return match;

            var nested = FindVisualDescendant<T>(child);
            if (nested is not null)
                return nested;
        }

        return null;
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
