namespace ClipForge.Models;

public sealed record ReplayLengthOption(string Label, TimeSpan Duration)
{
    public static IReadOnlyList<ReplayLengthOption> All { get; } =
    [
        new("30 seconds", TimeSpan.FromSeconds(30)),
        new("1 minute", TimeSpan.FromMinutes(1)),
        new("2 minutes", TimeSpan.FromMinutes(2)),
        new("3 minutes", TimeSpan.FromMinutes(3)),
        new("5 minutes", TimeSpan.FromMinutes(5)),
        new("10 minutes", TimeSpan.FromMinutes(10)),
        new("20 minutes", TimeSpan.FromMinutes(20)),
        new("30 minutes", TimeSpan.FromMinutes(30)),
        new("40 minutes", TimeSpan.FromMinutes(40)),
        new("1 hour", TimeSpan.FromHours(1))
    ];

    public override string ToString() => Label;
}

