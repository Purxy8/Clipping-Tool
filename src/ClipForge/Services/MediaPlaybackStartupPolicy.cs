namespace ClipForge.Services;

internal static class MediaPlaybackStartupPolicy
{
    internal static bool ShouldKeepPrimedPlaybackRunning(
        bool autoplay,
        bool hasRestorePosition) =>
        autoplay && !hasRestorePosition;
}
