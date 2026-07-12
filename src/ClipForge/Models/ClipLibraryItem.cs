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
    string? ThumbnailPath = null);

