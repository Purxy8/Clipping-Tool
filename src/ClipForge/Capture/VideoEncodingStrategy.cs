namespace ClipForge.Capture;

internal enum VideoEncoderKind
{
    NvidiaNvenc,
    IntelQuickSync,
    AmdAmf,
    SoftwareX264
}

internal enum DesktopCaptureBackend
{
    WindowsGraphicsCapture,
    Gdi
}

internal sealed record VideoEncodingStrategy(
    VideoEncoderKind Encoder,
    DesktopCaptureBackend CaptureBackend,
    bool RequiresSystemMemoryTransfer = false)
{
    public static VideoEncodingStrategy SoftwareGdi { get; } = new(
        VideoEncoderKind.SoftwareX264,
        DesktopCaptureBackend.Gdi);

    public string FfmpegEncoder => Encoder switch
    {
        VideoEncoderKind.NvidiaNvenc => "h264_nvenc",
        VideoEncoderKind.IntelQuickSync => "h264_qsv",
        VideoEncoderKind.AmdAmf => "h264_amf",
        _ => "libx264"
    };

    public string EncoderName => Encoder switch
    {
        VideoEncoderKind.NvidiaNvenc => "NVIDIA NVENC",
        VideoEncoderKind.IntelQuickSync => "Intel Quick Sync",
        VideoEncoderKind.AmdAmf => "AMD AMF",
        _ => "software H.264"
    };

    public string CaptureBackendName => CaptureBackend switch
    {
        DesktopCaptureBackend.WindowsGraphicsCapture when RequiresSystemMemoryTransfer =>
            "Windows Graphics Capture compatibility transfer",
        DesktopCaptureBackend.WindowsGraphicsCapture => "Windows Graphics Capture",
        _ => "GDI compatibility capture"
    };

    public string Description => $"{EncoderName} via {CaptureBackendName}";

    public bool IsHardwareEncoder => Encoder != VideoEncoderKind.SoftwareX264;
}
