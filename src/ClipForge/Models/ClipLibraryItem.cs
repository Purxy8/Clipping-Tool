namespace ClipForge.Models;

/// <summary>
/// A captured MP4 that was safely discovered in the configured clips directory.
/// </summary>
public sealed record ClipLibraryItem(
    string FileName,
    string FullPath,
    DateTimeOffset RecordedAtUtc,
    long FileSizeBytes,
    TimeSpan? Duration,
    string? ThumbnailPath = null)
{
    public DateTimeOffset RecordedAtLocal => RecordedAtUtc.ToLocalTime();

    /// <summary>
    /// Stable Windows identity captured while the item is discovered. It is kept
    /// internal because it is security metadata, not presentation state.
    /// </summary>
    internal ClipFileIdentity? FileIdentity { get; init; }
}

internal readonly record struct ClipFileIdentity(
    ulong VolumeSerialNumber,
    ulong FileIdLow,
    ulong FileIdHigh,
    uint NumberOfLinks);
