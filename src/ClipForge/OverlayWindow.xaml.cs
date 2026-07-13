using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using ClipForge.Models;
using ClipForge.Services;
using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;
using Brush = System.Windows.Media.Brush;
using VisualTreeHelper = System.Windows.Media.VisualTreeHelper;

namespace ClipForge;

public partial class OverlayWindow : Window
{
    private const int GwlExStyle = -20;
    private const int WmMouseActivate = 0x0021;
    private const int MaNoActivate = 3;
    private const long WsExToolWindow = 0x00000080L;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwcpRound = 2;

    private HwndSource? _windowSource;

    public OverlayWindow()
    {
        InitializeComponent();
        SourceInitialized += OverlayWindow_SourceInitialized;
    }

    public event EventHandler? SaveRequested;

    public event EventHandler? ShowAppRequested;

    public void UpdateState(ReplayStateSnapshot snapshot, bool isRunning, string saveHotkeyText)
    {
        var canSave = isRunning &&
                      snapshot.State is not ReplayState.Starting and not ReplayState.Stopping and not ReplayState.Saving &&
                      snapshot.AvailableDuration >= TimeSpan.FromSeconds(1);

        OverlayStatusText.Text = snapshot.State switch
        {
            ReplayState.Starting => "Starting replay…",
            ReplayState.Buffering => "Building buffer",
            ReplayState.Ready => "Replay ready",
            ReplayState.Saving => "Saving clip…",
            ReplayState.Faulted => "Replay error",
            ReplayState.Stopping => "Stopping replay…",
            _ => "Replay off"
        };
        OverlayStatusDot.Fill = (Brush)FindResource(snapshot.State switch
        {
            ReplayState.Ready => "SuccessBrush",
            ReplayState.Buffering => "WarningBrush",
            ReplayState.Faulted => "ErrorBrush",
            ReplayState.Starting or ReplayState.Saving => "AccentBrush",
            _ => "TextMutedBrush"
        });
        OverlayAvailableText.Text = isRunning && snapshot.AvailableDuration > TimeSpan.Zero
            ? $"{FormatDuration(snapshot.AvailableDuration)} ready · {FormatDuration(snapshot.Retention)} selected"
            : "No replay buffered";
        OverlayHotkeyText.Text = $"Save shortcut: {saveHotkeyText}";
        OverlaySaveButton.IsEnabled = canSave;
    }

    private void OverlayWindow_Loaded(object sender, RoutedEventArgs e)
    {
        var workArea = SystemParameters.WorkArea;
        Left = Math.Max(workArea.Left + 16, workArea.Right - ActualWidth - 22);
        Top = workArea.Top + 22;
        UiMotionService.RevealStartup(OverlayRoot);
    }

    private void OverlayWindow_SourceInitialized(object? sender, EventArgs e)
    {
        // Suppress activation only for pointer clicks. A permanent
        // WS_EX_NOACTIVATE style would also prevent accessibility tools from
        // keyboard-navigating the overlay controls.
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        _windowSource = HwndSource.FromHwnd(handle);
        _windowSource?.AddHook(OverlayWindow_WndProc);
        var extendedStyle = GetWindowLongPtr(handle, GwlExStyle);
        var desiredStyle = new IntPtr(
            extendedStyle.ToInt64() | WsExToolWindow);
        _ = SetWindowLongPtr(handle, GwlExStyle, desiredStyle);
        ApplyRoundedCorners(handle);
    }

    private IntPtr OverlayWindow_WndProc(
        IntPtr windowHandle,
        int message,
        IntPtr wordParameter,
        IntPtr longParameter,
        ref bool handled)
    {
        if (message != WmMouseActivate)
        {
            return IntPtr.Zero;
        }

        // MA_NOACTIVATE delivers the click without taking focus or relative-
        // mouse ownership from the foreground game.
        handled = true;
        return new IntPtr(MaNoActivate);
    }

    private void OverlayWindow_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed &&
            e.OriginalSource is DependencyObject source &&
            !HasButtonAncestor(source))
        {
            try
            {
                DragMove();
            }
            catch (InvalidOperationException)
            {
                // The pointer can be released between the routed event and DragMove.
            }
            finally
            {
                // DragMove normally releases capture itself. Explicitly clear any
                // remaining WPF capture so an interrupted drag cannot affect a game.
                if (Mouse.Captured is not null)
                {
                    Mouse.Capture(null);
                }
            }
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_windowSource is not null)
        {
            try
            {
                _windowSource.RemoveHook(OverlayWindow_WndProc);
            }
            catch (ObjectDisposedException)
            {
                // Destroying the HWND also releases its message hooks.
            }

            _windowSource = null;
        }

        SourceInitialized -= OverlayWindow_SourceInitialized;
        base.OnClosed(e);
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e) =>
        SaveRequested?.Invoke(this, EventArgs.Empty);

    private void OpenAppButton_Click(object sender, RoutedEventArgs e) =>
        ShowAppRequested?.Invoke(this, EventArgs.Empty);

    private void HideButton_Click(object sender, RoutedEventArgs e) => Hide();

    private static bool HasButtonAncestor(DependencyObject source)
    {
        for (var current = source; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is ButtonBase)
            {
                return true;
            }
        }

        return false;
    }

    private static string FormatDuration(TimeSpan duration) =>
        duration.TotalHours >= 1
            ? $"{(int)duration.TotalHours}:{duration.Minutes:00}:{duration.Seconds:00}"
            : $"{(int)duration.TotalMinutes}:{duration.Seconds:00}";

    private static void ApplyRoundedCorners(IntPtr windowHandle)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            return;
        }

        try
        {
            var preference = DwmwcpRound;
            _ = DwmSetWindowAttribute(
                windowHandle,
                DwmwaWindowCornerPreference,
                ref preference,
                sizeof(int));
        }
        catch (DllNotFoundException)
        {
            // Retain a normal rectangular window when DWM is unavailable.
        }
        catch (EntryPointNotFoundException)
        {
            // Retain a normal rectangular window on unsupported Windows builds.
        }
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr(IntPtr windowHandle, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr windowHandle, int index, IntPtr newValue);

    [DllImport("dwmapi.dll", ExactSpelling = true, PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(
        IntPtr windowHandle,
        int attribute,
        ref int attributeValue,
        int attributeSize);
}
