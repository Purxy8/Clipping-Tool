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
    /// <summary>
    /// Distinguishes an original replay save from a ClipForge-generated trim.
    /// The library assigns this from the strict generated-file-name grammar;
    /// callers should not infer ownership from arbitrary MP4 names.
    /// </summary>
    public ClipKind Kind { get; init; } = ClipKind.Original;

    public bool IsTrimmed => Kind == ClipKind.Trimmed;

    public DateTimeOffset RecordedAtLocal => RecordedAtUtc.ToLocalTime();

    /// <summary>
    /// Compact binary-megabyte size suitable for the recent-clips gallery.
    /// The stored byte count is used directly, so binding this property never
    /// performs extra file-system work on the UI thread.
    /// </summary>
    public string FileSizeDisplay => FormatFileSize(FileSizeBytes);

    internal static string FormatFileSize(long fileSizeBytes)
    {
        if (fileSizeBytes <= 0)
        {
            return "0 MB";
        }

        const double bytesPerMegabyte = 1024d * 1024d;
        var megabytes = fileSizeBytes / bytesPerMegabyte;
        return megabytes < 1
            ? "<1 MB"
            : FormattableString.Invariant($"{megabytes:0.0} MB");
    }

    /// <summary>
    /// Stable Windows identity captured while the item is discovered. It is kept
    /// internal because it is security metadata, not presentation state.
    /// </summary>
    internal ClipFileIdentity? FileIdentity { get; init; }
}

public enum ClipKind
{
    Original,
    Trimmed
}

public enum ClipLibraryFilter
{
    All,
    Original,
    Trimmed
}

public enum ClipThumbnailPolicy
{
    GenerateMissing,
    CachedOnly,
    None
}

internal readonly record struct ClipFileIdentity(
    ulong VolumeSerialNumber,
    ulong FileIdLow,
    ulong FileIdHigh,
    uint NumberOfLinks);
