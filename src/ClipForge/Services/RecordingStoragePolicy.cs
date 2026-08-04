namespace ClipForge.Services;

internal static class RecordingStoragePolicy
{
    internal static readonly TimeSpan NoReplayRetention = TimeSpan.Zero;
    internal const long MinimumStartFreeBytes = 4L * 1024 * 1024 * 1024;
    internal const long FinalizationSafetyReserveBytes = 2L * 1024 * 1024 * 1024;
    internal const long AutomaticFinalizationHeadroomBytes = 2L * 1024 * 1024 * 1024;

    internal static bool HasStartCapacity(long availableFreeBytes) =>
        availableFreeBytes >= MinimumStartFreeBytes;

    internal static bool ShouldFinalize(
        long sessionBytes,
        long availableFreeBytes) =>
        availableFreeBytes <= AddSaturating(
            GetRequiredFinalizationFreeBytes(sessionBytes),
            AutomaticFinalizationHeadroomBytes);

    internal static long GetRequiredFinalizationFreeBytes(long sessionBytes)
    {
        if (sessionBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sessionBytes));
        }

        return sessionBytes > long.MaxValue - FinalizationSafetyReserveBytes
            ? long.MaxValue
            : sessionBytes + FinalizationSafetyReserveBytes;
    }

    internal static string GetWorkingRoot(string saveDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(saveDirectory);
        return Path.Combine(
            Path.GetFullPath(saveDirectory),
            ".clipforge-recordings");
    }

    private static long AddSaturating(long left, long right) =>
        left > long.MaxValue - right ? long.MaxValue : left + right;
}
