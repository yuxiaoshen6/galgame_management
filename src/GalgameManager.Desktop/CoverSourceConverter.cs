using System.Globalization;
using System.IO;
using System.Windows.Data;
using System.Windows.Media.Imaging;

namespace GalgameManager.Desktop;

public sealed class CoverSourceConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        Load(value as string, int.TryParse(parameter as string, out var width) ? width : 380);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    internal static BitmapSource? Load(string? path, int width)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        try
        {
            using var stream = File.OpenRead(path);
            if (stream.Length > 8 * 1024 * 1024)
                return null;
            var image = new BitmapImage();
            image.BeginInit();
            image.StreamSource = stream;
            image.DecodePixelWidth = width;
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or
            FormatException or NotSupportedException)
        {
            return null;
        }
    }
}
