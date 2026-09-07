using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Text.Json;
using ClipForge.Capture;
using ClipForge.Models;
using ClipForge.Services;
using Forms = System.Windows.Forms;

internal static class DesktopDuplicationSmoke
{
    internal static async Task RunAsync(
        FfmpegSetupService setup,
        string artifactRoot,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var resolutionId = Option(arguments, "--resolution") ?? "1080p";
        var resolution = ResolutionOption.All.SingleOrDefault(option =>
            option.Id.Equals(resolutionId, StringComparison.OrdinalIgnoreCase));
        if (resolution is null || resolution.Id is not ("source" or "1080p"))
        {
            throw new ArgumentException("--dda-idle-smoke supports --resolution source or 1080p.");
        }

        var includeAudio = arguments.Contains("--audio", StringComparer.OrdinalIgnoreCase);
        var includeMicrophone = arguments.Contains("--microphone", StringComparer.OrdinalIgnoreCase);
        var recorder = arguments.Contains("--recorder", StringComparer.OrdinalIgnoreCase);
        var motionFirstSeconds = int.Parse(Option(arguments, "--motion-first-seconds") ?? "0", CultureInfo.InvariantCulture);
        if (motionFirstSeconds is < 0 or > 6)
        {
            throw new ArgumentException("--motion-first-seconds must be between 0 and 6.");
        }
        var discovery = new DeviceDiscoveryService();
        var display = discovery.GetDisplays().FirstOrDefault(candidate => candidate.IsPrimary)
            ?? throw new InvalidOperationException("No primary display is available for the DDA idle smoke.");
        if (display.DesktopDuplicationTarget is null)
        {
            throw new InvalidOperationException("The primary display has no verified DXGI duplication target.");
        }

        var output = includeAudio
            ? SelectAudioDevice(discovery.GetOutputDevices(), "desktop output")
            : null;
        var microphone = includeMicrophone
            ? SelectAudioDevice(discovery.GetMicrophones(), "microphone")
            : null;
        var runDirectory = Path.Combine(Path.GetFullPath(artifactRoot),
            $"dda-idle-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}");
        var clipsDirectory = Path.Combine(runDirectory, "clips");
        var bufferRoot = Path.Combine(runDirectory, "service-buffer");
        Directory.CreateDirectory(clipsDirectory);
        Directory.CreateDirectory(bufferRoot);
        var reportPath = Path.Combine(runDirectory, "dda-idle-report.json");
        var configuration = new CaptureConfiguration(
            display, resolution, 60,
            recorder ? RecordingStoragePolicy.NoReplayRetention : TimeSpan.FromHours(1),
            CaptureCursor: true,
            CaptureSystemAudio: includeAudio,
            OutputAudioDevice: output,
            CaptureMicrophone: includeMicrophone,
            MicrophoneDevice: microphone,
            SaveDirectory: clipsDirectory)
        {
            SessionMode = recorder ? CaptureSessionMode.Recording : CaptureSessionMode.InstantReplay
        };
        if (recorder)
        {
            var size = CaptureGeometry.ResolveOutputSize(configuration);
            configuration = configuration with
            {
                LockOutputGeometry = true,
                LockedOutputWidth = size.Width,
                LockedOutputHeight = size.Height
            };
        }
        var states = new ConcurrentQueue<ReplayStateSnapshot>();
        var recoveryRequests = new ConcurrentQueue<object>();
        var failure = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var startedUtc = DateTimeOffset.UtcNow;
        var runClock = Stopwatch.StartNew();
        var captureMilliseconds = 0d;
        var saveMilliseconds = 0d;
        var expectedClipSeconds = 10d;
        double? recorderMinimumVideoSeconds = null;
        double? recorderMaximumVideoSeconds = null;
        double? startupSecondsBeforeVideo = null;
        DateTime? processStartedUtc = null;
        DateTime? stopRequestedUtc = null;
        string? savedPath = null;
        string? mediaJson = null;
        string? error = null;
        CaptureSessionPlan? capturePlan = null;
        int? processId = null;
        await using var replay = new ReplayBufferService(setup, bufferRoot);
        replay.StateChanged += (_, state) =>
        {
            states.Enqueue(state);
            Console.WriteLine($"DDA idle: {state.State}, {state.AvailableDuration.TotalSeconds:0.0}s available.");
            if (state.State == ReplayState.Faulted)
            {
                failure.TrySetResult(state.Message ?? "The DDA session faulted.");
            }
        };
        replay.CaptureRecoveryRequested += (_, request) =>
        {
            recoveryRequests.Enqueue(new { Reason = request.Reason.ToString(), request.Diagnostic, request.ProcessId });
            failure.TrySetResult($"Unexpected recovery on a static desktop: {request.Reason}. {request.Diagnostic}");
            replay.CompleteCaptureRecoveryRequest(request);
        };

        Console.WriteLine($"DDA idle smoke: {display.Width}x{display.Height}, {resolution.Id}, 60 FPS, " +
                          $"{(recorder ? "Recorder" : "one-hour replay retention")}, cursor on, " +
                          $"audio={includeAudio}, microphone={includeMicrophone}, motionFirstSeconds={motionFirstSeconds}.");
        Console.WriteLine($"Artifacts: {runDirectory}");
        try
        {
            await using (var quiet = await QuietSession.StartAsync(display, motionFirstSeconds, cancellationToken))
            {
                using var quietCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken, quiet.Closed);
                await replay.StartAsync(configuration, quietCancellation.Token);
                capturePlan = replay.LastCapturePlan;
                processId = replay.CaptureProcessId;
                if (!replay.IsRunning ||
                    capturePlan?.Strategy.CaptureBackend != DesktopCaptureBackend.DesktopDuplication)
                {
                    throw new InvalidDataException("The production capability selection did not start Desktop Duplication.");
                }

                using (var captureProcess = Process.GetProcessById(processId!.Value))
                {
                    processStartedUtc = captureProcess.StartTime.ToUniversalTime();
                }
                await quiet.MarkCaptureStartedAsync();

                var captureClock = Stopwatch.StartNew();
                var hold = Task.Delay(TimeSpan.FromSeconds(12), quietCancellation.Token);
                if (await Task.WhenAny(hold, failure.Task) == failure.Task)
                {
                    throw new InvalidDataException(await failure.Task);
                }

                await hold;
                captureMilliseconds = captureClock.Elapsed.TotalMilliseconds;
                var saveClock = Stopwatch.StartNew();
                if (recorder)
                {
                    stopRequestedUtc = DateTime.UtcNow;
                    expectedClipSeconds = (stopRequestedUtc.Value - processStartedUtc!.Value).TotalSeconds;
                    savedPath = await replay.StopAndSaveRecordingAsync(clipsDirectory, quietCancellation.Token);
                    if (replay.IsRunning || replay.HasPendingRecording)
                    {
                        throw new InvalidDataException("Recorder did not finish saving and release its pending session.");
                    }
                }
                else
                {
                    savedPath = await replay.SaveClipAsync(
                        TimeSpan.FromSeconds(10), clipsDirectory, quietCancellation.Token);
                }
                saveMilliseconds = saveClock.Elapsed.TotalMilliseconds;
                quietCancellation.Token.ThrowIfCancellationRequested();
                await replay.StopAsync();
                if (failure.Task.IsCompletedSuccessfully)
                {
                    throw new InvalidDataException(await failure.Task);
                }
            }

            var ffprobe = setup.FindProbeExecutable()
                ?? throw new FileNotFoundException("The verified FFprobe executable is unavailable.");
            mediaJson = await ReadMediaAsync(ffprobe, savedPath!, cancellationToken);
            using var media = JsonDocument.Parse(mediaJson);
            var video = media.RootElement.GetProperty("streams").EnumerateArray()
                .First(stream => stream.GetProperty("codec_type").GetString() == "video");
            var videoDuration = double.Parse(video.GetProperty("duration").GetString()!, CultureInfo.InvariantCulture);
            if (recorder)
            {
                // Process.Start predates DXGI/audio initialization and the first
                // encoded frame; it is an upper bound, not the recording clock.
                // The entire post-StartAsync hold must still be present. Keep
                // that independent lower bound so startup cannot hide lost tail.
                recorderMinimumVideoSeconds = captureMilliseconds / 1000d - 0.2;
                recorderMaximumVideoSeconds = expectedClipSeconds + 0.5;
                startupSecondsBeforeVideo = expectedClipSeconds - videoDuration;
                if (videoDuration < recorderMinimumVideoSeconds ||
                    videoDuration > recorderMaximumVideoSeconds)
                {
                    throw new InvalidDataException(
                        $"Recorder produced {videoDuration:0.###}s; expected at least " +
                        $"{recorderMinimumVideoSeconds:0.###}s from the observed capture hold and at most " +
                        $"{recorderMaximumVideoSeconds:0.###}s from the process lifetime.");
                }
            }

            if (!ReplayBufferService.TryValidateExportProbe(
                    mediaJson, TimeSpan.FromSeconds(recorder ? videoDuration : 10), 60,
                    includeAudio || includeMicrophone, out var validationFailure))
            {
                throw new InvalidDataException(validationFailure);
            }

            var frameCount = long.Parse(video.GetProperty("nb_read_frames").GetString()!, CultureInfo.InvariantCulture);
            var expectedFrameCount = (recorder ? videoDuration : 10) * 60;
            if (Math.Abs(frameCount - expectedFrameCount) > 3)
            {
                throw new InvalidDataException($"Saved video contains {frameCount} decoded frames; expected about {expectedFrameCount:0}.");
            }

            Console.WriteLine($"PASS DDA {(recorder ? "Recorder" : "replay")} idle smoke: " +
                              $"{videoDuration:0.###}s / {frameCount} frames, save {saveMilliseconds:0.0}ms, no recovery requests.");
        }
        catch (Exception exception)
        {
            error = exception.ToString();
            throw;
        }
        finally
        {
            try
            {
                if (replay.IsRunning)
                {
                    await replay.StopAsync();
                }
            }
            finally
            {
                await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(new
                {
                    SchemaVersion = 1,
                    StartedUtc = startedUtc,
                    Configuration = configuration,
                    CapturePlan = capturePlan,
                    ProcessId = processId,
                    CaptureMilliseconds = captureMilliseconds,
                    SaveMilliseconds = saveMilliseconds,
                    Recorder = recorder,
                    MotionFirstSeconds = motionFirstSeconds,
                    ProcessStartedUtc = processStartedUtc,
                    StopRequestedUtc = stopRequestedUtc,
                    ElapsedMilliseconds = runClock.Elapsed.TotalMilliseconds,
                    SavedPath = savedPath,
                    ExpectedClipSeconds = recorder ? (double?)null : expectedClipSeconds,
                    RecorderProcessWallSeconds = recorder ? expectedClipSeconds : (double?)null,
                    RecorderMinimumVideoSeconds = recorderMinimumVideoSeconds,
                    RecorderMaximumVideoSeconds = recorderMaximumVideoSeconds,
                    StartupSecondsBeforeVideo = startupSecondsBeforeVideo,
                    ExpectedFramesPerSecond = 60,
                    QuietSurfaceLifetimeSeconds = 20,
                    States = states.ToArray(),
                    RecoveryRequests = recoveryRequests.ToArray(),
                    MediaProbeJson = mediaJson,
                    Error = error
                }, new JsonSerializerOptions { WriteIndented = true }), CancellationToken.None);
                Console.WriteLine($"Report: {reportPath}");
            }
        }
    }

    private static AudioDeviceOption SelectAudioDevice(IReadOnlyList<AudioDeviceOption> devices, string kind) =>
        devices.FirstOrDefault(device => device.IsDefault) ?? devices.FirstOrDefault()
        ?? throw new InvalidOperationException($"No active {kind} device was available.");

    private static string? Option(IReadOnlyList<string> arguments, string name)
    {
        for (var index = 0; index < arguments.Count; index++)
        {
            if (arguments[index].Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return index + 1 < arguments.Count && !arguments[index + 1].StartsWith("--", StringComparison.Ordinal)
                    ? arguments[index + 1]
                    : throw new ArgumentException($"{name} requires a value.");
            }
        }

        return null;
    }

    private static async Task<string> ReadMediaAsync(string executable, string mediaPath, CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in new[]
        {
            "-v", "error", "-protocol_whitelist", "file", "-count_frames", "-show_entries",
            "stream=codec_type,start_time,duration,avg_frame_rate,r_frame_rate,nb_frames,nb_read_frames:format=duration",
            "-of", "json", mediaPath
        })
        {
            info.ArgumentList.Add(argument);
        }

        using var process = Process.Start(info) ?? throw new InvalidOperationException("FFprobe did not start.");
        using var job = CaptureProcessJob.Attach(process);
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            var text = await output;
            var error = await errors;
            if (process.ExitCode != 0)
            {
                throw new InvalidDataException($"FFprobe failed: {error}");
            }

            return text;
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3));
            }
        }
    }

    private sealed class QuietSession : IAsyncDisposable
    {
        private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenSource _closed = new();
        private readonly object _gate = new();
        private QuietForm? _form;
        private int _disposeRequested;
        internal CancellationToken Closed => _closed.Token;

        internal static async Task<QuietSession> StartAsync(
            DisplayOption display, int motionFirstSeconds, CancellationToken cancellationToken)
        {
            var session = new QuietSession();
            var thread = new Thread(() => session.Run(display, motionFirstSeconds))
            {
                IsBackground = true, Name = "ClipForge DDA static smoke surface"
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            try
            {
                await session._ready.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
                return session;
            }
            catch
            {
                await session.DisposeAsync();
                throw;
            }
        }

        internal async Task MarkCaptureStartedAsync()
        {
            var acknowledged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            QuietForm form;
            lock (_gate)
            {
                form = _form ?? throw new InvalidOperationException("The diagnostic surface closed before capture started.");
            }

            form.BeginInvoke(() =>
            {
                form.MarkCaptureStarted();
                acknowledged.TrySetResult();
            });
            await acknowledged.Task.WaitAsync(TimeSpan.FromSeconds(2), Closed);
        }

        private void Run(DisplayOption display, int motionFirstSeconds)
        {
            try
            {
                Forms.Application.SetHighDpiMode(Forms.HighDpiMode.PerMonitorV2);
                Forms.Application.EnableVisualStyles();
                Forms.Application.SetCompatibleTextRenderingDefault(false);
                using var form = new QuietForm(display, motionFirstSeconds,
                    () => Volatile.Read(ref _disposeRequested) != 0,
                    () => _ready.TrySetResult());
                lock (_gate) { _form = form; }
                if (Volatile.Read(ref _disposeRequested) == 0)
                {
                    Forms.Application.Run(form);
                }
            }
            catch (Exception exception)
            {
                _ready.TrySetException(exception);
            }
            finally
            {
                lock (_gate) { _form = null; }
                _closed.Cancel();
                _ready.TrySetCanceled();
                _completed.TrySetResult();
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposeRequested, 1) != 0) { return; }
            QuietForm? form;
            lock (_gate) { form = _form; }
            if (form is { IsHandleCreated: true, IsDisposed: false })
            {
                try { form.BeginInvoke(form.Close); }
                catch (InvalidOperationException) { }
            }

            try { await _completed.Task.WaitAsync(TimeSpan.FromSeconds(3)); }
            catch (TimeoutException)
            {
                Console.Error.WriteLine("Quiet surface cleanup exceeded three seconds; its 20-second automatic close remains active.");
            }
        }
    }

    private sealed class QuietForm : Forms.Form
    {
        private const string Caption = "ClipForge static-screen diagnostic — closes automatically";
        private readonly Forms.Timer _closeTimer = new() { Interval = 20_000 };
        private readonly Forms.Timer _motionTimer = new() { Interval = 16 };
        private readonly int _motionFirstSeconds;
        private readonly Stopwatch _motionClock = Stopwatch.StartNew();
        private long? _motionEndsAt;
        private bool _animating;
        private readonly Func<bool> _stopRequested;
        private readonly Action _ready;
        private bool _readyReported;

        internal QuietForm(DisplayOption display, int motionFirstSeconds, Func<bool> stopRequested, Action ready)
        {
            _motionFirstSeconds = motionFirstSeconds;
            _animating = motionFirstSeconds > 0;
            _stopRequested = stopRequested;
            _ready = ready;
            AutoScaleMode = Forms.AutoScaleMode.None;
            StartPosition = Forms.FormStartPosition.Manual;
            Bounds = new Rectangle(display.Left, display.Top, display.Width, display.Height);
            BackColor = Color.FromArgb(20, 26, 34);
            DoubleBuffered = true;
            FormBorderStyle = Forms.FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            Text = Caption;
            _closeTimer.Tick += (_, _) => Close();
            _motionTimer.Tick += (_, _) =>
            {
                if (_motionEndsAt is { } stopAt && Stopwatch.GetTimestamp() >= stopAt)
                {
                    _motionTimer.Stop();
                    _animating = false;
                    Refresh();
                    Console.WriteLine("DDA diagnostic motion stopped; the remainder is static.");
                    return;
                }

                using var graphics = CreateGraphics();
                RenderMotion(graphics);
            };
        }

        internal void MarkCaptureStarted()
        {
            if (_motionFirstSeconds > 0)
            {
                // Animate during the capability probe, then retain the requested
                // motion interval in the actual recording before becoming quiet.
                _motionEndsAt = Stopwatch.GetTimestamp() +
                    (long)(_motionFirstSeconds * (double)Stopwatch.Frequency);
            }
        }

        protected override void OnShown(EventArgs eventArgs)
        {
            base.OnShown(eventArgs);
            if (_stopRequested()) { Close(); return; }
            _closeTimer.Start();
            if (_animating) { _motionTimer.Start(); }
            Refresh();
        }

        protected override void OnPaint(Forms.PaintEventArgs eventArgs)
        {
            base.OnPaint(eventArgs);
            if (_animating)
            {
                RenderMotion(eventArgs.Graphics);
            }
            using var font = new Font(FontFamily.GenericSansSerif, 30f, FontStyle.Regular, GraphicsUnit.Pixel);
            using var brush = new SolidBrush(Color.WhiteSmoke);
            eventArgs.Graphics.DrawString(Caption, font, brush, 48f, 48f);
            eventArgs.Graphics.DrawString("Static content • Escape closes this diagnostic", font, brush, 48f, 102f);
            // The default is static. After the optional motion interval, its
            // timer stops and only ordinary exposure painting can occur.
            if (!_readyReported) { _readyReported = true; _ready(); }
        }

        private void RenderMotion(Graphics graphics)
        {
            var step = (int)(_motionClock.Elapsed.TotalMilliseconds / 16);
            graphics.Clear(Color.FromArgb(25 + step * 3 % 160, 30, 60));
            using var brush = new SolidBrush(Color.Lime);
            graphics.FillRectangle(brush, step * 23 % Math.Max(1, ClientSize.Width - 150),
                ClientSize.Height / 2, 150, 150);
        }

        protected override bool ProcessCmdKey(ref Forms.Message message, Forms.Keys keyData)
        {
            if (keyData == Forms.Keys.Escape) { Close(); return true; }
            return base.ProcessCmdKey(ref message, keyData);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _closeTimer.Stop(); _closeTimer.Dispose();
                _motionTimer.Stop(); _motionTimer.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
