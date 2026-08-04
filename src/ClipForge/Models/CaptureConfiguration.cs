namespace ClipForge.Models;

public sealed record CaptureConfiguration(
    DisplayOption Display,
    ResolutionOption Resolution,
    int FramesPerSecond,
    TimeSpan Retention,
    bool CaptureCursor,
    bool CaptureSystemAudio,
    AudioDeviceOption? OutputAudioDevice,
    bool CaptureMicrophone,
    AudioDeviceOption? MicrophoneDevice,
    string SaveDirectory)
{
    public CaptureSessionMode SessionMode { get; init; } =
        CaptureSessionMode.InstantReplay;

    /// <summary>
    /// Keeps one encoded width/height across a full-session recording even if
    /// an exclusive-fullscreen game changes the desktop mode underneath WGC.
    /// </summary>
    public bool LockOutputGeometry { get; init; }

    public int? LockedOutputWidth { get; init; }

    public int? LockedOutputHeight { get; init; }
}
