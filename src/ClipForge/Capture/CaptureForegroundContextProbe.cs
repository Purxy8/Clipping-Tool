using System.Runtime.InteropServices;
using ClipForge.Models;

namespace ClipForge.Capture;

internal static class CaptureForegroundContextProbe
{
    private const uint DwmExtendedFrameBounds = 9;
    private const uint DwmCloaked = 14;
    private static readonly TimeSpan RecentInputThreshold = TimeSpan.FromSeconds(5);

    public static CaptureForegroundContext Read(DisplayOption display)
    {
        ArgumentNullException.ThrowIfNull(display);
        try
        {
            var window = GetForegroundWindow();
            if (window == IntPtr.Zero ||
                window == GetShellWindow() ||
                !IsWindowVisible(window) ||
                IsIconic(window))
            {
                return new CaptureForegroundContext(false, HasRecentInput());
            }

            _ = GetWindowThreadProcessId(window, out var processId);
            if (processId == 0 || processId == Environment.ProcessId || IsCloaked(window))
            {
                return new CaptureForegroundContext(false, HasRecentInput());
            }

            if (!TryGetWindowBounds(window, out var bounds))
            {
                return new CaptureForegroundContext(false, HasRecentInput());
            }

            var monitor = new NativeRect(
                display.Left,
                display.Top,
                checked(display.Left + display.Width),
                checked(display.Top + display.Height));
            var intersectionWidth = Math.Max(
                0,
                Math.Min(bounds.Right, monitor.Right) - Math.Max(bounds.Left, monitor.Left));
            var intersectionHeight = Math.Max(
                0,
                Math.Min(bounds.Bottom, monitor.Bottom) - Math.Max(bounds.Top, monitor.Top));
            var monitorArea = (long)display.Width * display.Height;
            var intersectionArea = (long)intersectionWidth * intersectionHeight;
            var isFullscreen = monitorArea > 0 && intersectionArea / (double)monitorArea >= 0.98;
            return new CaptureForegroundContext(isFullscreen, HasRecentInput());
        }
        catch (Exception exception) when (
            exception is OverflowException or ArgumentException or ExternalException)
        {
            return new CaptureForegroundContext(false, HasRecentInput());
        }
    }

    private static bool TryGetWindowBounds(IntPtr window, out NativeRect bounds)
    {
        if (DwmGetWindowAttribute(
                window,
                DwmExtendedFrameBounds,
                out bounds,
                Marshal.SizeOf<NativeRect>()) == 0)
        {
            return bounds.Right > bounds.Left && bounds.Bottom > bounds.Top;
        }

        return GetWindowRect(window, out bounds) &&
               bounds.Right > bounds.Left &&
               bounds.Bottom > bounds.Top;
    }

    private static bool IsCloaked(IntPtr window) =>
        DwmGetWindowAttributeInt32(
            window,
            DwmCloaked,
            out int cloaked,
            sizeof(int)) == 0 && cloaked != 0;

    private static bool HasRecentInput()
    {
        var input = new LastInputInfo
        {
            Size = (uint)Marshal.SizeOf<LastInputInfo>()
        };
        if (!GetLastInputInfo(ref input))
        {
            return false;
        }

        var elapsedMilliseconds = unchecked((uint)Environment.TickCount - input.TickCount);
        return elapsedMilliseconds <= RecentInputThreshold.TotalMilliseconds;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public NativeRect(int left, int top, int right, int bottom)
        {
            Left = left;
            Top = top;
            Right = right;
            Bottom = bottom;
        }

        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint Size;
        public uint TickCount;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr GetShellWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out NativeRect bounds);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LastInputInfo input);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(
        IntPtr window,
        uint attribute,
        out NativeRect value,
        int size);

    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    private static extern int DwmGetWindowAttributeInt32(
        IntPtr window,
        uint attribute,
        out int value,
        int size);
}
