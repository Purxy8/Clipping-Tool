using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
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

    private readonly DispatcherTimer _autoHideTimer;
    private HwndSource? _windowSource;
    private OverlayViewState? _lastRenderedState;
    private long _visibleSinceTimestamp;

    public OverlayWindow()
    {
        InitializeComponent();
        _autoHideTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = OverlayPresentationPolicy.AutoHideDelay
        };
        _autoHideTimer.Tick += AutoHideTimer_Tick;
        SourceInitialized += OverlayWindow_SourceInitialized;
        IsVisibleChanged += OverlayWindow_IsVisibleChanged;
        PreviewMouseDown += OverlayWindow_PreviewMouseDown;
    }

    public event EventHandler? SaveRequested;

    public event EventHandler? ShowAppRequested;

    /// <summary>
    /// Shows the existing overlay instance for one bounded interaction window.
    /// It deliberately does not activate, close, or recreate the HWND.
    /// </summary>
    public void ShowTransient()
    {
        if (!IsVisible)
        {
            Topmost = true;
            Show();
        }

        RestartAutoHideCountdown();
    }

    public void Dismiss()
    {
        _autoHideTimer.Stop();
        if (IsVisible)
        {
            Hide();
        }

        Topmost = false;
    }

    public void UpdateState(ReplayStateSnapshot snapshot, bool isRunning, string saveHotkeyText)
    {
        var canSave = isRunning &&
                      snapshot.State is not ReplayState.Starting and not ReplayState.Stopping and not ReplayState.Saving &&
                      snapshot.AvailableDuration >= TimeSpan.FromSeconds(1);

        var viewState = new OverlayViewState(
            snapshot.State,
            isRunning,
            canSave,
            snapshot.AvailableDuration,
            snapshot.Retention,
            saveHotkeyText);
        if (_lastRenderedState == viewState)
        {
            return;
        }

        _lastRenderedState = viewState;

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

    private void OverlayWindow_IsVisibleChanged(
        object sender,
        DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true)
        {
            _visibleSinceTimestamp = Stopwatch.GetTimestamp();
            Topmost = true;
            RestartAutoHideCountdown();
            return;
        }

        _autoHideTimer.Stop();
        Topmost = false;
        _visibleSinceTimestamp = 0;
        _lastRenderedState = null;
        if (IsMouseCaptureWithin)
        {
            Mouse.Capture(null);
        }
    }

    private void OverlayWindow_PreviewMouseDown(object sender, MouseButtonEventArgs e) =>
        RestartAutoHideCountdown();

    private void AutoHideTimer_Tick(object? sender, EventArgs e)
    {
        _autoHideTimer.Stop();
        var visibleDuration = _visibleSinceTimestamp == 0
            ? TimeSpan.Zero
            : Stopwatch.GetElapsedTime(_visibleSinceTimestamp);
        if (OverlayPresentationPolicy.ShouldAutoHide(
                IsVisible,
                IsMouseCaptureWithin,
                visibleDuration))
        {
            if (IsMouseCaptureWithin)
            {
                Mouse.Capture(null);
            }

            Dismiss();
            return;
        }

        if (IsVisible)
        {
            RestartAutoHideCountdown();
        }
    }

    private void RestartAutoHideCountdown()
    {
        _autoHideTimer.Stop();
        if (IsVisible)
        {
            var visibleDuration = _visibleSinceTimestamp == 0
                ? TimeSpan.Zero
                : Stopwatch.GetElapsedTime(_visibleSinceTimestamp);
            _autoHideTimer.Interval = OverlayPresentationPolicy.GetNextAutoHideDelay(
                visibleDuration);
            _autoHideTimer.Start();
        }
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
        _autoHideTimer.Stop();
        _autoHideTimer.Tick -= AutoHideTimer_Tick;
        IsVisibleChanged -= OverlayWindow_IsVisibleChanged;
        PreviewMouseDown -= OverlayWindow_PreviewMouseDown;
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

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        Dismiss();
        SaveRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OpenAppButton_Click(object sender, RoutedEventArgs e)
    {
        Dismiss();
        ShowAppRequested?.Invoke(this, EventArgs.Empty);
    }

    private void HideButton_Click(object sender, RoutedEventArgs e) => Dismiss();

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

    private sealed record OverlayViewState(
        ReplayState State,
        bool IsRunning,
        bool CanSave,
        TimeSpan AvailableDuration,
        TimeSpan Retention,
        string SaveHotkeyText);
}
