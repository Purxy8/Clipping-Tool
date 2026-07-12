using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using ClipForge.Models;
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
        ((Storyboard)FindResource("OverlayEnterStoryboard")).Begin(this, true);
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

        HwndSource.FromHwnd(handle)?.AddHook(OverlayWindow_WndProc);
        var extendedStyle = GetWindowLongPtr(handle, GwlExStyle);
        var desiredStyle = new IntPtr(
            extendedStyle.ToInt64() | WsExToolWindow);
        _ = SetWindowLongPtr(handle, GwlExStyle, desiredStyle);
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
                // Ensure a failed drag cannot leave WPF owning mouse capture.
                Mouse.Capture(null);
            }
        }
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

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr(IntPtr windowHandle, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr windowHandle, int index, IntPtr newValue);
}
