using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using UserControl = System.Windows.Controls.UserControl;

namespace ClipForge.Controls;

public partial class TrimRangeSelector : UserControl
{
    private const double ThumbWidth = 18;
    private const double DefaultKeyboardStep = 0.1;

    public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(
        nameof(Minimum),
        typeof(double),
        typeof(TrimRangeSelector),
        new FrameworkPropertyMetadata(0d, OnRangePropertyChanged));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum),
        typeof(double),
        typeof(TrimRangeSelector),
        new FrameworkPropertyMetadata(1d, OnRangePropertyChanged));

    public static readonly DependencyProperty LowerValueProperty = DependencyProperty.Register(
        nameof(LowerValue),
        typeof(double),
        typeof(TrimRangeSelector),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnRangePropertyChanged));

    public static readonly DependencyProperty UpperValueProperty = DependencyProperty.Register(
        nameof(UpperValue),
        typeof(double),
        typeof(TrimRangeSelector),
        new FrameworkPropertyMetadata(1d, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnRangePropertyChanged));

    public static readonly DependencyProperty MinimumSpanProperty = DependencyProperty.Register(
        nameof(MinimumSpan),
        typeof(double),
        typeof(TrimRangeSelector),
        new FrameworkPropertyMetadata(0.25d, OnRangePropertyChanged));

    private bool _isNormalizing;
    private TrimRangeHandle _activeHandle;

    public TrimRangeSelector()
    {
        InitializeComponent();
        IsEnabledChanged += (_, _) => UpdateVisuals();
        Loaded += (_, _) => UpdateVisuals();
    }

    public event EventHandler<TrimRangeChangedEventArgs>? RangeChanged;

    public event EventHandler<TrimRangeChangedEventArgs>? RangeChangeCompleted;

    public double Minimum
    {
        get => (double)GetValue(MinimumProperty);
        set => SetValue(MinimumProperty, value);
    }

    public double Maximum
    {
        get => (double)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public double LowerValue
    {
        get => (double)GetValue(LowerValueProperty);
        set => SetValue(LowerValueProperty, value);
    }

    public double UpperValue
    {
        get => (double)GetValue(UpperValueProperty);
        set => SetValue(UpperValueProperty, value);
    }

    public double MinimumSpan
    {
        get => (double)GetValue(MinimumSpanProperty);
        set => SetValue(MinimumSpanProperty, value);
    }

    public bool FocusStartHandle() => StartThumb.Focus();

    private static void OnRangePropertyChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        var selector = (TrimRangeSelector)sender;
        selector.NormalizeRange();
        selector.UpdateVisuals();
        if (!selector._isNormalizing)
        {
            selector.RaiseRangeChanged(selector._activeHandle, completed: false);
        }
    }

    private void NormalizeRange()
    {
        if (_isNormalizing)
        {
            return;
        }

        _isNormalizing = true;
        try
        {
            var minimum = double.IsFinite(Minimum) ? Minimum : 0;
            var maximum = double.IsFinite(Maximum) && Maximum > minimum
                ? Maximum
                : minimum + 1;
            var span = double.IsFinite(MinimumSpan)
                ? Math.Clamp(MinimumSpan, 0, maximum - minimum)
                : 0;
            var lower = double.IsFinite(LowerValue)
                ? Math.Clamp(LowerValue, minimum, maximum - span)
                : minimum;
            var upper = double.IsFinite(UpperValue)
                ? Math.Clamp(UpperValue, lower + span, maximum)
                : maximum;

            SetCurrentValue(MinimumProperty, minimum);
            SetCurrentValue(MaximumProperty, maximum);
            SetCurrentValue(MinimumSpanProperty, span);
            SetCurrentValue(LowerValueProperty, lower);
            SetCurrentValue(UpperValueProperty, upper);
        }
        finally
        {
            _isNormalizing = false;
        }
    }

    private void TrackArea_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateVisuals();

    private void UpdateVisuals()
    {
        if (HandleCanvas is null || TrackArea is null || TrackArea.ActualWidth <= 0)
        {
            return;
        }

        var range = Maximum - Minimum;
        var usableWidth = Math.Max(0, TrackArea.ActualWidth - ThumbWidth);
        var lowerFraction = range > 0 ? (LowerValue - Minimum) / range : 0;
        var upperFraction = range > 0 ? (UpperValue - Minimum) / range : 1;
        var lowerLeft = Math.Clamp(lowerFraction, 0, 1) * usableWidth;
        var upperLeft = Math.Clamp(upperFraction, 0, 1) * usableWidth;

        Canvas.SetLeft(StartThumb, lowerLeft);
        Canvas.SetTop(StartThumb, Math.Max(0, (TrackArea.ActualHeight - StartThumb.ActualHeight) / 2));
        Canvas.SetLeft(EndThumb, upperLeft);
        Canvas.SetTop(EndThumb, Math.Max(0, (TrackArea.ActualHeight - EndThumb.ActualHeight) / 2));
        SelectedTrack.Margin = new Thickness(lowerLeft + ThumbWidth / 2, 0, 0, 0);
        SelectedTrack.Width = Math.Max(0, upperLeft - lowerLeft);
        StartThumb.ToolTip = $"Trim start: {LowerValue:0.0} seconds";
        EndThumb.ToolTip = $"Trim end: {UpperValue:0.0} seconds";
        System.Windows.Automation.AutomationProperties.SetItemStatus(
            StartThumb,
            $"{LowerValue:0.0} seconds");
        System.Windows.Automation.AutomationProperties.SetItemStatus(
            EndThumb,
            $"{UpperValue:0.0} seconds");
    }

    private void StartThumb_DragDelta(object sender, DragDeltaEventArgs e) =>
        MoveHandle(TrimRangeHandle.Start, PixelsToValue(e.HorizontalChange), completed: false);

    private void EndThumb_DragDelta(object sender, DragDeltaEventArgs e) =>
        MoveHandle(TrimRangeHandle.End, PixelsToValue(e.HorizontalChange), completed: false);

    private void Thumb_DragCompleted(object sender, DragCompletedEventArgs e) =>
        RaiseRangeChanged(_activeHandle, completed: true);

    private double PixelsToValue(double pixels)
    {
        var usableWidth = Math.Max(1, TrackArea.ActualWidth - ThumbWidth);
        return pixels / usableWidth * (Maximum - Minimum);
    }

    private void StartThumb_PreviewKeyDown(object sender, KeyEventArgs e) =>
        HandleKeyboard(TrimRangeHandle.Start, e);

    private void EndThumb_PreviewKeyDown(object sender, KeyEventArgs e) =>
        HandleKeyboard(TrimRangeHandle.End, e);

    private void HandleKeyboard(TrimRangeHandle handle, KeyEventArgs e)
    {
        var step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 1d : DefaultKeyboardStep;
        var delta = e.Key switch
        {
            Key.Left or Key.Down => -step,
            Key.Right or Key.Up => step,
            Key.PageDown => -5d,
            Key.PageUp => 5d,
            _ => (double?)null
        };

        if (delta is not null)
        {
            MoveHandle(handle, delta.Value, completed: true);
            e.Handled = true;
            return;
        }

        var absolute = (handle, e.Key) switch
        {
            (TrimRangeHandle.Start, Key.Home) => Minimum,
            (TrimRangeHandle.Start, Key.End) => UpperValue - MinimumSpan,
            (TrimRangeHandle.End, Key.Home) => LowerValue + MinimumSpan,
            (TrimRangeHandle.End, Key.End) => Maximum,
            _ => (double?)null
        };
        if (absolute is null)
        {
            return;
        }

        SetHandleValue(handle, absolute.Value);
        RaiseRangeChanged(handle, completed: true);
        e.Handled = true;
    }

    private void TrackArea_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!IsEnabled || FindVisualParent<Thumb>(e.OriginalSource as DependencyObject) is not null)
        {
            return;
        }

        var usableWidth = Math.Max(1, TrackArea.ActualWidth - ThumbWidth);
        var x = Math.Clamp(e.GetPosition(TrackArea).X - ThumbWidth / 2, 0, usableWidth);
        var value = Minimum + x / usableWidth * (Maximum - Minimum);
        var handle = Math.Abs(value - LowerValue) <= Math.Abs(value - UpperValue)
            ? TrimRangeHandle.Start
            : TrimRangeHandle.End;
        SetHandleValue(handle, value);
        RaiseRangeChanged(handle, completed: true);
        (handle == TrimRangeHandle.Start ? StartThumb : EndThumb).Focus();
        e.Handled = true;
    }

    private void MoveHandle(TrimRangeHandle handle, double delta, bool completed)
    {
        SetHandleValue(handle, (handle == TrimRangeHandle.Start ? LowerValue : UpperValue) + delta);
        if (completed)
        {
            RaiseRangeChanged(handle, completed: true);
        }
    }

    private void SetHandleValue(TrimRangeHandle handle, double requested)
    {
        _activeHandle = handle;
        if (handle == TrimRangeHandle.Start)
        {
            SetCurrentValue(LowerValueProperty, Math.Clamp(requested, Minimum, UpperValue - MinimumSpan));
        }
        else
        {
            SetCurrentValue(UpperValueProperty, Math.Clamp(requested, LowerValue + MinimumSpan, Maximum));
        }
    }

    private void RaiseRangeChanged(TrimRangeHandle handle, bool completed)
    {
        var args = new TrimRangeChangedEventArgs(handle, LowerValue, UpperValue);
        if (completed)
        {
            RangeChangeCompleted?.Invoke(this, args);
        }
        else
        {
            RangeChanged?.Invoke(this, args);
        }
    }

    private static T? FindVisualParent<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match)
            {
                return match;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }
}

public enum TrimRangeHandle
{
    None,
    Start,
    End
}

public sealed class TrimRangeChangedEventArgs(
    TrimRangeHandle handle,
    double lowerValue,
    double upperValue) : EventArgs
{
    public TrimRangeHandle Handle { get; } = handle;

    public double LowerValue { get; } = lowerValue;

    public double UpperValue { get; } = upperValue;
}
