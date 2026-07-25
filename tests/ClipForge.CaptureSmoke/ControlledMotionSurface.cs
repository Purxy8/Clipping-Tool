using System.Diagnostics;
using System.Drawing.Drawing2D;
using ClipForge.Models;
using ClipForge.Services;
using Forms = System.Windows.Forms;

namespace ClipForge.Testing;

internal sealed class ControlledMotionSurfaceSession : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly TimeSpan _lifetime;
    private readonly TaskCompletionSource _ready = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _completed = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread _thread;
    private ControlledMotionSurfaceForm? _form;
    private DisplayOption? _display;
    private int _disposeRequested;

    private ControlledMotionSurfaceSession(TimeSpan lifetime)
    {
        _lifetime = lifetime;
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "ClipForge controlled motion surface"
        };
        _thread.SetApartmentState(ApartmentState.STA);
    }

    internal DisplayOption Display =>
        _display ?? throw new InvalidOperationException(
            "The controlled motion surface display is unavailable before readiness.");

    internal static async Task<ControlledMotionSurfaceSession> StartAsync(
        TimeSpan lifetime,
        CancellationToken cancellationToken)
    {
        if (lifetime < TimeSpan.FromSeconds(5) ||
            lifetime > TimeSpan.FromMinutes(70))
        {
            throw new ArgumentOutOfRangeException(
                nameof(lifetime),
                "The controlled motion surface lifetime must be between 5 seconds and 70 minutes.");
        }

        var session = new ControlledMotionSurfaceSession(lifetime);
        session._thread.Start();
        try
        {
            await session._ready.Task
                .WaitAsync(TimeSpan.FromSeconds(12), cancellationToken)
                .ConfigureAwait(false);
            return session;
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private void Run()
    {
        try
        {
            Forms.Application.SetHighDpiMode(Forms.HighDpiMode.PerMonitorV2);
            Forms.Application.EnableVisualStyles();
            Forms.Application.SetCompatibleTextRenderingDefault(false);
            var displays = new DeviceDiscoveryService().GetDisplays();
            _display = displays.FirstOrDefault(candidate => candidate.IsPrimary)
                       ?? displays.FirstOrDefault()
                       ?? throw new InvalidOperationException(
                           "No display was available for the controlled motion surface.");
            using var form = new ControlledMotionSurfaceForm(
                _display,
                _lifetime,
                () => Volatile.Read(ref _disposeRequested) != 0,
                () => _ready.TrySetResult());
            lock (_gate)
            {
                _form = form;
            }

            if (Volatile.Read(ref _disposeRequested) == 0)
            {
                form.Show();
                form.Refresh();
                Forms.Application.Run();
            }
        }
        catch (Exception exception)
        {
            _ready.TrySetException(new InvalidOperationException(
                "The controlled motion surface failed.",
                exception));
        }
        finally
        {
            lock (_gate)
            {
                _form = null;
            }

            _completed.TrySetResult();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeRequested, 1) != 0)
        {
            return;
        }

        ControlledMotionSurfaceForm? form;
        lock (_gate)
        {
            form = _form;
        }

        if (form is { IsHandleCreated: true, IsDisposed: false })
        {
            try
            {
                form.BeginInvoke(form.Close);
            }
            catch (InvalidOperationException)
            {
                // The window closed between the state check and dispatch.
            }
        }

        try
        {
            await _completed.Task
                .WaitAsync(TimeSpan.FromSeconds(3))
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            Console.Error.WriteLine(
                "The controlled motion surface did not close within the three-second cleanup budget. " +
                "Its background STA thread remains bounded by the configured surface lifetime.");
        }
    }
}

internal sealed class ControlledMotionSurfaceForm : Forms.Form
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly TimeSpan _lifetime;
    private readonly Func<bool> _stopRequested;
    private readonly Action _reportReady;
    private readonly Forms.Timer _animationTimer;
    private readonly Forms.Timer _lifetimeTimer;
    private bool _readyReported;
    private int _renderCount;

    internal ControlledMotionSurfaceForm(
        DisplayOption display,
        TimeSpan lifetime,
        Func<bool> stopRequested,
        Action reportReady)
    {
        _lifetime = lifetime;
        _stopRequested = stopRequested;
        _reportReady = reportReady;
        AutoScaleMode = Forms.AutoScaleMode.None;
        BackColor = Color.Black;
        Bounds = new Rectangle(
            display.Left,
            display.Top,
            display.Width,
            display.Height);
        DoubleBuffered = true;
        FormBorderStyle = Forms.FormBorderStyle.None;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowIcon = false;
        ShowInTaskbar = false;
        StartPosition = Forms.FormStartPosition.Manual;
        Text = "ClipForge deterministic motion surface";
        TopMost = true;

        SetStyle(
            Forms.ControlStyles.AllPaintingInWmPaint |
            Forms.ControlStyles.OptimizedDoubleBuffer |
            Forms.ControlStyles.UserPaint,
            true);

        _animationTimer = new Forms.Timer
        {
            Interval = 15
        };
        _animationTimer.Tick += (_, _) =>
        {
            // WM_PAINT can be coalesced indefinitely while the test runner
            // considers its topmost window occluded. Draw to the window DC so
            // every controlled tick reaches the compositor surface.
            using var graphics = CreateGraphics();
            RenderMotionFrame(graphics);
        };
        _lifetimeTimer = new Forms.Timer
        {
            Interval = 100
        };
        _lifetimeTimer.Tick += (_, _) =>
        {
            if (_stopRequested() || _clock.Elapsed >= _lifetime)
            {
                Close();
            }
        };
    }

    protected override bool ShowWithoutActivation => false;

    protected override void OnShown(EventArgs eventArgs)
    {
        base.OnShown(eventArgs);
        if (_stopRequested())
        {
            Close();
            return;
        }

        _animationTimer.Start();
        _lifetimeTimer.Start();
        Refresh();
        if (!_readyReported)
        {
            _readyReported = true;
            _reportReady();
        }
    }

    protected override void OnPaint(Forms.PaintEventArgs eventArgs)
    {
        base.OnPaint(eventArgs);
        RenderMotionFrame(eventArgs.Graphics);
    }

    private void RenderMotionFrame(Graphics graphics)
    {
        _renderCount++;
        if (_renderCount == 60)
        {
            Console.WriteLine("Controlled motion surface render cadence is active.");
        }

        graphics.CompositingMode = CompositingMode.SourceCopy;
        graphics.CompositingQuality = CompositingQuality.HighSpeed;
        graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
        graphics.PixelOffsetMode = PixelOffsetMode.HighSpeed;
        graphics.SmoothingMode = SmoothingMode.None;

        var elapsedMilliseconds = _clock.Elapsed.TotalMilliseconds;
        var frame = (long)Math.Floor(elapsedMilliseconds / (1000d / 120d));
        var red = 24 + (int)(frame * 5 % 160);
        var green = 24 + (int)(frame * 3 % 160);
        var blue = 24 + (int)(frame * 7 % 160);
        graphics.Clear(Color.FromArgb(red, green, blue));

        var stripeWidth = Math.Max(48, ClientSize.Width / 12);
        var stripeOffset = (int)(frame * 19 % (stripeWidth * 2)) - stripeWidth;
        using var lightBrush = new SolidBrush(Color.FromArgb(230, 245, 245, 245));
        using var darkBrush = new SolidBrush(Color.FromArgb(230, 12, 12, 12));
        for (var x = stripeOffset; x < ClientSize.Width; x += stripeWidth * 2)
        {
            graphics.FillRectangle(
                lightBrush,
                x,
                0,
                stripeWidth,
                ClientSize.Height);
            graphics.FillRectangle(
                darkBrush,
                x + stripeWidth,
                0,
                stripeWidth,
                ClientSize.Height);
        }

        var markerSize = Math.Max(80, Math.Min(ClientSize.Width, ClientSize.Height) / 5);
        var travelWidth = Math.Max(1, ClientSize.Width + markerSize);
        var travelHeight = Math.Max(1, ClientSize.Height - markerSize);
        var markerX = (int)(frame * 31 % travelWidth) - markerSize;
        var markerY = travelHeight <= 1
            ? 0
            : (int)((Math.Sin(frame / 13d) + 1d) * travelHeight / 2d);
        using var markerBrush = new SolidBrush(Color.Lime);
        graphics.FillEllipse(
            markerBrush,
            markerX,
            markerY,
            markerSize,
            markerSize);

        var fontSize = Math.Clamp(ClientSize.Height / 20f, 24f, 72f);
        using var font = new Font(
            FontFamily.GenericMonospace,
            fontSize,
            FontStyle.Bold,
            GraphicsUnit.Pixel);
        using var textBrush = new SolidBrush(Color.Yellow);
        graphics.DrawString(
            $"CLIPFORGE CONTROLLED MOTION  {frame:D10}",
            font,
            textBrush,
            24,
            24);
        graphics.Flush(FlushIntention.Sync);
    }

    protected override void OnFormClosed(Forms.FormClosedEventArgs eventArgs)
    {
        Console.WriteLine("Controlled motion surface closed.");
        _animationTimer.Stop();
        _lifetimeTimer.Stop();
        _animationTimer.Dispose();
        _lifetimeTimer.Dispose();
        base.OnFormClosed(eventArgs);
        Forms.Application.ExitThread();
    }
}
