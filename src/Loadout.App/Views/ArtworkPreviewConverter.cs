using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media.Imaging;
using Loadout.Artwork;

namespace Loadout.App;

public sealed class ArtworkPreviewConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return DependencyProperty.UnsetValue;
            }

            value = ArtworkPreview.FromFile(path);
        }

        if (value is not ArtworkPreview preview)
        {
            return DependencyProperty.UnsetValue;
        }

        try
        {
            if (preview.Bytes is { Length: > 0 } bytes)
            {
                return Load(bytes);
            }

            if (!string.IsNullOrWhiteSpace(preview.FilePath) && File.Exists(preview.FilePath))
            {
                var fileBytes = File.ReadAllBytes(preview.FilePath);
                if (fileBytes.Length == 0)
                {
                    return DependencyProperty.UnsetValue;
                }

                return Load(fileBytes);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return DependencyProperty.UnsetValue;
        }

        return DependencyProperty.UnsetValue;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();

    private static BitmapImage Load(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }
}
