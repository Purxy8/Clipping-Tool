namespace ClipForge.Models;

public sealed record DisplayOption(
    string DeviceName,
    string Label,
    int Left,
    int Top,
    int Width,
    int Height,
    bool IsPrimary)
{
    public override string ToString() => $"{Label} · {Width}×{Height}";
}

