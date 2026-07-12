using System.Globalization;

namespace ClipForge.Models;

public sealed class AppSettings
{
    public const string DefaultBackgroundColor = "#0B0D12";

    private static readonly int[] AllowedRecentClipCounts = [4, 8, 10, 15];

    public int ReplaySeconds { get; set; } = 120;
    public string ResolutionId { get; set; } = "1080p";
    public int FramesPerSecond { get; set; } = 30;
    public string? DisplayDeviceName { get; set; }
    public bool CaptureSystemAudio { get; set; } = true;
    public string? OutputAudioDeviceId { get; set; }
    public bool CaptureMicrophone { get; set; }
    public string? MicrophoneDeviceId { get; set; }
    public bool CheckForUpdatesAutomatically { get; set; }
    public string BackgroundColor { get; set; } = DefaultBackgroundColor;
    public int RecentClipCount { get; set; } = 4;
    public HotkeyGesture SaveClipHotkey { get; set; } = HotkeyGesture.DefaultSaveClip;
    public HotkeyGesture ToggleOverlayHotkey { get; set; } = HotkeyGesture.DefaultToggleOverlay;
    public string SaveDirectory { get; set; } = GetDefaultSaveDirectory();

    public static string GetDefaultSaveDirectory()
    {
        var videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        if (string.IsNullOrWhiteSpace(videos))
        {
            videos = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        }

        return Path.Combine(videos, "ClipForge");
    }

    internal static string NormalizeBackgroundColor(string? requestedColor)
    {
        if (requestedColor is null ||
            requestedColor.Length != 7 ||
            requestedColor[0] != '#' ||
            !byte.TryParse(requestedColor.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var red) ||
            !byte.TryParse(requestedColor.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var green) ||
            !byte.TryParse(requestedColor.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var blue))
        {
            return DefaultBackgroundColor;
        }

        // The interface uses fixed light typography, so preserve the requested hue
        // while keeping the brightest channel within the tested dark-shell range.
        const byte maximumChannel = 48;
        var brightestChannel = Math.Max(red, Math.Max(green, blue));
        if (brightestChannel > maximumChannel)
        {
            var scale = maximumChannel / (double)brightestChannel;
            red = (byte)Math.Round(red * scale, MidpointRounding.AwayFromZero);
            green = (byte)Math.Round(green * scale, MidpointRounding.AwayFromZero);
            blue = (byte)Math.Round(blue * scale, MidpointRounding.AwayFromZero);
        }

        return $"#{red:X2}{green:X2}{blue:X2}";
    }

    internal static int NormalizeRecentClipCount(int requestedCount) =>
        AllowedRecentClipCounts.Contains(requestedCount) ? requestedCount : 4;
}
