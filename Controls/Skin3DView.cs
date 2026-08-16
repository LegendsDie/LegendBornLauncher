using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;

namespace LegendBorn.Controls;

/// <summary>
/// Lightweight local WPF Minecraft skin renderer. It downloads the selected 64x64 skin texture
/// and maps the vanilla base-layer UVs onto cuboids inside a Viewport3D. No browser/WebView is
/// required, and an unavailable texture degrades to a quiet placeholder.
/// </summary>
public sealed class Skin3DView : UserControl
{
    private const long MaxSkinBytes = 4L * 1024 * 1024;
    private static readonly HttpClient Http = CreateHttp();

    private readonly Viewport3D _viewport = new();
    private readonly Border _placeholder;
    private readonly Model3DGroup _scene = new();
    private CancellationTokenSource? _loadCts;

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

        var root = new Grid();
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
                        Text = "3D-скин появится здесь",
                        Margin = new Thickness(0, 8, 0, 0),
                        FontSize = 11,
                        Foreground = new SolidColorBrush(Color.FromRgb(135, 145, 166)),
                        HorizontalAlignment = HorizontalAlignment.Center
                    }
                }
            }
        };
        root.Children.Add(_placeholder);
        Content = root;

        _viewport.Camera = new PerspectiveCamera(
            new Point3D(31, 12, 52),
            new Vector3D(-31, -7, -52),
            new Vector3D(0, 1, 0),
            34);

        _scene.Children.Add(new AmbientLight(Color.FromRgb(160, 160, 180)));
        _scene.Children.Add(new DirectionalLight(Color.FromRgb(255, 244, 255), new Vector3D(-1, -1, -2)));
        _scene.Children.Add(new DirectionalLight(Color.FromRgb(126, 87, 190), new Vector3D(1, 0, 1)));
        _viewport.Children.Add(new ModelVisual3D { Content = _scene });

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

        var value = (rawUrl ?? string.Empty).Trim();
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            _placeholder.Visibility = Visibility.Visible;
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
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                return;

            if (response.RequestMessage?.RequestUri is not { Scheme: "https" })
                return;

            if (response.Content.Headers.ContentLength is long declared && declared > MaxSkinBytes)
                return;

            var bytes = await response.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false);
            if (bytes.Length <= 0 || (long)bytes.Length > MaxSkinBytes)
                return;

            var image = CreateBitmap(bytes);
            if (image.PixelWidth < 64 || image.PixelHeight < 32)
                return;

            await Dispatcher.InvokeAsync(() => BuildPlayer(image));
        }
        catch (OperationCanceledException) { }
        catch
        {
            // Dashboard rendering is presentation-only; profile/game flow must not depend on it.
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
        image.Freeze();
        return image;
    }

    private void BuildPlayer(BitmapSource skin)
    {
        ClearPlayer();

        var material = new DiffuseMaterial(new ImageBrush(skin)
        {
            Stretch = Stretch.Fill,
            TileMode = TileMode.None,
            ViewportUnits = BrushMappingMode.RelativeToBoundingBox
        });
        material.Freeze();

        var player = new Model3DGroup();

        // Standard 64x64 Minecraft base-layer layout, classic 4px arms.
        player.Children.Add(CreateCuboid(0, 17, 0, 8, 8, 8, material,
            front: Uv(8, 8, 8, 8), back: Uv(24, 8, 8, 8),
            left: Uv(16, 8, 8, 8), right: Uv(0, 8, 8, 8),
            top: Uv(8, 0, 8, 8), bottom: Uv(16, 0, 8, 8)));

        player.Children.Add(CreateCuboid(0, 7, 0, 8, 12, 4, material,
            front: Uv(20, 20, 8, 12), back: Uv(32, 20, 8, 12),
            left: Uv(28, 20, 4, 12), right: Uv(16, 20, 4, 12),
            top: Uv(20, 16, 8, 4), bottom: Uv(28, 16, 8, 4)));

        player.Children.Add(CreateCuboid(-6, 7, 0, 4, 12, 4, material,
            front: Uv(36, 52, 4, 12), back: Uv(44, 52, 4, 12),
            left: Uv(40, 52, 4, 12), right: Uv(32, 52, 4, 12),
            top: Uv(36, 48, 4, 4), bottom: Uv(40, 48, 4, 4)));

        player.Children.Add(CreateCuboid(6, 7, 0, 4, 12, 4, material,
            front: Uv(44, 20, 4, 12), back: Uv(52, 20, 4, 12),
            left: Uv(48, 20, 4, 12), right: Uv(40, 20, 4, 12),
            top: Uv(44, 16, 4, 4), bottom: Uv(48, 16, 4, 4)));

        player.Children.Add(CreateCuboid(-2, -5, 0, 4, 12, 4, material,
            front: Uv(20, 52, 4, 12), back: Uv(28, 52, 4, 12),
            left: Uv(24, 52, 4, 12), right: Uv(16, 52, 4, 12),
            top: Uv(20, 48, 4, 4), bottom: Uv(24, 48, 4, 4)));

        player.Children.Add(CreateCuboid(2, -5, 0, 4, 12, 4, material,
            front: Uv(4, 20, 4, 12), back: Uv(12, 20, 4, 12),
            left: Uv(8, 20, 4, 12), right: Uv(0, 20, 4, 12),
            top: Uv(4, 16, 4, 4), bottom: Uv(8, 16, 4, 4)));

        var rotation = new AxisAngleRotation3D(new Vector3D(0, 1, 0), -13);
        player.Transform = new RotateTransform3D(rotation, new Point3D(0, 4, 0));
        rotation.BeginAnimation(
            AxisAngleRotation3D.AngleProperty,
            new DoubleAnimation(-16, 16, TimeSpan.FromSeconds(5.5))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            });

        _scene.Children.Add(player);
        _placeholder.Visibility = Visibility.Collapsed;
    }

    private void ClearPlayer()
    {
        while (_scene.Children.Count > 3)
            _scene.Children.RemoveAt(_scene.Children.Count - 1);
    }

    private static GeometryModel3D CreateCuboid(
        double cx, double cy, double cz,
        double width, double height, double depth,
        Material material,
        Rect front, Rect back, Rect left, Rect right, Rect top, Rect bottom)
    {
        var x0 = cx - width / 2.0;
        var x1 = cx + width / 2.0;
        var y0 = cy - height / 2.0;
        var y1 = cy + height / 2.0;
        var z0 = cz - depth / 2.0;
        var z1 = cz + depth / 2.0;

        var mesh = new MeshGeometry3D();

        AddFace(mesh, new Point3D(x0, y0, z1), new Point3D(x1, y0, z1), new Point3D(x1, y1, z1), new Point3D(x0, y1, z1), front);
        AddFace(mesh, new Point3D(x1, y0, z0), new Point3D(x0, y0, z0), new Point3D(x0, y1, z0), new Point3D(x1, y1, z0), back);
        AddFace(mesh, new Point3D(x0, y0, z0), new Point3D(x0, y0, z1), new Point3D(x0, y1, z1), new Point3D(x0, y1, z0), left);
        AddFace(mesh, new Point3D(x1, y0, z1), new Point3D(x1, y0, z0), new Point3D(x1, y1, z0), new Point3D(x1, y1, z1), right);
        AddFace(mesh, new Point3D(x0, y1, z1), new Point3D(x1, y1, z1), new Point3D(x1, y1, z0), new Point3D(x0, y1, z0), top);
        AddFace(mesh, new Point3D(x0, y0, z0), new Point3D(x1, y0, z0), new Point3D(x1, y0, z1), new Point3D(x0, y0, z1), bottom);

        mesh.Freeze();
        return new GeometryModel3D(mesh, material) { BackMaterial = material };
    }

    private static void AddFace(MeshGeometry3D mesh, Point3D p0, Point3D p1, Point3D p2, Point3D p3, Rect uv)
    {
        var start = mesh.Positions.Count;
        mesh.Positions.Add(p0);
        mesh.Positions.Add(p1);
        mesh.Positions.Add(p2);
        mesh.Positions.Add(p3);

        mesh.TextureCoordinates.Add(new Point(uv.Left, uv.Bottom));
        mesh.TextureCoordinates.Add(new Point(uv.Right, uv.Bottom));
        mesh.TextureCoordinates.Add(new Point(uv.Right, uv.Top));
        mesh.TextureCoordinates.Add(new Point(uv.Left, uv.Top));

        mesh.TriangleIndices.Add(start);
        mesh.TriangleIndices.Add(start + 1);
        mesh.TriangleIndices.Add(start + 2);
        mesh.TriangleIndices.Add(start);
        mesh.TriangleIndices.Add(start + 2);
        mesh.TriangleIndices.Add(start + 3);
    }

    private static Rect Uv(double x, double y, double width, double height)
        => new(x / 64.0, y / 64.0, width / 64.0, height / 64.0);

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

        return new HttpClient(handler) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
    }
}
