using System.Collections.ObjectModel;

namespace ClipForge.Models;

/// <summary>
/// An immutable, newest-first view of the user's clip library.
/// </summary>
public sealed class ClipLibrarySnapshot
{
    public static ClipLibrarySnapshot Empty { get; } = new([]);

    public ClipLibrarySnapshot(IEnumerable<ClipLibraryItem> clips)
    {
        ArgumentNullException.ThrowIfNull(clips);
        var items = clips.ToArray();
        Clips = new ReadOnlyCollection<ClipLibraryItem>(items);
        GalleryClips = new ReadOnlyCollection<ClipLibraryItem>(items.Take(4).ToArray());
    }

    /// <summary>
    /// Newest clips first. The default library load returns up to five entries.
    /// </summary>
    public IReadOnlyList<ClipLibraryItem> Clips { get; }

    public ClipLibraryItem? LatestClip => Clips.Count == 0 ? null : Clips[0];

    /// <summary>
    /// The four newest entries suitable for the compact gallery.
    /// </summary>
    public IReadOnlyList<ClipLibraryItem> GalleryClips { get; }
}

