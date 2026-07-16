using System.Windows;
using System.Windows.Input;

namespace ClipForge.Services;

/// <summary>
/// Releases only mouse capture owned by a specific ClipForge window. Clearing
/// another HWND's capture globally while a game is taking focus would be just
/// as disruptive as leaving ClipForge's slider or thumb captured.
/// </summary>
internal static class WindowInputReleaseService
{
    internal static bool ReleaseMouseCaptureWithin(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        if (Mouse.Captured is not DependencyObject captured ||
            !ReferenceEquals(Window.GetWindow(captured), window))
        {
            return false;
        }

        Mouse.Capture(null);
        return true;
    }
}
