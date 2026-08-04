namespace ClipForge.Models;

public sealed record DisplayOption(
    string DeviceName,
    string Label,
    int Left,
    int Top,
    int Width,
    int Height,
    bool IsPrimary,
    int MonitorIndex = 0,
    int RefreshRateHz = 0)
{
    public override string ToString() => RefreshRateHz > 0
        ? $"{Label} · {Width}×{Height} @ {RefreshRateHz} Hz"
        : $"{Label} · {Width}×{Height}";
}
