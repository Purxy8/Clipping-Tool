using System.Globalization;
using System.Windows.Data;
using System.Windows.Media.Imaging;

namespace ClipForge;

/// <summary>
/// Decodes a bounded local JPEG into memory and closes the file immediately.
/// WPF's default URI loader is on-demand and can otherwise keep a deleted
/// clip's cached thumbnail locked after its gallery card is removed.
/// </summary>
public sealed class ThumbnailPathConverter : IValueConverter
{
    private const long MaximumThumbnailBytes = 16 * 1024 * 1024;
    private const int DecodePixelWidth = 640;

    public object? Convert(
        object? value,
        Type targetType,
        object? parameter,
        CultureInfo culture)
    {
        if (value is not string path ||
            string.IsNullOrWhiteSpace(path) ||
            !Path.IsPathFullyQualified(path))
        {
            return null;
        }

        try
        {
            var thumbnail = new FileInfo(Path.GetFullPath(path));
            if (!thumbnail.Exists ||
                thumbnail.Length is < 5 or > MaximumThumbnailBytes ||
                (thumbnail.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            {
                return null;
            }

            using var stream = new FileStream(
                thumbnail.FullName,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read | FileShare.Delete,
                bufferSize: 64 * 1024,
                FileOptions.SequentialScan);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = DecodePixelWidth;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or
                NotSupportedException or InvalidOperationException or FileFormatException)
        {
            return null;
        }
    }

    public object ConvertBack(
        object? value,
        Type targetType,
        object? parameter,
        CultureInfo culture) => throw new NotSupportedException();
}
