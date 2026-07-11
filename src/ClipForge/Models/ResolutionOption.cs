namespace ClipForge.Models;

public sealed record ResolutionOption(string Id, string Label, int? Width, int? Height)
{
    public static IReadOnlyList<ResolutionOption> All { get; } =
    [
        new("source", "Source / native", null, null),
        new("720p", "720p HD", 1280, 720),
        new("1080p", "1080p Full HD", 1920, 1080),
        new("1440p", "1440p QHD", 2560, 1440),
        new("2160p", "2160p 4K", 3840, 2160)
    ];

    public override string ToString() => Label;
}

