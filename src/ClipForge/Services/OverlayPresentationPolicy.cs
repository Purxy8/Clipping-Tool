namespace ClipForge.Services;

/// <summary>
/// Keeps the optional topmost overlay transient. A permanently visible topmost
/// HWND can keep some fullscreen presentation paths in desktop composition even
/// when the overlay itself is static, so ClipForge dismisses it after a short
/// interaction window and leaves the global hotkey available to reveal the same
/// instance.
/// </summary>
internal static class OverlayPresentationPolicy
{
    internal static readonly TimeSpan AutoHideDelay = TimeSpan.FromSeconds(10);
    internal static readonly TimeSpan HardMaximumVisibleDuration = TimeSpan.FromSeconds(15);

    internal static bool ShouldAutoHide(
        bool isVisible,
        bool hasMouseCapture,
        TimeSpan visibleDuration) =>
        isVisible &&
        (!hasMouseCapture || visibleDuration >= HardMaximumVisibleDuration);

    internal static TimeSpan GetNextAutoHideDelay(TimeSpan visibleDuration)
    {
        var untilHardLimit = HardMaximumVisibleDuration - visibleDuration;
        if (untilHardLimit <= TimeSpan.Zero)
        {
            return TimeSpan.FromMilliseconds(1);
        }

        return untilHardLimit < AutoHideDelay ? untilHardLimit : AutoHideDelay;
    }
}
