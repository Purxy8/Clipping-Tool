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
    private const int MaximumCachedThumbnailKeys = 128;
    private static readonly object CacheGate = new();
    private static readonly Dictionary<ThumbnailCacheKey, CachedThumbnail> Cache = [];
    private static long _cacheAccessOrder;

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

            var cacheKey = new ThumbnailCacheKey(
                thumbnail.FullName,
                thumbnail.Length,
                thumbnail.LastWriteTimeUtc.Ticks);
            if (TryGetCachedThumbnail(cacheKey, out var cached))
            {
                return cached;
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
            CacheThumbnail(cacheKey, image);
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

    private static bool TryGetCachedThumbnail(
        ThumbnailCacheKey key,
        out BitmapSource? thumbnail)
    {
        lock (CacheGate)
        {
            if (!Cache.TryGetValue(key, out var cached) ||
                !cached.Reference.TryGetTarget(out thumbnail))
            {
                if (cached.Reference is not null)
                {
                    Cache.Remove(key);
                }

                thumbnail = null;
                return false;
            }

            Cache[key] = cached with { LastAccessOrder = NextCacheAccessOrderLocked() };
            return true;
        }
    }

    private static void CacheThumbnail(ThumbnailCacheKey key, BitmapSource thumbnail)
    {
        lock (CacheGate)
        {
            if (!Cache.ContainsKey(key) && Cache.Count >= MaximumCachedThumbnailKeys)
            {
                foreach (var staleKey in Cache
                             .Where(entry => !entry.Value.Reference.TryGetTarget(out _))
                             .Select(entry => entry.Key)
                             .ToArray())
                {
                    Cache.Remove(staleKey);
                }

                if (Cache.Count >= MaximumCachedThumbnailKeys)
                {
                    var oldestKey = Cache.MinBy(entry => entry.Value.LastAccessOrder).Key;
                    Cache.Remove(oldestKey);
                }
            }

            Cache[key] = new CachedThumbnail(
                new WeakReference<BitmapSource>(thumbnail),
                NextCacheAccessOrderLocked());
        }
    }

    private static long NextCacheAccessOrderLocked()
    {
        if (_cacheAccessOrder == long.MaxValue)
        {
            _cacheAccessOrder = 0;
            foreach (var key in Cache.Keys.ToArray())
            {
                Cache[key] = Cache[key] with { LastAccessOrder = 0 };
            }
        }

        return ++_cacheAccessOrder;
    }

    private readonly record struct ThumbnailCacheKey(
        string FullPath,
        long Length,
        long LastWriteTimeUtcTicks);

    private readonly record struct CachedThumbnail(
        WeakReference<BitmapSource> Reference,
        long LastAccessOrder);
}
