namespace ClipForge.Models;

public sealed class AppSettings
{
    public const string DefaultBackgroundColor = "#0B0D12";
    public const string DefaultAccentColor = "#7C6CF2";
    public const string DefaultSurfaceColor = "#12151D";

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
    public bool PlayClipSavedSound { get; set; } = true;
    public string BackgroundColor { get; set; } = DefaultBackgroundColor;
    public string AccentColor { get; set; } = DefaultAccentColor;
    public string SurfaceColor { get; set; } = DefaultSurfaceColor;
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

    internal static string NormalizeBackgroundColor(string? requestedColor) =>
        AppearanceColorPolicy.NormalizeDarkColor(requestedColor, DefaultBackgroundColor);

    internal static string NormalizeSurfaceColor(string? requestedColor) =>
        AppearanceColorPolicy.NormalizeDarkColor(requestedColor, DefaultSurfaceColor);

    internal static string NormalizeAccentColor(string? requestedColor) =>
        AppearanceColorPolicy.NormalizeAccentColor(requestedColor, DefaultAccentColor);

    internal static int NormalizeRecentClipCount(int requestedCount) =>
        AllowedRecentClipCounts.Contains(requestedCount) ? requestedCount : 4;
}
