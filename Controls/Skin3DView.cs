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
/// Native WPF Minecraft skin preview aligned with legendborn.xyz/immersion (skinview3d semantics).
/// It normalizes legacy skins, fixes opaque outer layers, auto-detects slim arms and maps the
/// Minecraft UV atlas to the correct world faces before WPF renders the model.
/// </summary>
public sealed class Skin3DView : UserControl
{
    private const long MaxSkinBytes = 4L * 1024 * 1024;
    private const int TextureUpscale = 6;
    private const int MaxCachedSkinUrls = 32;

    // Keep the native WPF preview framed like the website's SkinViewer: fov=55, zoom=0.9.
    private const double PreviewFov = 55.0;
    private const double PreviewZoom = 0.90;
    private const double CameraPadding = 4.5;
    private const double PlayerHalfHeight = 16.5;
    private const double PlayerCenterY = 5.0;
    private const double DefaultYaw = -18.0;

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

    private readonly record struct PreparedSkin(BitmapSource Texture, bool IsSlim, bool HasOuterLayer);

    private readonly record struct CuboidUv(
        Int32Rect Front,
        Int32Rect Back,
        Int32Rect Left,
        Int32Rect Right,
        Int32Rect Top,
        Int32Rect Bottom);

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

        var cameraDistance = ComputeCameraDistance(PreviewFov, PreviewZoom);
        _viewport.Camera = new PerspectiveCamera(
            new Point3D(0, PlayerCenterY, cameraDistance),
            new Vector3D(0, 0, -cameraDistance),
            new Vector3D(0, 1, 0),
            PreviewFov);

        _scene.Children.Add(new AmbientLight(Color.FromRgb(220, 216, 228)));
        _scene.Children.Add(new DirectionalLight(Color.FromRgb(255, 250, 255), new Vector3D(-1, -1, -2)));
        _scene.Children.Add(new DirectionalLight(Color.FromRgb(126, 91, 188), new Vector3D(1, 0, 1)));
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

    private static double ComputeCameraDistance(double fov, double zoom)
        => CameraPadding + PlayerHalfHeight / Math.Tan(fov * Math.PI / 360.0) / zoom;

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
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
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
            // Presentation-only component: profile/game flow must never depend on the preview.
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

    private void BuildPlayer(BitmapSource sourceSkin)
    {
        ClearPlayer();

        var prepared = PrepareSkinForRendering(sourceSkin);
        var skin = prepared.Texture;
        var armWidth = prepared.IsSlim ? 3 : 4;
        var armCenter = 4.0 + armWidth / 2.0;
        var player = new Model3DGroup();

        // Base layer. UV origins and dimensions intentionally mirror skinview3d's setSkinUVs calls.
        AddCuboid(player, skin, 0, 17, 0, 8, 8, 8, Uv(0, 0, 8, 8, 8), doubleSided: false);
        AddCuboid(player, skin, 0, 7, 0, 8, 12, 4, Uv(16, 16, 8, 12, 4), doubleSided: false);
        AddCuboid(player, skin, -armCenter, 7, 0, armWidth, 12, 4, Uv(40, 16, armWidth, 12, 4), doubleSided: false);
        AddCuboid(player, skin, armCenter, 7, 0, armWidth, 12, 4, Uv(32, 48, armWidth, 12, 4), doubleSided: false);
        AddCuboid(player, skin, -1.9, -5, -0.1, 4, 12, 4, Uv(0, 16, 4, 12, 4), doubleSided: false);
        AddCuboid(player, skin, 1.9, -5, -0.1, 4, 12, 4, Uv(16, 48, 4, 12, 4), doubleSided: false);

        // Only native 1.8+ skins have authored overlay geometry. Legacy 64x32 skins are
        // normalized for correct left-limb geometry but intentionally keep the six base cuboids.
        if (prepared.HasOuterLayer)
        {
            // Transparent pixels stay transparent, and opaque legacy-style garbage is cleared in
            // PrepareSkinForRendering before these slightly enlarged cuboids are created.
            AddCuboid(player, skin, 0, 17, 0, 9.0, 9.0, 9.0, Uv(32, 0, 8, 8, 8), doubleSided: true);
            AddCuboid(player, skin, 0, 7, 0, 8.5, 12.5, 4.5, Uv(16, 32, 8, 12, 4), doubleSided: true);
            AddCuboid(player, skin, -armCenter, 7, 0, armWidth + 0.5, 12.5, 4.5, Uv(40, 32, armWidth, 12, 4), doubleSided: true);
            AddCuboid(player, skin, armCenter, 7, 0, armWidth + 0.5, 12.5, 4.5, Uv(48, 48, armWidth, 12, 4), doubleSided: true);
            AddCuboid(player, skin, -1.9, -5, -0.1, 4.5, 12.5, 4.5, Uv(0, 32, 4, 12, 4), doubleSided: true);
            AddCuboid(player, skin, 1.9, -5, -0.1, 4.5, 12.5, 4.5, Uv(0, 48, 4, 12, 4), doubleSided: true);
        }

        _rotation = new AxisAngleRotation3D(new Vector3D(0, 1, 0), DefaultYaw);
        player.Transform = new RotateTransform3D(_rotation, new Point3D(0, PlayerCenterY, 0));

        _scene.Children.Add(player);
        _placeholder.Visibility = Visibility.Collapsed;
    }

    private static void AddCuboid(
        Model3DGroup player,
        BitmapSource skin,
        double cx,
        double cy,
        double cz,
        double width,
        double height,
        double depth,
        CuboidUv uv,
        bool doubleSided)
    {
        player.Children.Add(CreateCuboid(
            skin,
            cx,
            cy,
            cz,
            width,
            height,
            depth,
            uv.Front,
            uv.Back,
            uv.Left,
            uv.Right,
            uv.Top,
            uv.Bottom,
            doubleSided));
    }

    /// <summary>
    /// Minecraft's skin atlas is a wrapped cuboid net. This is the same logical mapping used by
    /// skinview3d: -X receives the first side strip, +X the second side strip, while front/back
    /// keep their natural orientation. Using one generic mapper prevents left/right limb swaps.
    /// </summary>
    private static CuboidUv Uv(int u, int v, int width, int height, int depth)
        => new(
            Front: R(u + depth, v + depth, width, height),
            Back: R(u + width + depth * 2, v + depth, width, height),
            Left: R(u, v + depth, depth, height),
            Right: R(u + width + depth, v + depth, depth, height),
            Top: R(u + depth, v, width, depth),
            Bottom: R(u + width + depth, v, width, depth));

    private static PreparedSkin PrepareSkinForRendering(BitmapSource source)
    {
        var scale = Math.Max(1, source.PixelWidth / 64);
        var sourceWidth = source.PixelWidth;
        var sourceHeight = source.PixelHeight;
        var sourcePixels = CopyBgra32(source, out var sourceStride);
        var modern = sourceHeight == sourceWidth;

        byte[] pixels;
        int stride;

        if (modern)
        {
            pixels = sourcePixels;
            stride = sourceStride;

            // skinview-utils fixes completely opaque 1.8+ skins by clearing every layer-2 area.
            // Without this, WPF renders opaque garbage as a giant hat/jacket/sleeves over the model.
            if (!HasTransparency(pixels, stride, 0, 0, sourceWidth, sourceHeight))
                ClearModernOuterLayer(pixels, stride, scale);
        }
        else
        {
            var targetHeight = sourceWidth;
            stride = checked(sourceWidth * 4);
            pixels = new byte[checked(stride * targetHeight)];

            for (var y = 0; y < sourceHeight; y++)
            {
                Buffer.BlockCopy(sourcePixels, y * sourceStride, pixels, y * stride, Math.Min(sourceStride, stride));
            }

            // Legacy 64x32 skins only contain right limbs. skinview3d mirrors them into the 1.8
            // left-limb slots before rendering, so do the same here.
            ConvertLegacyLimbsToModern(pixels, stride, scale);

            if (!HasTransparency(sourcePixels, sourceStride, 0, 0, sourceWidth, sourceHeight))
                ClearHeadOuterLayer(pixels, stride, scale);
        }

        var texture = BitmapSource.Create(
            sourceWidth,
            sourceWidth,
            source.DpiX > 0 ? source.DpiX : 96,
            source.DpiY > 0 ? source.DpiY : 96,
            PixelFormats.Bgra32,
            null,
            pixels,
            stride);
        RenderOptions.SetBitmapScalingMode(texture, BitmapScalingMode.NearestNeighbor);
        texture.Freeze();

        var slim = modern && InferSlimModel(pixels, stride, scale);
        return new PreparedSkin(texture, slim, modern);
    }

    private static byte[] CopyBgra32(BitmapSource source, out int stride)
    {
        BitmapSource converted = source.Format == PixelFormats.Bgra32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);

        stride = checked(converted.PixelWidth * 4);
        var pixels = new byte[checked(stride * converted.PixelHeight)];
        converted.CopyPixels(pixels, stride, 0);
        return pixels;
    }

    private static void ConvertLegacyLimbsToModern(byte[] pixels, int stride, int scale)
    {
        // Left leg base.
        CopyLogicalRegionMirrored(pixels, stride, scale, 4, 16, 4, 4, 20, 48);
        CopyLogicalRegionMirrored(pixels, stride, scale, 8, 16, 4, 4, 24, 48);
        CopyLogicalRegionMirrored(pixels, stride, scale, 0, 20, 4, 12, 24, 52);
        CopyLogicalRegionMirrored(pixels, stride, scale, 4, 20, 4, 12, 20, 52);
        CopyLogicalRegionMirrored(pixels, stride, scale, 8, 20, 4, 12, 16, 52);
        CopyLogicalRegionMirrored(pixels, stride, scale, 12, 20, 4, 12, 28, 52);

        // Left arm base.
        CopyLogicalRegionMirrored(pixels, stride, scale, 44, 16, 4, 4, 36, 48);
        CopyLogicalRegionMirrored(pixels, stride, scale, 48, 16, 4, 4, 40, 48);
        CopyLogicalRegionMirrored(pixels, stride, scale, 40, 20, 4, 12, 40, 52);
        CopyLogicalRegionMirrored(pixels, stride, scale, 44, 20, 4, 12, 36, 52);
        CopyLogicalRegionMirrored(pixels, stride, scale, 48, 20, 4, 12, 32, 52);
        CopyLogicalRegionMirrored(pixels, stride, scale, 52, 20, 4, 12, 44, 52);
    }

    private static void CopyLogicalRegionMirrored(
        byte[] pixels,
        int stride,
        int scale,
        int sourceX,
        int sourceY,
        int width,
        int height,
        int destinationX,
        int destinationY)
    {
        var sx = sourceX * scale;
        var sy = sourceY * scale;
        var w = width * scale;
        var h = height * scale;
        var dx = destinationX * scale;
        var dy = destinationY * scale;
        var temp = new byte[checked(w * h * 4)];

        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var sourceOffset = (sy + y) * stride + (sx + x) * 4;
                var tempOffset = (y * w + (w - 1 - x)) * 4;
                Buffer.BlockCopy(pixels, sourceOffset, temp, tempOffset, 4);
            }
        }

        for (var y = 0; y < h; y++)
        {
            Buffer.BlockCopy(temp, y * w * 4, pixels, (dy + y) * stride + dx * 4, w * 4);
        }
    }

    private static void ClearModernOuterLayer(byte[] pixels, int stride, int scale)
    {
        // Head layer 2.
        ClearLogicalArea(pixels, stride, scale, 40, 0, 8, 8);
        ClearLogicalArea(pixels, stride, scale, 48, 0, 8, 8);
        ClearLogicalArea(pixels, stride, scale, 32, 8, 8, 8);
        ClearLogicalArea(pixels, stride, scale, 40, 8, 8, 8);
        ClearLogicalArea(pixels, stride, scale, 48, 8, 8, 8);
        ClearLogicalArea(pixels, stride, scale, 56, 8, 8, 8);

        // Right leg layer 2.
        ClearLogicalArea(pixels, stride, scale, 4, 32, 4, 4);
        ClearLogicalArea(pixels, stride, scale, 8, 32, 4, 4);
        ClearLogicalArea(pixels, stride, scale, 0, 36, 4, 12);
        ClearLogicalArea(pixels, stride, scale, 4, 36, 4, 12);
        ClearLogicalArea(pixels, stride, scale, 8, 36, 4, 12);
        ClearLogicalArea(pixels, stride, scale, 12, 36, 4, 12);

        // Body layer 2.
        ClearLogicalArea(pixels, stride, scale, 20, 32, 8, 4);
        ClearLogicalArea(pixels, stride, scale, 28, 32, 8, 4);
        ClearLogicalArea(pixels, stride, scale, 16, 36, 4, 12);
        ClearLogicalArea(pixels, stride, scale, 20, 36, 8, 12);
        ClearLogicalArea(pixels, stride, scale, 28, 36, 4, 12);
        ClearLogicalArea(pixels, stride, scale, 32, 36, 8, 12);

        // Right arm layer 2. Clear the trailing unused strip too, matching skinview-utils.
        ClearLogicalArea(pixels, stride, scale, 44, 32, 4, 4);
        ClearLogicalArea(pixels, stride, scale, 48, 32, 4, 4);
        ClearLogicalArea(pixels, stride, scale, 40, 36, 4, 12);
        ClearLogicalArea(pixels, stride, scale, 44, 36, 4, 12);
        ClearLogicalArea(pixels, stride, scale, 48, 36, 4, 12);
        ClearLogicalArea(pixels, stride, scale, 52, 36, 12, 12);

        // Left leg layer 2.
        ClearLogicalArea(pixels, stride, scale, 4, 48, 4, 4);
        ClearLogicalArea(pixels, stride, scale, 8, 48, 4, 4);
        ClearLogicalArea(pixels, stride, scale, 0, 52, 4, 12);
        ClearLogicalArea(pixels, stride, scale, 4, 52, 4, 12);
        ClearLogicalArea(pixels, stride, scale, 8, 52, 4, 12);
        ClearLogicalArea(pixels, stride, scale, 12, 52, 4, 12);

        // Left arm layer 2.
        ClearLogicalArea(pixels, stride, scale, 52, 48, 4, 4);
        ClearLogicalArea(pixels, stride, scale, 56, 48, 4, 4);
        ClearLogicalArea(pixels, stride, scale, 48, 52, 4, 12);
        ClearLogicalArea(pixels, stride, scale, 52, 52, 4, 12);
        ClearLogicalArea(pixels, stride, scale, 56, 52, 4, 12);
        ClearLogicalArea(pixels, stride, scale, 60, 52, 4, 12);
    }

    private static void ClearHeadOuterLayer(byte[] pixels, int stride, int scale)
    {
        ClearLogicalArea(pixels, stride, scale, 40, 0, 8, 8);
        ClearLogicalArea(pixels, stride, scale, 48, 0, 8, 8);
        ClearLogicalArea(pixels, stride, scale, 32, 8, 8, 8);
        ClearLogicalArea(pixels, stride, scale, 40, 8, 8, 8);
        ClearLogicalArea(pixels, stride, scale, 48, 8, 8, 8);
        ClearLogicalArea(pixels, stride, scale, 56, 8, 8, 8);
    }

    private static void ClearLogicalArea(
        byte[] pixels,
        int stride,
        int scale,
        int x,
        int y,
        int width,
        int height)
    {
        var x0 = x * scale;
        var y0 = y * scale;
        var w = width * scale;
        var h = height * scale;

        for (var py = y0; py < y0 + h; py++)
        {
            for (var px = x0; px < x0 + w; px++)
            {
                var offset = py * stride + px * 4;
                pixels[offset] = 0;
                pixels[offset + 1] = 0;
                pixels[offset + 2] = 0;
                pixels[offset + 3] = 0;
            }
        }
    }

    private static bool InferSlimModel(byte[] pixels, int stride, int scale)
    {
        var areas = new[]
        {
            (X: 50, Y: 16, W: 2, H: 4),
            (X: 54, Y: 20, W: 2, H: 12),
            (X: 42, Y: 48, W: 2, H: 4),
            (X: 46, Y: 52, W: 2, H: 12)
        };

        foreach (var area in areas)
        {
            if (HasTransparency(
                    pixels,
                    stride,
                    area.X * scale,
                    area.Y * scale,
                    area.W * scale,
                    area.H * scale))
            {
                return true;
            }
        }

        var allBlack = true;
        var allWhite = true;
        foreach (var area in areas)
        {
            allBlack &= IsSolidArea(pixels, stride, scale, area.X, area.Y, area.W, area.H, 0x00);
            allWhite &= IsSolidArea(pixels, stride, scale, area.X, area.Y, area.W, area.H, 0xff);
        }

        return allBlack || allWhite;
    }

    private static bool IsSolidArea(
        byte[] pixels,
        int stride,
        int scale,
        int x,
        int y,
        int width,
        int height,
        byte value)
    {
        var x0 = x * scale;
        var y0 = y * scale;
        var w = width * scale;
        var h = height * scale;

        for (var py = y0; py < y0 + h; py++)
        {
            for (var px = x0; px < x0 + w; px++)
            {
                var offset = py * stride + px * 4;
                if (pixels[offset] != value ||
                    pixels[offset + 1] != value ||
                    pixels[offset + 2] != value ||
                    pixels[offset + 3] != 0xff)
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static bool HasTransparency(byte[] pixels, int stride, int x, int y, int width, int height)
    {
        for (var py = y; py < y + height; py++)
        {
            for (var px = x; px < x + width; px++)
            {
                if (pixels[py * stride + px * 4 + 3] != 0xff)
                    return true;
            }
        }

        return false;
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
        Int32Rect bottom,
        bool doubleSided)
    {
        var x0 = cx - width / 2.0;
        var x1 = cx + width / 2.0;
        var y0 = cy - height / 2.0;
        var y1 = cy + height / 2.0;
        var z0 = cz - depth / 2.0;
        var z1 = cz + depth / 2.0;

        var group = new Model3DGroup();
        group.Children.Add(CreateFace(skin, front,
            new Point3D(x0, y0, z1), new Point3D(x1, y0, z1), new Point3D(x1, y1, z1), new Point3D(x0, y1, z1),
            doubleSided));
        group.Children.Add(CreateFace(skin, back,
            new Point3D(x1, y0, z0), new Point3D(x0, y0, z0), new Point3D(x0, y1, z0), new Point3D(x1, y1, z0),
            doubleSided));
        group.Children.Add(CreateFace(skin, left,
            new Point3D(x0, y0, z0), new Point3D(x0, y0, z1), new Point3D(x0, y1, z1), new Point3D(x0, y1, z0),
            doubleSided));
        group.Children.Add(CreateFace(skin, right,
            new Point3D(x1, y0, z1), new Point3D(x1, y0, z0), new Point3D(x1, y1, z0), new Point3D(x1, y1, z1),
            doubleSided));
        group.Children.Add(CreateFace(skin, top,
            new Point3D(x0, y1, z1), new Point3D(x1, y1, z1), new Point3D(x1, y1, z0), new Point3D(x0, y1, z0),
            doubleSided));
        group.Children.Add(CreateFace(skin, bottom,
            new Point3D(x0, y0, z0), new Point3D(x1, y0, z0), new Point3D(x1, y0, z1), new Point3D(x0, y0, z1),
            doubleSided));
        return group;
    }

    private static GeometryModel3D CreateFace(
        BitmapSource skin,
        Int32Rect logicalRegion,
        Point3D p0,
        Point3D p1,
        Point3D p2,
        Point3D p3,
        bool doubleSided)
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
            BackMaterial = doubleSided ? material : null
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
            var fallback = new SolidColorBrush(Color.FromRgb(120, 104, 146));
            fallback.Freeze();
            var fallbackMaterial = new DiffuseMaterial(fallback);
            fallbackMaterial.Freeze();
            return fallbackMaterial;
        }

        var crop = new CroppedBitmap(skin, region);
        crop.Freeze();
        var sharp = CreateNearestNeighbourTexture(crop);

        var brush = new ImageBrush(sharp)
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

    private static BitmapSource CreateNearestNeighbourTexture(BitmapSource source)
    {
        var converted = source.Format == PixelFormats.Bgra32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);

        var width = converted.PixelWidth;
        var height = converted.PixelHeight;
        var sourceStride = checked(width * 4);
        var sourcePixels = new byte[checked(sourceStride * height)];
        converted.CopyPixels(sourcePixels, sourceStride, 0);

        var targetWidth = checked(width * TextureUpscale);
        var targetHeight = checked(height * TextureUpscale);
        var targetStride = checked(targetWidth * 4);
        var targetPixels = new byte[checked(targetStride * targetHeight)];

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var sourceOffset = y * sourceStride + x * 4;
                for (var oy = 0; oy < TextureUpscale; oy++)
                {
                    var row = (y * TextureUpscale + oy) * targetStride;
                    for (var ox = 0; ox < TextureUpscale; ox++)
                    {
                        var targetOffset = row + (x * TextureUpscale + ox) * 4;
                        targetPixels[targetOffset] = sourcePixels[sourceOffset];
                        targetPixels[targetOffset + 1] = sourcePixels[sourceOffset + 1];
                        targetPixels[targetOffset + 2] = sourcePixels[sourceOffset + 2];
                        targetPixels[targetOffset + 3] = sourcePixels[sourceOffset + 3];
                    }
                }
            }
        }

        var bitmap = BitmapSource.Create(
            targetWidth,
            targetHeight,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            targetPixels,
            targetStride);
        RenderOptions.SetBitmapScalingMode(bitmap, BitmapScalingMode.NearestNeighbor);
        bitmap.Freeze();
        return bitmap;
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

        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }
}
