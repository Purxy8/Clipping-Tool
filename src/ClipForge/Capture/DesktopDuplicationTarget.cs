namespace ClipForge.Capture;

/// <summary>
/// A DXGI adapter-local output resolved from the selected Windows display.
/// Adapter/output indices belong to one discovery snapshot; the adapter LUID
/// also participates in capture-plan identity across display topology changes.
/// </summary>
public sealed record DxgiCaptureTarget(
    int AdapterIndex,
    int OutputIndex,
    long AdapterLuid,
    string DeviceName,
    int Rotation);
