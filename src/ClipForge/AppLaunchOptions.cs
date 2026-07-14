namespace ClipForge;

internal sealed record AppLaunchOptions(bool IsAutoStart)
{
    public const string AutoStartArgument = "--autostart";

    public static AppLaunchOptions Interactive { get; } = new(false);

    public bool StartInBackground => IsAutoStart;

    public bool ShouldActivateExistingInstance => !IsAutoStart;

    public static AppLaunchOptions Parse(IEnumerable<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        return new AppLaunchOptions(arguments.Any(argument =>
            string.Equals(argument, AutoStartArgument, StringComparison.OrdinalIgnoreCase)));
    }
}
