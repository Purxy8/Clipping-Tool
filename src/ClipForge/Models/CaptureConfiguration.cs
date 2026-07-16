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
    string SaveDirectory);
