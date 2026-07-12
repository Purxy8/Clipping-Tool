using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using ClipForge.Models;
using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;
using Brush = System.Windows.Media.Brush;
using VisualTreeHelper = System.Windows.Media.VisualTreeHelper;

namespace ClipForge;

public partial class OverlayWindow : Window
{
    public OverlayWindow()
    {
        InitializeComponent();
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
}
