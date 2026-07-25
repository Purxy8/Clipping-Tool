namespace ClipForge.Testing;

internal static class RenewalSegmentSequence
{
    internal static void AssertMonotonicWithKnownQuarantinedHeads(
        IReadOnlyList<int> segmentIds,
        IReadOnlySet<int> knownQuarantinedHeads,
        string phase)
    {
        ArgumentNullException.ThrowIfNull(segmentIds);
        ArgumentNullException.ThrowIfNull(knownQuarantinedHeads);
        ArgumentException.ThrowIfNullOrWhiteSpace(phase);

        for (var index = 1; index < segmentIds.Count; index++)
        {
            var previous = segmentIds[index - 1];
            var current = segmentIds[index];
            if (current <= previous)
            {
                throw new InvalidDataException(
                    $"Replay segment ids were not strictly increasing {phase}: " +
                    $"{previous} -> {current}.");
            }

            var missingCount = (long)current - previous - 1;
            if (missingCount == 0)
            {
                continue;
            }

            var knownGapCount = knownQuarantinedHeads.Count(
                head => head > previous && head < current);
            if (knownGapCount != missingCount)
            {
                throw new InvalidDataException(
                    $"Replay segment ids contained an unexplained gap {phase}: " +
                    $"{previous} -> {current}; only known capture-generation heads " +
                    "may be absent.");
            }
        }
    }
}
