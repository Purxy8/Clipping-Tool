using ClipForge.Capture;

namespace ClipForge.Services;

internal static class RecordingStoragePolicy
{
    internal static readonly TimeSpan NoReplayRetention = TimeSpan.Zero;
    internal const long MinimumStartFreeBytes = 4L * 1024 * 1024 * 1024;
    internal const long FinalizationSafetyReserveBytes = 2L * 1024 * 1024 * 1024;
    internal const long AutomaticFinalizationHeadroomBytes = 2L * 1024 * 1024 * 1024;
    internal const long ReplaySafetyReserveBytes = 1L * 1024 * 1024 * 1024;

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

    internal static long EstimateWorkingBytes(long encodedOutputBytes)
    {
        if (encodedOutputBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(encodedOutputBytes));
        }

        // Recorder writes one authoritative recovery stream and one directly
        // publishable MP4 from the same encode. The estimate describes peak
        // capture-time use, not the single final file retained after cleanup.
        return AddSaturating(encodedOutputBytes, encodedOutputBytes);
    }

    internal static long GetRecommendedStartingFreeBytes(
        long encodedOutputBytes) =>
        AddSaturating(
            EstimateWorkingBytes(encodedOutputBytes),
            AddSaturating(
                GetRequiredFinalizationFreeBytes(encodedOutputBytes),
                AutomaticFinalizationHeadroomBytes));

    internal static long GetRequiredReplayStartFreeBytes(
        long estimatedBufferBytes)
    {
        if (estimatedBufferBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(estimatedBufferBytes));
        }

        return AddSaturating(
            estimatedBufferBytes,
            ReplaySafetyReserveBytes);
    }

    internal static long GetRequiredReplaySaveFreeBytes(
        long selectedSegmentBytes,
        long rollingOverlapBytes,
        bool sharesBufferVolume)
    {
        if (selectedSegmentBytes < 0 || rollingOverlapBytes < 0)
        {
            throw new ArgumentOutOfRangeException(
                selectedSegmentBytes < 0
                    ? nameof(selectedSegmentBytes)
                    : nameof(rollingOverlapBytes));
        }

        // Export creates one output-sized partial. While those source segments
        // are protected, the rolling ring can temporarily retain another window
        // before new unprotected segments become the oldest removable entries.
        // The caller supplies the larger of measured and VBR-estimated overlap.
        // On one volume both costs apply; on separate volumes this method budgets
        // only the output destination and the buffer-side helper budgets growth.
        var transientBytes = sharesBufferVolume
            ? AddSaturating(selectedSegmentBytes, rollingOverlapBytes)
            : selectedSegmentBytes;
        return AddSaturating(transientBytes, ReplaySafetyReserveBytes);
    }

    internal static long GetRequiredReplaySaveBufferFreeBytes(
        long rollingOverlapBytes)
    {
        if (rollingOverlapBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(rollingOverlapBytes));
        }

        return AddSaturating(
            rollingOverlapBytes,
            ReplaySafetyReserveBytes);
    }

    internal static bool ShouldStopReplayForSafety(long? availableFreeBytes) =>
        availableFreeBytes is null or <= ReplaySafetyReserveBytes;

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
