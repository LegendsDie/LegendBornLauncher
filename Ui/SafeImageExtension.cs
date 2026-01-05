using System;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LegendBorn.Ui;

[MarkupExtensionReturnType(typeof(ImageSource))]
public sealed class SafeImageExtension : MarkupExtension
{
    public string? Uri { get; set; }

    public SafeImageExtension() { }
    public SafeImageExtension(string uri) => Uri = uri;

    public override object? ProvideValue(IServiceProvider serviceProvider)
    {
        if (string.IsNullOrWhiteSpace(Uri))
            return null;

        try
        {
            var uri = new Uri(Uri, UriKind.RelativeOrAbsolute);

            var bi = new BitmapImage();
            bi.BeginInit();
            bi.UriSource = uri;
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            bi.EndInit();
            bi.Freeze();

            return bi;
        }
        catch
        {
            return null; // ключевое: НЕ ПАДАЕМ
        }
    }
}