namespace ClipForge.Models;

public enum ReplayState
{
    Stopped,
    Starting,
    Buffering,
    Ready,
    Saving,
    Faulted,
    Stopping
}

public sealed record ReplayStateSnapshot(
    ReplayState State,
    TimeSpan AvailableDuration,
    TimeSpan Retention,
    long BufferBytes,
    string? Message = null,
    string? LastSavedPath = null);

