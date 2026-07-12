namespace ClipForge.Models;

public sealed class AppSettings
{
    public int ReplaySeconds { get; set; } = 120;
    public string ResolutionId { get; set; } = "1080p";
    public int FramesPerSecond { get; set; } = 30;
    public string? DisplayDeviceName { get; set; }
    public bool CaptureSystemAudio { get; set; } = true;
    public string? OutputAudioDeviceId { get; set; }
    public bool CaptureMicrophone { get; set; }
    public string? MicrophoneDeviceId { get; set; }
    public bool CheckForUpdatesAutomatically { get; set; } = true;
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
}
