using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;

namespace LegendBorn.Controls;

/// <summary>
/// Lightweight native WPF Minecraft skin renderer.
/// The atlas is cropped into individual pixel-perfect face textures and rendered with the
/// real Minecraft second layer (hat/jacket/sleeves/trousers), so transparent overlays are
/// not lost and the preview stays sharp instead of being bilinear-filtered.
/// </summary>
public sealed class Skin3DView : UserControl
{
    private const long MaxSkinBytes = 4L * 1024 * 1024;
    private const int MaxCachedSkinUrls = 32;

    private static readonly HttpClient Http = CreateHttp();
    private static readonly ConcurrentDictionary<string, WeakReference<BitmapSource>> SkinCache =
        new(StringComparer.Ordinal);

    private readonly Viewport3D _viewport = new();
    private readonly Border _placeholder;
    private readonly Model3DGroup _scene = new();

    private CancellationTokenSource? _loadCts;
    private AxisAngleRotation3D? _rotation;
    private Point _dragStart;
    private double _dragStartAngle;
    private bool _dragging;

    public static readonly DependencyProperty SkinUrlProperty = DependencyProperty.Register(
        nameof(SkinUrl),
        typeof(string),
        typeof(Skin3DView),
        new PropertyMetadata(null, OnSkinUrlChanged));

    public string? SkinUrl
    {
        get => GetValue(SkinUrlProperty) as string;
        set => SetValue(SkinUrlProperty, value);
    }

    public Skin3DView()
    {
        Focusable = false;
        ClipToBounds = true;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);
        RenderOptions.SetBitmapScalingMode(_viewport, BitmapScalingMode.NearestNeighbor);

        var root = new Grid
        {
            SnapsToDevicePixels = true,
            UseLayoutRounding = true
        };
        root.Children.Add(_viewport);

        _placeholder = new Border
        {
            Background = Brushes.Transparent,
            Child = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    new TextBlock
                    {
                        Text = "◈",
                        FontSize = 28,
                        Foreground = new SolidColorBrush(Color.FromRgb(127, 91, 181)),
                        HorizontalAlignment = HorizontalAlignment.Center
                    },
                    new TextBlock
                    {
                        Text = "Загружаю 3D-образ…",
                        Margin = new Thickness(0, 8, 0, 0),
                        FontSize = 10,
                        Foreground = new SolidColorBrush(Color.FromRgb(135, 145, 166)),
                        HorizontalAlignment = HorizontalAlignment.Center
                    }
                }
            }
        };
        root.Children.Add(_placeholder);
        Content = root;

        _viewport.Camera = new PerspectiveCamera(
            new Point3D(27, 8, 46),
            new Vector3D(-27, -3, -46),
            new Vector3D(0, 1, 0),
            55);

        _scene.Children.Add(new AmbientLight(Color.FromRgb(178, 174, 194)));
        _scene.Children.Add(new DirectionalLight(Color.FromRgb(255, 248, 255), new Vector3D(-1, -1, -2)));
        _scene.Children.Add(new DirectionalLight(Color.FromRgb(112, 77, 176), new Vector3D(1, 0, 1)));
        _viewport.Children.Add(new ModelVisual3D { Content = _scene });

        _viewport.Cursor = Cursors.Hand;
        _viewport.MouseLeftButtonDown += Viewport_OnMouseLeftButtonDown;
        _viewport.MouseMove += Viewport_OnMouseMove;
        _viewport.MouseLeftButtonUp += Viewport_OnMouseLeftButtonUp;
        _viewport.LostMouseCapture += (_, _) => _dragging = false;

        Loaded += (_, _) =>
        {
            if (_scene.Children.Count <= 3 && _loadCts is null)
                StartLoad(SkinUrl);
        };
        Unloaded += (_, _) => CancelLoad();
    }

    private static void OnSkinUrlChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is Skin3DView view)
            view.StartLoad(e.NewValue as string);
    }

    private void StartLoad(string? rawUrl)
    {
        CancelLoad();
        ClearPlayer();
        _placeholder.Visibility = Visibility.Visible;

        var value = (rawUrl ?? string.Empty).Trim();
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            return;

        var cached = TryGetCachedSkin(uri.AbsoluteUri);
        if (cached is not null)
        {
            BuildPlayer(cached);
            return;
        }

        var cts = new CancellationTokenSource();
        _loadCts = cts;
        _ = LoadAsync(uri, cts.Token);
    }

    private async Task LoadAsync(Uri uri, CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));

            using var request = new HttpRequestMessage(HttpMethod.Get, uri)
            {
                Version = HttpVersion.Version20,
                VersionPolicy = HttpVersionPolicy.RequestVersionOrLower
            };
            using var response = await Http.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    timeout.Token)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                return;
            if (response.RequestMessage?.RequestUri is not { Scheme: "https" })
                return;
            if (response.Content.Headers.ContentLength is long declared && declared > MaxSkinBytes)
                return;

            var bytes = await response.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false);
            if (bytes.Length <= 0 || bytes.LongLength > MaxSkinBytes)
                return;

            var image = CreateBitmap(bytes);
            if (!IsSupportedSkin(image))
                return;

            CacheSkin(uri.AbsoluteUri, image);
            await Dispatcher.InvokeAsync(() => BuildPlayer(image));
        }
        catch (OperationCanceledException)
        {
            // Normal when the URL changes or the control leaves the visual tree.
        }
        catch
        {
            // Rendering is presentation-only; profile/game flow must never depend on the preview.
        }
        finally
        {
            if (!ct.IsCancellationRequested)
            {
                var completed = Interlocked.Exchange(ref _loadCts, null);
                completed?.Dispose();
            }
        }
    }

    private static BitmapImage CreateBitmap(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes, writable: false);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
        image.StreamSource = ms;
        image.EndInit();
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.NearestNeighbor);
        image.Freeze();
        return image;
    }

    private static bool IsSupportedSkin(BitmapSource skin)
    {
        if (skin.PixelWidth < 64 || skin.PixelHeight < 32)
            return false;
        if (skin.PixelWidth % 64 != 0 || skin.PixelHeight % 32 != 0)
            return false;

        var scale = skin.PixelWidth / 64;
        return skin.PixelHeight == 32 * scale || skin.PixelHeight == 64 * scale;
    }

    private void BuildPlayer(BitmapSource skin)
    {
        ClearPlayer();

        var modern = skin.PixelHeight == skin.PixelWidth;
        var player = new Model3DGroup();

        // Base head/body.
        player.Children.Add(CreateCuboid(skin, 0, 17, 0, 8, 8, 8,
            R(8, 8, 8, 8), R(24, 8, 8, 8), R(16, 8, 8, 8), R(0, 8, 8, 8),
            R(8, 0, 8, 8), R(16, 0, 8, 8)));
        player.Children.Add(CreateCuboid(skin, 0, 7, 0, 8, 12, 4,
            R(20, 20, 8, 12), R(32, 20, 8, 12), R(28, 20, 4, 12), R(16, 20, 4, 12),
            R(20, 16, 8, 4), R(28, 16, 8, 4)));

        // Base arms. Modern skins have a dedicated left-arm region; legacy skins mirror the right arm.
        player.Children.Add(CreateCuboid(skin, -6, 7, 0, 4, 12, 4,
            modern ? R(36, 52, 4, 12) : R(44, 20, 4, 12),
            modern ? R(44, 52, 4, 12) : R(52, 20, 4, 12),
            modern ? R(40, 52, 4, 12) : R(48, 20, 4, 12),
            modern ? R(32, 52, 4, 12) : R(40, 20, 4, 12),
            modern ? R(36, 48, 4, 4) : R(44, 16, 4, 4),
            modern ? R(40, 48, 4, 4) : R(48, 16, 4, 4)));
        player.Children.Add(CreateCuboid(skin, 6, 7, 0, 4, 12, 4,
            R(44, 20, 4, 12), R(52, 20, 4, 12), R(48, 20, 4, 12), R(40, 20, 4, 12),
            R(44, 16, 4, 4), R(48, 16, 4, 4)));

        // Base legs. Modern skins have a dedicated left-leg region; legacy skins mirror the right leg.
        player.Children.Add(CreateCuboid(skin, -2, -5, 0, 4, 12, 4,
            modern ? R(20, 52, 4, 12) : R(4, 20, 4, 12),
            modern ? R(28, 52, 4, 12) : R(12, 20, 4, 12),
            modern ? R(24, 52, 4, 12) : R(8, 20, 4, 12),
            modern ? R(16, 52, 4, 12) : R(0, 20, 4, 12),
            modern ? R(20, 48, 4, 4) : R(4, 16, 4, 4),
            modern ? R(24, 48, 4, 4) : R(8, 16, 4, 4)));
        player.Children.Add(CreateCuboid(skin, 2, -5, 0, 4, 12, 4,
            R(4, 20, 4, 12), R(12, 20, 4, 12), R(8, 20, 4, 12), R(0, 20, 4, 12),
            R(4, 16, 4, 4), R(8, 16, 4, 4)));

        // Second layer: the hat exists in both 64x32 and 64x64 layouts.
        player.Children.Add(CreateCuboid(skin, 0, 17, 0, 9, 9, 9,
            R(40, 8, 8, 8), R(56, 8, 8, 8), R(48, 8, 8, 8), R(32, 8, 8, 8),
            R(40, 0, 8, 8), R(48, 0, 8, 8)));

        if (modern)
        {
            // Jacket.
            player.Children.Add(CreateCuboid(skin, 0, 7, 0, 8.5, 12.5, 4.5,
                R(20, 36, 8, 12), R(32, 36, 8, 12), R(28, 36, 4, 12), R(16, 36, 4, 12),
                R(20, 32, 8, 4), R(28, 32, 8, 4)));

            // Left/right sleeves.
            player.Children.Add(CreateCuboid(skin, -6, 7, 0, 4.5, 12.5, 4.5,
                R(52, 52, 4, 12), R(60, 52, 4, 12), R(56, 52, 4, 12), R(48, 52, 4, 12),
                R(52, 48, 4, 4), R(56, 48, 4, 4)));
            player.Children.Add(CreateCuboid(skin, 6, 7, 0, 4.5, 12.5, 4.5,
                R(44, 36, 4, 12), R(52, 36, 4, 12), R(48, 36, 4, 12), R(40, 36, 4, 12),
                R(44, 32, 4, 4), R(48, 32, 4, 4)));

            // Left/right trouser layers.
            player.Children.Add(CreateCuboid(skin, -2, -5, 0, 4.5, 12.5, 4.5,
                R(4, 52, 4, 12), R(12, 52, 4, 12), R(8, 52, 4, 12), R(0, 52, 4, 12),
                R(4, 48, 4, 4), R(8, 48, 4, 4)));
            player.Children.Add(CreateCuboid(skin, 2, -5, 0, 4.5, 12.5, 4.5,
                R(4, 36, 4, 12), R(12, 36, 4, 12), R(8, 36, 4, 12), R(0, 36, 4, 12),
                R(4, 32, 4, 4), R(8, 32, 4, 4)));
        }

        _rotation = new AxisAngleRotation3D(new Vector3D(0, 1, 0), -18);
        player.Transform = new RotateTransform3D(_rotation, new Point3D(0, 4, 0));
        _scene.Children.Add(player);
        _placeholder.Visibility = Visibility.Collapsed;
    }

    private void Viewport_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_rotation is null) return;
        _dragging = true;
        _dragStart = e.GetPosition(_viewport);
        _dragStartAngle = _rotation.Angle;
        _viewport.CaptureMouse();
        _viewport.Cursor = Cursors.SizeWE;
        e.Handled = true;
    }

    private void Viewport_OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging || _rotation is null || e.LeftButton != MouseButtonState.Pressed)
            return;

        var current = e.GetPosition(_viewport);
        _rotation.Angle = _dragStartAngle + (current.X - _dragStart.X) * 0.65;
        e.Handled = true;
    }

    private void Viewport_OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        _viewport.ReleaseMouseCapture();
        _viewport.Cursor = Cursors.Hand;
        e.Handled = true;
    }

    private void ClearPlayer()
    {
        _rotation = null;
        while (_scene.Children.Count > 3)
            _scene.Children.RemoveAt(_scene.Children.Count - 1);
    }

    private static Model3DGroup CreateCuboid(
        BitmapSource skin,
        double cx,
        double cy,
        double cz,
        double width,
        double height,
        double depth,
        Int32Rect front,
        Int32Rect back,
        Int32Rect left,
        Int32Rect right,
        Int32Rect top,
        Int32Rect bottom)
    {
        var x0 = cx - width / 2.0;
        var x1 = cx + width / 2.0;
        var y0 = cy - height / 2.0;
        var y1 = cy + height / 2.0;
        var z0 = cz - depth / 2.0;
        var z1 = cz + depth / 2.0;

        var group = new Model3DGroup();
        group.Children.Add(CreateFace(skin, front,
            new Point3D(x0, y0, z1), new Point3D(x1, y0, z1), new Point3D(x1, y1, z1), new Point3D(x0, y1, z1)));
        group.Children.Add(CreateFace(skin, back,
            new Point3D(x1, y0, z0), new Point3D(x0, y0, z0), new Point3D(x0, y1, z0), new Point3D(x1, y1, z0)));
        group.Children.Add(CreateFace(skin, left,
            new Point3D(x0, y0, z0), new Point3D(x0, y0, z1), new Point3D(x0, y1, z1), new Point3D(x0, y1, z0)));
        group.Children.Add(CreateFace(skin, right,
            new Point3D(x1, y0, z1), new Point3D(x1, y0, z0), new Point3D(x1, y1, z0), new Point3D(x1, y1, z1)));
        group.Children.Add(CreateFace(skin, top,
            new Point3D(x0, y1, z1), new Point3D(x1, y1, z1), new Point3D(x1, y1, z0), new Point3D(x0, y1, z0)));
        group.Children.Add(CreateFace(skin, bottom,
            new Point3D(x0, y0, z0), new Point3D(x1, y0, z0), new Point3D(x1, y0, z1), new Point3D(x0, y0, z1)));
        return group;
    }

    private static GeometryModel3D CreateFace(
        BitmapSource skin,
        Int32Rect logicalRegion,
        Point3D p0,
        Point3D p1,
        Point3D p2,
        Point3D p3)
    {
        var material = CreateFaceMaterial(skin, logicalRegion);
        var mesh = new MeshGeometry3D
        {
            Positions = new Point3DCollection { p0, p1, p2, p3 },
            TextureCoordinates = new PointCollection
            {
                new(0, 1),
                new(1, 1),
                new(1, 0),
                new(0, 0)
            },
            TriangleIndices = new Int32Collection { 0, 1, 2, 0, 2, 3 }
        };
        mesh.Freeze();

        return new GeometryModel3D(mesh, material)
        {
            BackMaterial = material
        };
    }

    private static Material CreateFaceMaterial(BitmapSource skin, Int32Rect logicalRegion)
    {
        var scale = Math.Max(1, skin.PixelWidth / 64);
        var region = new Int32Rect(
            logicalRegion.X * scale,
            logicalRegion.Y * scale,
            logicalRegion.Width * scale,
            logicalRegion.Height * scale);

        if (region.X < 0 || region.Y < 0 ||
            region.X + region.Width > skin.PixelWidth ||
            region.Y + region.Height > skin.PixelHeight)
        {
            var fallbackBrush = new SolidColorBrush(Color.FromRgb(120, 104, 146));
            fallbackBrush.Freeze();
            var fallback = new DiffuseMaterial(fallbackBrush);
            fallback.Freeze();
            return fallback;
        }

        var crop = new CroppedBitmap(skin, region);
        RenderOptions.SetBitmapScalingMode(crop, BitmapScalingMode.NearestNeighbor);
        crop.Freeze();

        var brush = new ImageBrush(crop)
        {
            Stretch = Stretch.Fill,
            TileMode = TileMode.None,
            ViewportUnits = BrushMappingMode.RelativeToBoundingBox
        };
        RenderOptions.SetBitmapScalingMode(brush, BitmapScalingMode.NearestNeighbor);
        brush.Freeze();

        var material = new DiffuseMaterial(brush);
        material.Freeze();
        return material;
    }

    private static Int32Rect R(int x, int y, int width, int height)
        => new(x, y, width, height);

    private static BitmapSource? TryGetCachedSkin(string key)
    {
        if (!SkinCache.TryGetValue(key, out var reference))
            return null;

        if (reference.TryGetTarget(out var target) && target is not null)
            return target;

        SkinCache.TryRemove(key, out _);
        return null;
    }

    private static void CacheSkin(string key, BitmapSource skin)
    {
        if (SkinCache.Count >= MaxCachedSkinUrls)
            SkinCache.Clear();

        SkinCache[key] = new WeakReference<BitmapSource>(skin);
    }

    private void CancelLoad()
    {
        var old = Interlocked.Exchange(ref _loadCts, null);
        try { old?.Cancel(); } catch { }
        try { old?.Dispose(); } catch { }
    }

    private static HttpClient CreateHttp()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            Proxy = WebRequest.DefaultWebProxy,
            UseProxy = true,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            MaxConnectionsPerServer = 2,
            AllowAutoRedirect = true
        };

        return new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
    }
}
