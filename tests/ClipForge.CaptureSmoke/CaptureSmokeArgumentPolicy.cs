namespace ClipForge.Testing;

internal static class CaptureSmokeArgumentPolicy
{
    private static readonly string[] InteractiveMatrixForwardedSwitches =
    [
        "--audio",
        "--microphone",
        "--force-gdi",
        "--force-wgc",
        "--prune",
        "--motion-validation",
        "--print-probe-diagnostics"
    ];

    internal static IReadOnlyList<string> GetInteractiveMatrixForwardedSwitches(
        IEnumerable<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var requested = arguments.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return InteractiveMatrixForwardedSwitches
            .Where(requested.Contains)
            .ToArray();
    }
}
