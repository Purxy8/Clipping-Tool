using System.Runtime.InteropServices;
using ClipForge.Models;

namespace ClipForge.Capture;

internal static class CaptureForegroundContextProbe
{
    private const uint DwmExtendedFrameBounds = 9;
    private const uint DwmCloaked = 14;
    private const uint MonitorDefaultToNearest = 2;
    private const int GwlStyle = -16;
    private const long WsCaption = 0x00C00000L;
    private const long WsThickFrame = 0x00040000L;
    private const long WsChild = 0x40000000L;
    private const ushort XInputGamepadLeftThumbDeadZone = 7849;
    private const ushort XInputGamepadRightThumbDeadZone = 8689;
    private const byte XInputGamepadTriggerThreshold = 30;
    private static readonly TimeSpan RecentInputThreshold = TimeSpan.FromSeconds(5);
    private static readonly object ControllerInputGate = new();
    private static readonly uint?[] LastControllerPacketNumbers = new uint?[4];
    private static long _lastControllerInputTimestamp = -1;

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

            var configuredMonitor = new NativeRect(
                display.Left,
                display.Top,
                checked(display.Left + display.Width),
                checked(display.Top + display.Height));
            var monitorMatches =
                TryGetWindowMonitor(
                    window,
                    out var liveMonitor,
                    out var monitorDeviceName) &&
                string.Equals(
                    monitorDeviceName,
                    display.DeviceName,
                    StringComparison.OrdinalIgnoreCase);
            // Exclusive/custom display modes can temporarily change the
            // HMONITOR rectangle while DeviceName remains stable. Measure
            // coverage against that live rectangle so a stretched game is not
            // rejected because CaptureConfiguration still has the old bounds.
            var monitor = monitorMatches ? liveMonitor : configuredMonitor;
            var intersectionWidth = Math.Max(
                0,
                Math.Min(bounds.Right, monitor.Right) - Math.Max(bounds.Left, monitor.Left));
            var intersectionHeight = Math.Max(
                0,
                Math.Min(bounds.Bottom, monitor.Bottom) - Math.Max(bounds.Top, monitor.Top));
            var monitorArea =
                (long)(monitor.Right - monitor.Left) *
                (monitor.Bottom - monitor.Top);
            var intersectionArea = (long)intersectionWidth * intersectionHeight;
            var coverage = monitorArea > 0
                ? intersectionArea / (double)monitorArea
                : 0;
            var borderless = IsBorderlessTopLevelWindow(window);
            var anchoredToMonitor =
                Math.Abs(bounds.Left - monitor.Left) <= 8 &&
                Math.Abs(bounds.Top - monitor.Top) <= 8;
            var usedCustomFullscreenFallback =
                coverage < 0.98 &&
                IsFullscreenCandidate(
                    coverage,
                    monitorMatches,
                    borderless,
                    anchoredToMonitor,
                    (bounds.Right - bounds.Left) /
                        (double)(bounds.Bottom - bounds.Top),
                    (monitor.Right - monitor.Left) /
                        (double)(monitor.Bottom - monitor.Top));
            var isFullscreen =
                monitorMatches &&
                coverage >= 0.98 ||
                usedCustomFullscreenFallback;
            return new CaptureForegroundContext(
                isFullscreen,
                HasRecentInput(),
                Math.Clamp(coverage, 0, 1),
                usedCustomFullscreenFallback);
        }
        catch (Exception exception) when (
            exception is OverflowException or ArgumentException or ExternalException)
        {
            return new CaptureForegroundContext(false, HasRecentInput());
        }
    }

    internal static bool IsFullscreenCandidate(
        double capturedDisplayCoverage,
        bool monitorMatches,
        bool isBorderless,
        bool isAnchoredToMonitor,
        double foregroundAspectRatio = double.NaN,
        double monitorAspectRatio = double.NaN)
    {
        if (capturedDisplayCoverage >= 0.98)
        {
            return true;
        }

        var hasCustomAspect =
            double.IsFinite(foregroundAspectRatio) &&
            double.IsFinite(monitorAspectRatio) &&
            foregroundAspectRatio > 0 &&
            monitorAspectRatio > 0 &&
            Math.Abs(foregroundAspectRatio - monitorAspectRatio) /
                monitorAspectRatio >= 0.04;
        return monitorMatches &&
               isBorderless &&
               isAnchoredToMonitor &&
               // A stretched game can report its pre-stretch 4:3/16:10 bounds
               // even though it physically occupies the monitor. Requiring a
               // meaningful aspect mismatch distinguishes that case from an
               // ordinary smaller 16:9 borderless window parked at (0, 0).
               hasCustomAspect &&
               capturedDisplayCoverage >= 0.50;
    }

    private static bool TryGetWindowBounds(IntPtr window, out NativeRect bounds)
    {
        // GetWindowRect and GetMonitorInfo follow the calling thread's DPI
        // coordinate space. DWMWA_EXTENDED_FRAME_BOUNDS is always physical
        // pixels, so preferring it would mix spaces on a scaled secondary
        // monitor and corrupt coverage/anchoring calculations.
        if (GetWindowRect(window, out bounds) &&
            bounds.Right > bounds.Left &&
            bounds.Bottom > bounds.Top)
        {
            return true;
        }

        if (DwmGetWindowAttribute(
                window,
                DwmExtendedFrameBounds,
                out bounds,
                Marshal.SizeOf<NativeRect>()) == 0)
        {
            return bounds.Right > bounds.Left && bounds.Bottom > bounds.Top;
        }

        return false;
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
            return HasRecentControllerInput();
        }

        var elapsedMilliseconds = unchecked((uint)Environment.TickCount - input.TickCount);
        return elapsedMilliseconds <= RecentInputThreshold.TotalMilliseconds ||
               HasRecentControllerInput();
    }

    private static bool HasRecentControllerInput()
    {
        lock (ControllerInputGate)
        {
            var now = Stopwatch.GetTimestamp();
            try
            {
                for (uint userIndex = 0; userIndex < LastControllerPacketNumbers.Length; userIndex++)
                {
                    if (XInputGetState(userIndex, out var state) != 0)
                    {
                        LastControllerPacketNumbers[userIndex] = null;
                        continue;
                    }

                    var packetChanged =
                        LastControllerPacketNumbers[userIndex] is { } previousPacket &&
                        previousPacket != state.PacketNumber;
                    LastControllerPacketNumbers[userIndex] = state.PacketNumber;
                    if (packetChanged || HasMeaningfulControllerState(state.Gamepad))
                    {
                        _lastControllerInputTimestamp = now;
                    }
                }
            }
            catch (Exception exception) when (
                exception is DllNotFoundException or EntryPointNotFoundException or
                    BadImageFormatException)
            {
                return false;
            }

            return _lastControllerInputTimestamp >= 0 &&
                   Stopwatch.GetElapsedTime(_lastControllerInputTimestamp, now) <=
                   RecentInputThreshold;
        }
    }

    private static bool HasMeaningfulControllerState(XInputGamepad gamepad) =>
        gamepad.Buttons != 0 ||
        gamepad.LeftTrigger > XInputGamepadTriggerThreshold ||
        gamepad.RightTrigger > XInputGamepadTriggerThreshold ||
        Math.Abs((int)gamepad.ThumbLX) > XInputGamepadLeftThumbDeadZone ||
        Math.Abs((int)gamepad.ThumbLY) > XInputGamepadLeftThumbDeadZone ||
        Math.Abs((int)gamepad.ThumbRX) > XInputGamepadRightThumbDeadZone ||
        Math.Abs((int)gamepad.ThumbRY) > XInputGamepadRightThumbDeadZone;

    private static bool TryGetWindowMonitor(
        IntPtr window,
        out NativeRect bounds,
        out string? deviceName)
    {
        bounds = default;
        deviceName = null;
        var monitor = MonitorFromWindow(window, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero)
        {
            return false;
        }

        var info = new MonitorInfoEx
        {
            Size = (uint)Marshal.SizeOf<MonitorInfoEx>()
        };
        if (!GetMonitorInfo(monitor, ref info))
        {
            return false;
        }

        bounds = info.Monitor;
        deviceName = info.DeviceName;
        return bounds.Right > bounds.Left && bounds.Bottom > bounds.Top;
    }

    private static bool IsBorderlessTopLevelWindow(IntPtr window)
    {
        var style = GetWindowLongPtr(window, GwlStyle).ToInt64();
        return (style & WsChild) == 0 &&
               (style & (WsCaption | WsThickFrame)) == 0;
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

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public uint Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string? DeviceName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputState
    {
        public uint PacketNumber;
        public XInputGamepad Gamepad;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputGamepad
    {
        public ushort Buttons;
        public byte LeftTrigger;
        public byte RightTrigger;
        public short ThumbLX;
        public short ThumbLY;
        public short ThumbRX;
        public short ThumbRY;
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
    private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfoEx monitorInfo);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);

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

    [DllImport("xinput1_4.dll")]
    private static extern uint XInputGetState(uint userIndex, out XInputState state);
}
