using ClipForge.Capture;
using ClipForge.Models;

namespace ClipForge.Services;

public static class StorageEstimator
{
    public static long EstimateBufferBytes(
        DisplayOption display,
        ResolutionOption resolution,
        int framesPerSecond,
        TimeSpan duration,
        bool hasAudio)
    {
        var output = CaptureGeometry.ResolveOutputSize(display, resolution);
        var pixelsPerSecond = (double)output.Width * output.Height * framesPerSecond;

        // A conservative H.264 target for screen/game content. The capture engine uses
        // quality-based encoding, so this is intentionally an estimate rather than a quota.
        var videoBitsPerSecond = Math.Clamp(pixelsPerSecond * 0.14, 3_000_000, 55_000_000);
        var audioBitsPerSecond = hasAudio ? 192_000 : 0;
        return checked((long)Math.Ceiling((videoBitsPerSecond + audioBitsPerSecond) * duration.TotalSeconds / 8d));
    }

    public static string FormatBytes(long bytes)
    {
        string[] suffixes = ["B", "KB", "MB", "GB", "TB"];
        var value = Math.Max(0, (double)bytes);
        var suffix = 0;

        while (value >= 1024 && suffix < suffixes.Length - 1)
        {
            value /= 1024;
            suffix++;
        }

        return suffix == 0
            ? $"{value:0} {suffixes[suffix]}"
            : $"{value:0.#} {suffixes[suffix]}";
    }
}
