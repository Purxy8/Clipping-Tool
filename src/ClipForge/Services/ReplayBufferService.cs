using System.Globalization;
using System.Security;
using System.Text;
using ClipForge.Capture;
using ClipForge.Models;

namespace ClipForge.Services;

/// <summary>
/// Owns FFmpeg's continuous segment process, prunes its disk-backed ring, and
/// remuxes a frozen segment snapshot into a user-facing MP4 clip.
/// </summary>
public sealed class ReplayBufferService : IAsyncDisposable
{
    private const int MaximumDiagnosticLines = 60;
    private const int MaximumDiagnosticLineCharacters = 1000;
    private static readonly TimeSpan CaptureHealthPollInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan BufferRefreshInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan CaptureBoundaryWaitTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan CaptureRefreshGracefulStopTimeout = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan CaptureCleanupTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan CaptureRecoveryRetryDelay = TimeSpan.FromSeconds(30);
    // Windows Graphics Capture frame pools can lose delivery cadence after a
    // long, uninterrupted desktop session while FFmpeg itself remains alive.
    // Renew only the capture process at a bounded age; the disk ring survives.
    internal static readonly TimeSpan CaptureProcessMaximumAge = TimeSpan.FromMinutes(30);

    private readonly FfmpegSetupService _ffmpegSetupService;
    private readonly FfmpegCapabilityProbe _capabilityProbe = new();
    private readonly VideoEncodingStrategy? _captureStrategyOverride;
    private readonly string _bufferRoot;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly object _fileGate = new();
    private readonly object _diagnosticGate = new();
    private readonly HashSet<string> _protectedSegments = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _diagnosticLines = new();
    private readonly List<WasapiAudioPipe> _audioPipes = [];
    private readonly List<BufferedSegment> _segments = [];

    private Process? _captureProcess;
    private CaptureProcessJob? _captureProcessJob;
    private CancellationTokenSource? _sessionCancellation;
    private Task? _monitorTask;
    private Task? _diagnosticTask;
    private Task? _captureProgressTask;
    private string? _segmentDirectory;
    private TimeSpan _retention = TimeSpan.FromMinutes(2);
    private ReplayStateSnapshot _state = new(
        ReplayState.Stopped,
        TimeSpan.Zero,
        TimeSpan.FromMinutes(2),
        0);
    private string? _lastSavedPath;
    private string? _activeEncoderDescription;
    private CaptureConfiguration? _activeConfiguration;
    private VideoEncodingStrategy? _activeCaptureStrategy;
    private string? _activeFfmpegPath;
    private CaptureSessionPlan? _lastCapturePlan;
    private CaptureProgressSample? _latestCaptureProgress;
    private CaptureStarvationWatchdog? _captureStarvationWatchdog;
    private long _bufferBytes;
    private long _reportedDroppedAudioBlocks;
    private int _nextSegmentNumber;
    private int _isRunning;
    private int _isSaving;
    private int _isStopping;
    private int _recoveryRequested;
    private long _recoveryRetryNotBefore;
    private int _recoveryRetryGeneration;
    private int _disposed;

    public ReplayBufferService(
        FfmpegSetupService? ffmpegSetupService = null,
        string? bufferRoot = null)
    {
        _ffmpegSetupService = ffmpegSetupService ?? new FfmpegSetupService();
        _bufferRoot = Path.GetFullPath(bufferRoot ?? GetDefaultBufferRoot());

        // MainWindow creates this service only after primary single-instance
        // ownership is established, so pre-existing sessions are crash residue.
        CleanupStaleBuffers();
    }

    internal ReplayBufferService(
        FfmpegSetupService ffmpegSetupService,
        string bufferRoot,
        VideoEncodingStrategy captureStrategyOverride)
        : this(ffmpegSetupService, bufferRoot)
    {
        _captureStrategyOverride = captureStrategyOverride;
    }

    internal static string GetDefaultBufferRoot()
    {
        var localApplicationData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        using var process = Process.GetCurrentProcess();
        return Path.Combine(
            localApplicationData,
            "ClipForge",
            "Buffer",
            $"WindowsSession-{process.SessionId}");
    }

    public event EventHandler<ReplayStateSnapshot>? StateChanged;

    internal event EventHandler<CaptureRecoveryRequestedEventArgs>? CaptureRecoveryRequested;

    public bool IsRunning => Volatile.Read(ref _isRunning) != 0;

    public string? ActiveEncoderDescription => _activeEncoderDescription;

    internal CaptureSessionPlan? LastCapturePlan => Volatile.Read(ref _lastCapturePlan);

    internal static bool ShouldScheduleCaptureRefresh(
        DesktopCaptureBackend captureBackend,
        TimeSpan processUptime) =>
        captureBackend == DesktopCaptureBackend.WindowsGraphicsCapture &&
        processUptime >= CaptureProcessMaximumAge;

    public int? CaptureProcessId
    {
        get
        {
            var process = _captureProcess;
            if (process is null)
            {
                return null;
            }

            try
            {
                return process.Id;
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }
    }

    public Task StartAsync(
        CaptureConfiguration configuration,
        CancellationToken cancellationToken) =>
        StartAsync(
            configuration,
            cancellationToken,
            sessionStrategyOverride: null,
            sourceSafetyMode: false);

    internal async Task StartAsync(
        CaptureConfiguration configuration,
        CancellationToken cancellationToken,
        VideoEncodingStrategy? sessionStrategyOverride,
        bool sourceSafetyMode)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ThrowIfDisposed();
        await _saveGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (IsRunning)
                {
                    throw new InvalidOperationException("Instant Replay is already running.");
                }

                if (_captureProcess is not null || _segmentDirectory is not null)
                {
                    await StopCoreAsync(deleteBuffer: true, publishStopped: false).ConfigureAwait(false);
                }

                ValidateConfiguration(configuration);
                _retention = configuration.Retention;
                Publish(new ReplayStateSnapshot(
                    ReplayState.Starting,
                    TimeSpan.Zero,
                    _retention,
                    0,
                    "Preparing the capture engine…"));

                var ffmpegPath = _ffmpegSetupService.FindExecutable()
                    ?? throw new InvalidOperationException(
                        "The capture engine is not installed. Use Install engine and try again.");

                EnsureSafeBufferRoot();
                Directory.CreateDirectory(_bufferRoot);
                EnsureSafeBufferRoot();
                CleanupStaleBuffers();
                _segmentDirectory = Path.Combine(
                    _bufferRoot,
                    $"session-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}");
                EnsureSafeBufferRoot();
                Directory.CreateDirectory(_segmentDirectory);
                var sessionInfo = new DirectoryInfo(_segmentDirectory);
                if (!sessionInfo.Exists ||
                    !IsSafeBufferDirectoryPath(
                        _bufferRoot,
                        sessionInfo.FullName,
                        sessionInfo.Attributes))
                {
                    throw new InvalidOperationException(
                        "The replay buffer session path is not a regular local directory.");
                }

                lock (_fileGate)
                {
                    ResetSegmentIndexLocked();
                }

                _sessionCancellation = new CancellationTokenSource();
                _reportedDroppedAudioBlocks = 0;
                Volatile.Write(ref _latestCaptureProgress, null);
                _captureStarvationWatchdog = new CaptureStarvationWatchdog(
                    configuration.FramesPerSecond);
                Volatile.Write(ref _recoveryRequested, 0);
                Volatile.Write(ref _recoveryRetryNotBefore, 0);
                _ = Interlocked.Increment(ref _recoveryRetryGeneration);
                lock (_diagnosticGate)
                {
                    _diagnosticLines.Clear();
                }

                if (sourceSafetyMode &&
                    (configuration.Resolution.Width is not null ||
                     configuration.Resolution.Height is not null))
                {
                    throw new InvalidOperationException(
                        "Source safety recovery requires the current display's native geometry.");
                }

                // A strategy verified for a fixed output size is not proof that
                // the same WGC/encoder path can create native Source surfaces.
                // Always probe the actual Source geometry for the safety pass;
                // retain overrides only for the first same-geometry reacquire
                // (and for explicit capture-smoke sessions).
                var effectiveStrategyOverride = sourceSafetyMode
                    ? null
                    : sessionStrategyOverride ?? _captureStrategyOverride;
                var capabilitySelection = effectiveStrategyOverride is null
                    ? await (sourceSafetyMode
                            ? new FfmpegCapabilityProbe()
                            : _capabilityProbe)
                        .SelectAsync(ffmpegPath, configuration, cancellationToken)
                        .ConfigureAwait(false)
                    : new FfmpegCapabilitySelection(
                        effectiveStrategyOverride,
                        sessionStrategyOverride is null
                            ? $"Capture smoke override selected {effectiveStrategyOverride.Description}."
                            : $"Capture recovery retained the verified {effectiveStrategyOverride.Description} path.");
                if (sourceSafetyMode &&
                    capabilitySelection.Strategy.CaptureBackend !=
                    DesktopCaptureBackend.WindowsGraphicsCapture)
                {
                    EnqueueDiagnostic(capabilitySelection.Diagnostics);
                    throw new InvalidOperationException(
                        "ClipForge could not safely verify Windows Graphics Capture at the display's " +
                        "native Source resolution. Instant Replay was stopped instead of starting an " +
                        "unverified high-impact fallback. Try starting replay again or choose Source manually.");
                }

                _activeEncoderDescription = capabilitySelection.Strategy.Description +
                    (sourceSafetyMode ? " (Source safety mode)" : string.Empty);
                _activeConfiguration = configuration;
                _activeCaptureStrategy = capabilitySelection.Strategy;
                _activeFfmpegPath = ffmpegPath;
                Volatile.Write(
                    ref _lastCapturePlan,
                    new CaptureSessionPlan(
                        configuration.Display,
                        configuration.Resolution,
                        capabilitySelection.Strategy));
                EnqueueDiagnostic(capabilitySelection.Diagnostics);

                CreateAudioPipes(configuration);
                var arguments = FfmpegArgumentBuilder.BuildCaptureArguments(
                    configuration,
                    _audioPipes.Select(pipe => pipe.Specification).ToArray(),
                    capabilitySelection.Strategy,
                    _segmentDirectory);

                var captureProcess = CreateProcess(
                    ffmpegPath,
                    arguments,
                    redirectStandardInput: true,
                    redirectStandardOutput: true);
                try
                {
                    if (!captureProcess.Start())
                    {
                        throw new InvalidOperationException("Windows could not start the capture engine.");
                    }

                    // Attach immediately after Process.Start. If ClipForge is
                    // terminated before graceful cleanup, closing this job's
                    // last handle makes Windows terminate FFmpeg as well.
                    _captureProcessJob = CaptureProcessJob.Attach(captureProcess);
                }
                catch
                {
                    TryKill(captureProcess);
                    _captureProcessJob?.Dispose();
                    _captureProcessJob = null;
                    captureProcess.Dispose();
                    throw;
                }

                _captureProcess = captureProcess;
                if (!ProcessTuning.TryApplyCapturePriority(
                        captureProcess,
                        capabilitySelection.Strategy))
                {
                    EnqueueDiagnostic(
                        "Windows did not allow ClipForge to apply the capture process priority policy.");
                }

                captureProcess.StandardInput.AutoFlush = true;
                _diagnosticTask = PumpDiagnosticsAsync(captureProcess);
                _captureProgressTask = PumpCaptureProgressAsync(captureProcess);
                Volatile.Write(ref _isRunning, 1);

                if (_audioPipes.Count > 0)
                {
                    var connectionTasks = _audioPipes
                        .Select(pipe => pipe.ConnectAndStartAsync(_sessionCancellation.Token))
                        .ToArray();
                    await Task.WhenAll(connectionTasks)
                        .WaitAsync(TimeSpan.FromSeconds(15), cancellationToken)
                        .ConfigureAwait(false);
                }

                await Task.Delay(500, cancellationToken).ConfigureAwait(false);
                if (captureProcess.HasExited)
                {
                    throw new InvalidOperationException(BuildCaptureFailureMessage());
                }

                Volatile.Write(ref _isStopping, 0);
                _monitorTask = MonitorCaptureAsync(
                    captureProcess,
                    configuration,
                    capabilitySelection.Strategy,
                    _sessionCancellation.Token);
                Publish(new ReplayStateSnapshot(
                    ReplayState.Buffering,
                    TimeSpan.Zero,
                    _retention,
                    0,
                    $"Instant Replay is filling its buffer using {_activeEncoderDescription}."));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Volatile.Write(ref _isRunning, 0);
                try
                {
                    await StopCoreAsync(deleteBuffer: true, publishStopped: false).ConfigureAwait(false);
                }
                catch
                {
                    // Cancellation takes priority over best-effort cleanup.
                }

                Publish(new ReplayStateSnapshot(
                    ReplayState.Stopped,
                    TimeSpan.Zero,
                    _retention,
                    0,
                    "Instant Replay start was cancelled.",
                    _lastSavedPath));
                throw;
            }
            catch (Exception exception)
            {
                Volatile.Write(ref _isRunning, 0);
                var message = exception is InvalidOperationException
                    ? exception.Message
                    : $"Instant Replay could not start. {exception.Message}";

                try
                {
                    await StopCoreAsync(deleteBuffer: true, publishStopped: false).ConfigureAwait(false);
                }
                catch
                {
                    // Preserve the original startup failure.
                }

                Publish(new ReplayStateSnapshot(
                    ReplayState.Faulted,
                    TimeSpan.Zero,
                    _retention,
                    0,
                    message));
                throw new InvalidOperationException(message, exception);
            }
            finally
            {
                _lifecycleGate.Release();
            }
        }
        finally
        {
            _saveGate.Release();
        }
    }

    public async Task StopAsync()
    {
        ThrowIfDisposed();
        await _saveGate.WaitAsync().ConfigureAwait(false);

        try
        {
            await _lifecycleGate.WaitAsync().ConfigureAwait(false);
            try
            {
                await StopCoreAsync(deleteBuffer: true, publishStopped: true).ConfigureAwait(false);
            }
            finally
            {
                _lifecycleGate.Release();
            }
        }
        finally
        {
            _saveGate.Release();
        }
    }

    /// <summary>
    /// Renews an aged Windows Graphics Capture process without throwing away
    /// the completed replay ring. The expected process id makes a queued
    /// refresh harmless if a manual stop/restart already replaced the session.
    /// </summary>
    internal async Task<bool> RefreshCaptureAsync(
        int? expectedProcessId,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await _saveGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var process = _captureProcess;
                var configuration = _activeConfiguration;
                var strategy = _activeCaptureStrategy;
                var ffmpegPath = _activeFfmpegPath;
                var segmentDirectory = _segmentDirectory;
                if (!IsRunning ||
                    process is null ||
                    configuration is null ||
                    strategy is null ||
                    string.IsNullOrWhiteSpace(ffmpegPath) ||
                    string.IsNullOrWhiteSpace(segmentDirectory) ||
                    expectedProcessId is null ||
                    CaptureProcessId != expectedProcessId)
                {
                    return false;
                }

                if (strategy.CaptureBackend != DesktopCaptureBackend.WindowsGraphicsCapture)
                {
                    return false;
                }

                if (!IsSafeActiveBufferDirectory(segmentDirectory))
                {
                    Volatile.Write(ref _isStopping, 1);
                    _ = await StopCaptureResourcesForRefreshAsync().ConfigureAwait(false);
                    Volatile.Write(ref _isRunning, 0);
                    Volatile.Write(ref _isStopping, 0);
                    const string unsafeBufferMessage =
                        "The replay buffer path became unsafe. ClipForge stopped capture and refused to follow or delete it.";
                    Publish(_state with
                    {
                        State = ReplayState.Faulted,
                        Message = unsafeBufferMessage
                    });
                    throw new SecurityException(unsafeBufferMessage);
                }

                string verifiedFfmpegPath;
                FileStream? verifiedExecutableLease = null;
                try
                {
                    // The private FFmpeg payload lives below a user-writable app-data
                    // directory. Re-hash it for every new process instead of trusting
                    // the path cached when this session first started.
                    verifiedFfmpegPath = _ffmpegSetupService.FindExecutable()
                        ?? throw new InvalidOperationException(
                            "The verified capture engine is no longer available. Replay was left unchanged.");
                    if (!Path.GetFullPath(verifiedFfmpegPath).Equals(
                            Path.GetFullPath(ffmpegPath),
                            StringComparison.OrdinalIgnoreCase))
                    {
                        throw new SecurityException(
                            "The capture engine path changed during replay. ClipForge refused to launch it.");
                    }

                    // Hash the exact open file handle and deny write/delete sharing
                    // until Process.Start has consumed this path. This closes the
                    // boundary-wait TOCTOU window for the user-writable payload.
                    verifiedExecutableLease = _ffmpegSetupService.OpenVerifiedExecutableLease(
                            verifiedFfmpegPath)
                        ?? throw new SecurityException(
                            "The capture engine failed its final pinned-file verification. Replay was left unchanged.");
                }
                catch
                {
                    verifiedExecutableLease?.Dispose();
                    // Nothing was stopped yet. Keep the existing replay alive,
                    // release the one-shot latch, and throttle the next attempt so
                    // a transient AV/file-system error cannot disable recovery or
                    // create a half-second error/hash loop forever.
                    DeferCaptureRecoveryRetry(process.Id);
                    throw;
                }

                // Keep logical replay running while maintenance owns _saveGate.
                // A hotkey arriving now waits and saves the retained ring after
                // renewal instead of being rejected as though replay were off.
                Publish(_state with
                {
                    Message = "Refreshing the capture engine while keeping your replay buffer..."
                });

                try
                {
                    var boundaryWaitStarted = Stopwatch.GetTimestamp();
                    var reachedSegmentBoundary = await WaitForNextCaptureSegmentBoundaryAsync(
                            process,
                            cancellationToken)
                        .ConfigureAwait(false);
                    var boundaryWait = Stopwatch.GetElapsedTime(boundaryWaitStarted);
                    var replacementStarted = Stopwatch.GetTimestamp();

                    if (!IsSafeActiveBufferDirectory(segmentDirectory))
                    {
                        throw new SecurityException(
                            "The replay buffer path changed during renewal. ClipForge refused to follow or delete it.");
                    }

                    Volatile.Write(ref _isStopping, 1);
                    var oldProcessExited = await StopCaptureResourcesForRefreshAsync(
                            terminateAtCompletedBoundary: reachedSegmentBoundary)
                        .ConfigureAwait(false);
                    if (!oldProcessExited)
                    {
                        throw new InvalidOperationException(
                            "The previous capture resources did not finish bounded cleanup. ClipForge refused to start an overlapping recorder.");
                    }

                    int segmentStartNumber;
                    lock (_fileGate)
                    {
                        RefreshSegmentIndexLocked();
                        DiscardNewestCaptureTailLocked();
                        segmentStartNumber = _nextSegmentNumber;
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    _sessionCancellation = new CancellationTokenSource();
                    _reportedDroppedAudioBlocks = 0;
                    Volatile.Write(ref _latestCaptureProgress, null);
                    _captureStarvationWatchdog = new CaptureStarvationWatchdog(
                        configuration.FramesPerSecond);

                    lock (_diagnosticGate)
                    {
                        _diagnosticLines.Clear();
                    }

                    CreateAudioPipes(configuration);
                    var arguments = FfmpegArgumentBuilder.BuildCaptureArguments(
                        configuration,
                        _audioPipes.Select(pipe => pipe.Specification).ToArray(),
                        strategy,
                        segmentDirectory,
                        segmentStartNumber);
                    var replacement = CreateProcess(
                        verifiedFfmpegPath,
                        arguments,
                        redirectStandardInput: true,
                        redirectStandardOutput: true);
                    try
                    {
                        if (!replacement.Start())
                        {
                            throw new InvalidOperationException(
                                "Windows could not renew the capture engine.");
                        }

                        _captureProcessJob = CaptureProcessJob.Attach(replacement);
                    }
                    catch
                    {
                        TryKill(replacement);
                        _captureProcessJob?.Dispose();
                        _captureProcessJob = null;
                        replacement.Dispose();
                        throw;
                    }

                    _captureProcess = replacement;
                    if (!ProcessTuning.TryApplyCapturePriority(replacement, strategy))
                    {
                        EnqueueDiagnostic(
                            "Windows did not allow ClipForge to reapply the capture process priority policy.");
                    }

                    replacement.StandardInput.AutoFlush = true;
                    _diagnosticTask = PumpDiagnosticsAsync(replacement);
                    _captureProgressTask = PumpCaptureProgressAsync(replacement);
                    if (_audioPipes.Count > 0)
                    {
                        var connectionTasks = _audioPipes
                            .Select(pipe => pipe.ConnectAndStartAsync(_sessionCancellation.Token))
                            .ToArray();
                        await Task.WhenAll(connectionTasks)
                            .WaitAsync(TimeSpan.FromSeconds(15), cancellationToken)
                            .ConfigureAwait(false);
                    }

                    await Task.Delay(500, cancellationToken).ConfigureAwait(false);
                    if (replacement.HasExited)
                    {
                        throw new InvalidOperationException(BuildCaptureFailureMessage());
                    }

                    Volatile.Write(ref _recoveryRequested, 0);
                    Volatile.Write(ref _recoveryRetryNotBefore, 0);
                    _ = Interlocked.Increment(ref _recoveryRetryGeneration);
                    Volatile.Write(ref _isStopping, 0);
                    Volatile.Write(ref _isRunning, 1);
                    _monitorTask = MonitorCaptureAsync(
                        replacement,
                        configuration,
                        strategy,
                        _sessionCancellation.Token);
                    EnqueueDiagnostic(
                        string.Create(
                            CultureInfo.InvariantCulture,
                            $"Renewed the WGC capture process at segment {segmentStartNumber}; " +
                            $"completed replay segments were retained. BoundaryAligned={reachedSegmentBoundary}; " +
                            $"boundaryWaitMs={boundaryWait.TotalMilliseconds:0}; " +
                            $"replacementMs={Stopwatch.GetElapsedTime(replacementStarted).TotalMilliseconds:0}."));
                    RefreshBufferState();
                    return true;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    Volatile.Write(ref _isStopping, 1);
                    await StopCaptureResourcesForRefreshAsync().ConfigureAwait(false);
                    Volatile.Write(ref _isRunning, 0);
                    Volatile.Write(ref _isStopping, 0);
                    throw;
                }
                catch (Exception exception)
                {
                    Volatile.Write(ref _isStopping, 1);
                    await StopCaptureResourcesForRefreshAsync().ConfigureAwait(false);
                    Volatile.Write(ref _isRunning, 0);
                    Volatile.Write(ref _isStopping, 0);
                    var message = exception is InvalidOperationException
                        ? exception.Message
                        : $"The capture engine could not be refreshed. {exception.Message}";
                    Publish(_state with
                    {
                        State = ReplayState.Faulted,
                        Message = message
                    });
                    throw new InvalidOperationException(message, exception);
                }
                finally
                {
                    verifiedExecutableLease?.Dispose();
                }
            }
            finally
            {
                _lifecycleGate.Release();
            }
        }
        finally
        {
            _saveGate.Release();
        }
    }

    public void UpdateRetention(TimeSpan retention)
    {
        ThrowIfDisposed();
        if (retention < TimeSpan.FromSeconds(FfmpegArgumentBuilder.SegmentSeconds) ||
            retention > TimeSpan.FromHours(1))
        {
            throw new ArgumentOutOfRangeException(
                nameof(retention),
                "Replay length must be between two seconds and one hour.");
        }

        lock (_fileGate)
        {
            _retention = retention;
        }

        RefreshBufferState();
    }

    public async Task<string> SaveClipAsync(
        TimeSpan requestedDuration,
        string saveDirectory,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(saveDirectory);
        if (requestedDuration <= TimeSpan.Zero || requestedDuration > TimeSpan.FromHours(1))
        {
            throw new ArgumentOutOfRangeException(
                nameof(requestedDuration),
                "Clip length must be between one second and one hour.");
        }

        await _saveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<string> selectedSegments = [];
        string? manifestPath = null;
        string? partialPath = null;

        try
        {
            if (!IsRunning)
            {
                throw new InvalidOperationException("Instant Replay is not running.");
            }

            TimeSpan actualDuration;
            lock (_fileGate)
            {
                var requestedCount = checked((int)Math.Ceiling(
                    requestedDuration.TotalSeconds / FfmpegArgumentBuilder.SegmentSeconds));
                var completed = GetCompletedSegmentsLocked(requestedCount);
                var selectedCount = completed.Count;
                if (selectedCount == 0)
                {
                    throw new InvalidOperationException(
                        "The replay buffer is still warming up. Wait a moment and try again.");
                }

                selectedSegments = completed;
                foreach (var path in selectedSegments)
                {
                    _protectedSegments.Add(path);
                }

                actualDuration = TimeSpan.FromSeconds(Math.Min(
                    requestedDuration.TotalSeconds,
                    selectedCount * FfmpegArgumentBuilder.SegmentSeconds));
            }

            Volatile.Write(ref _isSaving, 1);
            Publish(_state with
            {
                State = ReplayState.Saving,
                Message = "Saving your clip…"
            });

            var ffmpegPath = _ffmpegSetupService.FindExecutable()
                ?? throw new InvalidOperationException("The capture engine is no longer available.");
            Directory.CreateDirectory(saveDirectory);
            var finalPath = GetUniqueClipPath(saveDirectory);
            partialPath = Path.Combine(
                saveDirectory,
                $".{Path.GetFileNameWithoutExtension(finalPath)}-{Guid.NewGuid():N}.partial.mp4");
            manifestPath = Path.Combine(
                _segmentDirectory ?? _bufferRoot,
                $"export-{Guid.NewGuid():N}.txt");

            var manifestLines = BuildConcatManifestLines(selectedSegments);
            await File.WriteAllLinesAsync(
                    manifestPath,
                    manifestLines,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                    cancellationToken)
                .ConfigureAwait(false);

            var selectedDuration = TimeSpan.FromSeconds(
                selectedSegments.Count * FfmpegArgumentBuilder.SegmentSeconds);
            var trimFromStart = selectedDuration - actualDuration;
            var arguments = FfmpegArgumentBuilder.BuildConcatArguments(
                manifestPath,
                partialPath,
                trimFromStart,
                actualDuration);
            await RunExportProcessAsync(ffmpegPath, arguments, cancellationToken)
                .ConfigureAwait(false);

            if (!File.Exists(partialPath) || new FileInfo(partialPath).Length == 0)
            {
                throw new InvalidDataException("The capture engine produced an empty clip.");
            }

            File.Move(partialPath, finalPath);
            partialPath = null;
            _lastSavedPath = finalPath;
            return finalPath;
        }
        finally
        {
            Volatile.Write(ref _isSaving, 0);
            lock (_fileGate)
            {
                foreach (var path in selectedSegments)
                {
                    _protectedSegments.Remove(path);
                }
            }

            TryDeleteFile(manifestPath);
            TryDeleteFile(partialPath);
            _saveGate.Release();
            RefreshBufferState(_lastSavedPath);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        await _saveGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await _lifecycleGate.WaitAsync().ConfigureAwait(false);
            try
            {
                await StopCoreAsync(deleteBuffer: true, publishStopped: false).ConfigureAwait(false);
                Volatile.Write(ref _disposed, 1);
            }
            finally
            {
                _lifecycleGate.Release();
            }
        }
        finally
        {
            _saveGate.Release();
        }

        _lifecycleGate.Dispose();
        _saveGate.Dispose();
    }

    private void CreateAudioPipes(CaptureConfiguration configuration)
    {
        if (configuration.CaptureSystemAudio)
        {
            _audioPipes.Add(new WasapiAudioPipe(
                configuration.OutputAudioDevice
                ?? throw new InvalidOperationException("No desktop audio output was selected."),
                captureLoopback: true));
        }

        if (configuration.CaptureMicrophone)
        {
            _audioPipes.Add(new WasapiAudioPipe(
                configuration.MicrophoneDevice
                ?? throw new InvalidOperationException("No microphone was selected."),
                captureLoopback: false));
        }
    }

    private async Task<bool> StopCaptureResourcesForRefreshAsync(
        bool terminateAtCompletedBoundary = false,
        TimeSpan? gracefulStopTimeout = null)
    {
        var process = _captureProcess;
        var processJob = _captureProcessJob;
        var processExited = process is null;
        try
        {
            if (process is not null)
            {
                if (terminateAtCompletedBoundary)
                {
                    // The previous file is finalized once the next segment is
                    // observed. End the old WGC process immediately and discard
                    // only that newly opened tail, avoiding an extra graceful-q
                    // delay in the real capture handoff.
                    TryKill(process);
                    using var exitTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                    try
                    {
                        await process.WaitForExitAsync(exitTimeout.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        // CaptureProcessJob disposal below is the final bound.
                    }
                }
                else
                {
                    await StopProcessGracefullyAsync(
                            process,
                            gracefulStopTimeout ?? CaptureRefreshGracefulStopTimeout)
                        .ConfigureAwait(false);
                }
            }
        }
        catch (Exception exception)
        {
            EnqueueDiagnostic(
                $"The previous capture process resisted renewal cleanup: {exception.GetBaseException().Message}");
            if (process is not null)
            {
                TryKill(process);
            }
        }
        finally
        {
            try
            {
                // Kill-on-close is the final bounded containment guarantee when
                // FFmpeg or a child process did not acknowledge either q or Kill.
                processJob?.Dispose();
            }
            catch (Exception exception)
            {
                EnqueueDiagnostic(
                    $"The previous capture job reported a cleanup error: {exception.GetBaseException().Message}");
            }

            _captureProcessJob = null;
        }

        if (process is not null)
        {
            try
            {
                if (!process.HasExited)
                {
                    await process.WaitForExitAsync()
                        .WaitAsync(CaptureCleanupTimeout)
                        .ConfigureAwait(false);
                }

                processExited = process.HasExited;
            }
            catch (Exception exception) when (
                exception is InvalidOperationException or ObjectDisposedException or TimeoutException)
            {
                EnqueueDiagnostic(
                    "The previous capture process did not confirm exit after job containment closed.");
            }
        }

        var sessionCancellation = _sessionCancellation;
        var cleanupCompleted = true;
        try
        {
            sessionCancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // A concurrent failure path may already have released the token.
        }

        var monitorTask = _monitorTask;
        if (monitorTask is not null)
        {
            cleanupCompleted &= await ObserveCaptureCleanupTaskAsync(monitorTask, "monitor")
                .ConfigureAwait(false);
        }

        foreach (var audioPipe in _audioPipes.ToArray())
        {
            try
            {
                cleanupCompleted &= await ObserveCaptureCleanupTaskAsync(
                        audioPipe.DisposeAsync().AsTask(),
                        "audio input")
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                cleanupCompleted = false;
                EnqueueDiagnostic(
                    $"An audio input reported a cleanup error during renewal: {exception.GetBaseException().Message}");
            }
        }

        if (_diagnosticTask is { } diagnosticTask)
        {
            cleanupCompleted &= await ObserveCaptureCleanupTaskAsync(
                    diagnosticTask,
                    "diagnostic pump")
                .ConfigureAwait(false);
        }

        if (_captureProgressTask is { } progressTask)
        {
            cleanupCompleted &= await ObserveCaptureCleanupTaskAsync(
                    progressTask,
                    "progress pump")
                .ConfigureAwait(false);
        }

        if (!processExited || !cleanupCompleted)
        {
            // Do not reuse shared process/task/audio fields while any old work
            // can still resume. A later Stop/Exit may retry cleanup, but no new
            // encoder is allowed to overlap this uncertain generation.
            _captureProcess = process;
            return false;
        }

        _audioPipes.Clear();

        try
        {
            process?.Dispose();
        }
        catch (Exception exception)
        {
            EnqueueDiagnostic(
                $"The previous capture handle reported a cleanup error: {exception.GetBaseException().Message}");
        }

        try
        {
            sessionCancellation?.Dispose();
        }
        catch (ObjectDisposedException)
        {
            // Already released by a concurrent failure path.
        }

        _captureProcess = null;
        _sessionCancellation = null;
        _monitorTask = null;
        _diagnosticTask = null;
        _captureProgressTask = null;
        Volatile.Write(ref _latestCaptureProgress, null);
        _captureStarvationWatchdog = null;
        return true;
    }

    private async Task<bool> ObserveCaptureCleanupTaskAsync(Task task, string componentName)
    {
        try
        {
            await task.WaitAsync(CaptureCleanupTimeout).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // Stream closure is expected while FFmpeg is being replaced.
            return true;
        }
        catch (TimeoutException)
        {
            EnqueueDiagnostic(
                $"The previous capture {componentName} did not finish within the bounded cleanup window.");
            _ = task.ContinueWith(
                completed => _ = completed.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
            return false;
        }
        catch (Exception exception)
        {
            EnqueueDiagnostic(
                $"The previous capture {componentName} faulted during renewal: {exception.GetBaseException().Message}");
            return true;
        }
    }

    private async Task<bool> WaitForNextCaptureSegmentBoundaryAsync(
        Process process,
        CancellationToken cancellationToken)
    {
        int initialSegmentNumber;
        lock (_fileGate)
        {
            RefreshSegmentIndexLocked();
            initialSegmentNumber = _nextSegmentNumber;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(CaptureBoundaryWaitTimeout);
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50), timeout.Token)
                    .ConfigureAwait(false);
                try
                {
                    if (process.HasExited)
                    {
                        return false;
                    }
                }
                catch (InvalidOperationException)
                {
                    return false;
                }

                lock (_fileGate)
                {
                    RefreshSegmentIndexLocked();
                    if (_nextSegmentNumber > initialSegmentNumber)
                    {
                        return true;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Segment creation should occur every two seconds. A bounded timeout
            // prevents rotation from stalling if an already degraded WGC source
            // has stopped advancing entirely.
            return false;
        }
    }

    private void DiscardNewestCaptureTailLocked()
    {
        if (_segments.Count == 0)
        {
            return;
        }

        var tailIndex = _segments.Count - 1;
        var tail = _segments[tailIndex];
        if (_protectedSegments.Contains(tail.Path) || !TryDeleteFile(tail.Path))
        {
            // Never overwrite an uncertain file. If deletion is unavailable,
            // the replacement starts at the next number and leaves this short
            // tail intact rather than risking completed replay data.
            return;
        }

        _bufferBytes = Math.Max(0, _bufferBytes - tail.Length);
        _segments.RemoveAt(tailIndex);
        _nextSegmentNumber = Math.Max(0, _nextSegmentNumber - 1);
    }

    private async Task StopCoreAsync(bool deleteBuffer, bool publishStopped)
    {
        Volatile.Write(ref _isStopping, 1);
        var hadSession = _captureProcess is not null ||
                         _segmentDirectory is not null ||
                         _audioPipes.Count > 0;
        if (hadSession && publishStopped)
        {
            Publish(_state with
            {
                State = ReplayState.Stopping,
                Message = "Stopping Instant Replay…"
            });
        }

        Volatile.Write(ref _isRunning, 0);

        var processExited = await StopCaptureResourcesForRefreshAsync(
                gracefulStopTimeout: TimeSpan.FromSeconds(5))
            .ConfigureAwait(false);
        if (!processExited)
        {
            Volatile.Write(ref _isStopping, 0);
            var message =
                "The capture resources did not finish bounded cleanup. ClipForge retained ownership and refused to discard the replay buffer.";
            Publish(_state with
            {
                State = ReplayState.Faulted,
                Message = message
            });
            throw new InvalidOperationException(message);
        }

        _activeConfiguration = null;
        _activeCaptureStrategy = null;
        _activeFfmpegPath = null;

        var oldSegmentDirectory = _segmentDirectory;
        _segmentDirectory = null;
        if (deleteBuffer)
        {
            TryDeleteBufferDirectory(oldSegmentDirectory);
        }

        lock (_fileGate)
        {
            _protectedSegments.Clear();
            ResetSegmentIndexLocked();
        }

        Volatile.Write(ref _isStopping, 0);
        if (publishStopped)
        {
            Publish(new ReplayStateSnapshot(
                ReplayState.Stopped,
                TimeSpan.Zero,
                _retention,
                0,
                LastSavedPath: _lastSavedPath));
        }
    }

    private async Task MonitorCaptureAsync(
        Process process,
        CaptureConfiguration configuration,
        VideoEncodingStrategy strategy,
        CancellationToken cancellationToken)
    {
        using var healthTimer = new PeriodicTimer(CaptureHealthPollInterval);
        var captureStarted = Stopwatch.GetTimestamp();
        var lastBufferRefresh = Stopwatch.GetTimestamp();
        var lastCaptureActivity = captureStarted;
        long lastProgressFrame = -1;
        long lastProgressOutputTime = -1;
        long lastEvaluatedProgressTimestamp = -1;
        var lastSegmentNumber = -1;
        long lastBufferBytes = -1;

        try
        {
            while (await healthTimer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                if (process.HasExited)
                {
                    Volatile.Write(ref _isRunning, 0);
                    if (Volatile.Read(ref _isStopping) == 0)
                    {
                        _sessionCancellation?.Cancel();
                        await DisposeAudioPipesAfterFailureAsync().ConfigureAwait(false);
                        Publish(_state with
                        {
                            State = ReplayState.Faulted,
                            Message = BuildCaptureFailureMessage()
                        });
                    }

                    return;
                }

                Task? failedPumpTask = null;
                var failedPumpName = string.Empty;
                if (_captureProgressTask?.IsCompleted == true)
                {
                    failedPumpTask = _captureProgressTask;
                    failedPumpName = "progress";
                }
                else if (_diagnosticTask?.IsCompleted == true)
                {
                    failedPumpTask = _diagnosticTask;
                    failedPumpName = "diagnostic";
                }

                if (failedPumpTask is not null &&
                    Volatile.Read(ref _isStopping) == 0 &&
                    Volatile.Read(ref _recoveryRequested) == 0)
                {
                    var detail = failedPumpTask.Exception?.GetBaseException().Message ??
                        "The stream reader reached EOF unexpectedly.";
                    var pumpDiagnostic =
                        $"The FFmpeg {failedPumpName} pump stopped while capture remained alive. {detail}";
                    if (strategy.CaptureBackend == DesktopCaptureBackend.WindowsGraphicsCapture)
                    {
                        RequestCaptureRecovery(
                            CaptureRecoveryReason.CaptureHang,
                            pumpDiagnostic);
                    }
                    else
                    {
                        Volatile.Write(ref _isRunning, 0);
                        _sessionCancellation?.Cancel();
                        EnqueueDiagnostic($"Capture faulted: {pumpDiagnostic}");
                        TryKill(process);
                        await DisposeAudioPipesAfterFailureAsync().ConfigureAwait(false);
                        Publish(_state with
                        {
                            State = ReplayState.Faulted,
                            Message =
                                "ClipForge lost contact with the compatibility capture process. " +
                                "Instant Replay was stopped safely."
                        });
                        return;
                    }
                }

                var stoppedAudioPipe = _audioPipes.FirstOrDefault(pipe => pipe.Completion.IsCompleted);
                if (stoppedAudioPipe is not null && Volatile.Read(ref _isStopping) == 0)
                {
                    string detail;
                    try
                    {
                        await stoppedAudioPipe.Completion.ConfigureAwait(false);
                        detail = "An audio device stopped sending data.";
                    }
                    catch (Exception exception)
                    {
                        detail = exception.GetBaseException().Message;
                    }

                    Volatile.Write(ref _isRunning, 0);
                    _sessionCancellation?.Cancel();
                    TryKill(process);
                    await DisposeAudioPipesAfterFailureAsync().ConfigureAwait(false);
                    Publish(_state with
                    {
                        State = ReplayState.Faulted,
                        Message = $"Audio capture stopped unexpectedly. {detail}"
                    });
                    return;
                }

                if (Stopwatch.GetElapsedTime(lastBufferRefresh) < BufferRefreshInterval)
                {
                    continue;
                }

                lastBufferRefresh = Stopwatch.GetTimestamp();
                var droppedAudioBlocks = _audioPipes.Sum(pipe => pipe.DroppedSampleBlocks);
                if (droppedAudioBlocks > _reportedDroppedAudioBlocks)
                {
                    EnqueueDiagnostic(
                        $"Audio backpressure dropped {droppedAudioBlocks - _reportedDroppedAudioBlocks} input block(s); memory remained bounded.");
                    _reportedDroppedAudioBlocks = droppedAudioBlocks;
                }

                RefreshBufferState();

                var progress = Volatile.Read(ref _latestCaptureProgress);
                int segmentNumber;
                long bufferBytes;
                lock (_fileGate)
                {
                    segmentNumber = _nextSegmentNumber;
                    bufferBytes = _bufferBytes;
                }

                var progressAdvanced = progress is not null &&
                    (progress.Frame != lastProgressFrame ||
                     progress.OutputTimeMicroseconds != lastProgressOutputTime);
                var segmentAdvanced = segmentNumber != lastSegmentNumber ||
                                      bufferBytes != lastBufferBytes;
                if (progressAdvanced || segmentAdvanced)
                {
                    lastCaptureActivity = Stopwatch.GetTimestamp();
                }

                if (progress is not null)
                {
                    lastProgressFrame = progress.Frame;
                    lastProgressOutputTime = progress.OutputTimeMicroseconds;
                }

                lastSegmentNumber = segmentNumber;
                lastBufferBytes = bufferBytes;

                var captureProcessUptime = Stopwatch.GetElapsedTime(captureStarted);
                if (ShouldScheduleCaptureRefresh(
                        strategy.CaptureBackend,
                        captureProcessUptime))
                {
                    RequestCaptureRecovery(
                        CaptureRecoveryReason.ScheduledRefresh,
                        string.Create(
                            CultureInfo.InvariantCulture,
                            $"The WGC capture process reached its bounded {CaptureProcessMaximumAge.TotalMinutes:0}-minute lifetime; renewing it prevents long-session frame-pool degradation."));
                }

                if (strategy.CaptureBackend == DesktopCaptureBackend.WindowsGraphicsCapture &&
                    progress is not null &&
                    progress.Timestamp != lastEvaluatedProgressTimestamp)
                {
                    lastEvaluatedProgressTimestamp = progress.Timestamp;
                    var context = CaptureForegroundContextProbe.Read(configuration.Display);
                    var assessment = _captureStarvationWatchdog?.Observe(
                        progress,
                        context,
                        captureProcessUptime);
                    if (assessment is not null &&
                        Stopwatch.GetElapsedTime(captureStarted) >= TimeSpan.FromSeconds(8))
                    {
                        RequestCaptureRecovery(
                            CaptureRecoveryReason.SourceStarvation,
                            string.Create(
                                CultureInfo.InvariantCulture,
                                $"WGC supplied only {assessment.UniqueFramesPerSecond:0.0} unique FPS " +
                                $"over {assessment.Window.TotalSeconds:0.0}s " +
                                $"({assessment.DuplicateRatio:P0} CFR duplicates)."));
                    }
                }

                if (Stopwatch.GetElapsedTime(captureStarted) >= TimeSpan.FromSeconds(8) &&
                    Stopwatch.GetElapsedTime(lastCaptureActivity) >= TimeSpan.FromSeconds(7))
                {
                    const string hangDiagnostic =
                        "FFmpeg remained alive but neither progress nor the replay segments advanced for seven seconds.";
                    if (strategy.CaptureBackend ==
                        DesktopCaptureBackend.WindowsGraphicsCapture)
                    {
                        RequestCaptureRecovery(
                            CaptureRecoveryReason.CaptureHang,
                            hangDiagnostic);
                    }
                    else
                    {
                        // Automatic recovery is deliberately WGC-only. Leaving
                        // a hung GDI process marked Running would make saves and
                        // controls operate on a dead buffer, so fail the session
                        // explicitly instead of raising an event MainWindow must
                        // reject as unsafe.
                        Volatile.Write(ref _isRunning, 0);
                        _sessionCancellation?.Cancel();
                        EnqueueDiagnostic($"Capture faulted: {hangDiagnostic}");
                        TryKill(process);
                        await DisposeAudioPipesAfterFailureAsync().ConfigureAwait(false);
                        Publish(_state with
                        {
                            State = ReplayState.Faulted,
                            Message =
                                "The compatibility capture path stopped producing replay data. " +
                                "Instant Replay was stopped safely; start it again to recheck the capture engine."
                        });
                        return;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal stop.
        }
        catch (Exception exception)
        {
            Volatile.Write(ref _isRunning, 0);
            if (Volatile.Read(ref _isStopping) == 0)
            {
                _sessionCancellation?.Cancel();
                TryKill(process);
                await DisposeAudioPipesAfterFailureAsync().ConfigureAwait(false);
                Publish(_state with
                {
                    State = ReplayState.Faulted,
                    Message = $"The replay buffer stopped unexpectedly. {exception.Message}"
                });
            }
        }
    }

    private async Task DisposeAudioPipesAfterFailureAsync()
    {
        foreach (var audioPipe in _audioPipes.ToArray())
        {
            try
            {
                await audioPipe.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                // The primary capture error is more useful than a cleanup failure.
            }
        }
    }

    private void RefreshBufferState(string? lastSavedPath = null)
    {
        if (_segmentDirectory is null || Volatile.Read(ref _isStopping) != 0)
        {
            return;
        }

        TimeSpan available;
        long bytes;

        lock (_fileGate)
        {
            RefreshSegmentIndexLocked();
            var maximumCompletedSegments = checked((int)Math.Ceiling(
                _retention.TotalSeconds / FfmpegArgumentBuilder.SegmentSeconds));

            while (Math.Max(0, _segments.Count - 1) > maximumCompletedSegments)
            {
                var candidateIndex = _segments.FindIndex(0, _segments.Count - 1, segment =>
                    !_protectedSegments.Contains(segment.Path));
                if (candidateIndex < 0)
                {
                    break;
                }

                var candidate = _segments[candidateIndex];
                if (!TryDeleteFile(candidate.Path))
                {
                    break;
                }

                _bufferBytes = Math.Max(0, _bufferBytes - candidate.Length);
                _segments.RemoveAt(candidateIndex);
            }

            var completedCount = _segments
                .Take(Math.Max(0, _segments.Count - 1))
                .Count(segment => segment.Length > 0);
            available = TimeSpan.FromSeconds(Math.Min(
                _retention.TotalSeconds,
                completedCount * FfmpegArgumentBuilder.SegmentSeconds));
            bytes = _bufferBytes;
        }

        var replayState = Volatile.Read(ref _isSaving) != 0
            ? ReplayState.Saving
            : available >= _retention
                ? ReplayState.Ready
                : ReplayState.Buffering;
        Publish(new ReplayStateSnapshot(
            replayState,
            available,
            _retention,
            bytes,
            replayState == ReplayState.Ready
                ? $"Instant Replay is ready using {_activeEncoderDescription}."
                : replayState == ReplayState.Saving
                    ? "Saving your clip…"
                    : $"Instant Replay is filling its buffer using {_activeEncoderDescription}.",
            lastSavedPath ?? _lastSavedPath));
    }

    private List<string> GetCompletedSegmentsLocked(int maximumCount)
    {
        if (maximumCount <= 0)
        {
            return [];
        }

        RefreshSegmentIndexLocked();
        var completedCount = _segments.Count;
        if (IsRunning && completedCount > 0)
        {
            completedCount--;
        }

        // Revalidate only the tail needed by this export. A 30-second hotkey
        // save therefore checks about 15 files, not all ~1,800 entries in a
        // one-hour ring. Walk farther back only when a file disappeared.
        var result = new List<string>(Math.Min(maximumCount, completedCount));
        for (var index = completedCount - 1;
             index >= 0 && result.Count < maximumCount;
             index--)
        {
            var segment = _segments[index];
            if (GetFileLengthSafely(segment.Path) > 0)
            {
                result.Add(segment.Path);
            }
        }

        result.Reverse();
        return result;
    }

    private void RefreshSegmentIndexLocked()
    {
        if (string.IsNullOrWhiteSpace(_segmentDirectory) ||
            !Directory.Exists(_segmentDirectory))
        {
            return;
        }

        // FFmpeg's segment muxer creates zero-padded, strictly increasing file
        // names. Follow that sequence instead of enumerating, sorting, and
        // stat'ing the entire one-hour ring on every health poll.
        while (_nextSegmentNumber < int.MaxValue)
        {
            var nextPath = Path.Combine(
                _segmentDirectory,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"segment-{_nextSegmentNumber:D9}.mkv"));
            if (!File.Exists(nextPath))
            {
                break;
            }

            UpdateNewestSegmentLengthLocked();
            _segments.Add(new BufferedSegment(nextPath, 0));
            _nextSegmentNumber++;
        }

        UpdateNewestSegmentLengthLocked();
    }

    private void UpdateNewestSegmentLengthLocked()
    {
        if (_segments.Count == 0)
        {
            return;
        }

        var newestIndex = _segments.Count - 1;
        var newest = _segments[newestIndex];
        var currentLength = GetFileLengthSafely(newest.Path);
        _bufferBytes = Math.Max(0, _bufferBytes + currentLength - newest.Length);
        _segments[newestIndex] = newest with { Length = currentLength };
    }

    private void ResetSegmentIndexLocked()
    {
        _segments.Clear();
        _bufferBytes = 0;
        _nextSegmentNumber = 0;
    }

    private static long GetFileLengthSafely(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or FileNotFoundException)
        {
            return 0;
        }
    }

    /// <summary>
    /// Builds a concat-demuxer manifest whose timeline advances by the known
    /// video segment cadence rather than each Matroska file's longest stream.
    /// AAC packets do not divide evenly into two seconds at 48 kHz, so relying
    /// on container duration inserts a visible video gap at every segment join.
    /// </summary>
    internal static IReadOnlyList<string> BuildConcatManifestLines(
        IEnumerable<string> segmentPaths)
    {
        ArgumentNullException.ThrowIfNull(segmentPaths);

        var duration = FfmpegArgumentBuilder.SegmentSeconds.ToString(
            "0.000000",
            CultureInfo.InvariantCulture);
        var lines = new List<string>();
        foreach (var path in segmentPaths)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(path);
            lines.Add($"file '{EscapeConcatPath(path)}'");
            lines.Add($"duration {duration}");
        }

        return lines;
    }

    private async Task PumpDiagnosticsAsync(Process process)
    {
        while (await process.StandardError.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            EnqueueDiagnostic(line);
        }
    }

    private async Task PumpCaptureProgressAsync(Process process)
    {
        var parser = new CaptureProgressParser();
        while (await process.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            if (parser.TryParse(
                    line,
                    Stopwatch.GetTimestamp(),
                    out var sample) &&
                sample is not null)
            {
                Volatile.Write(ref _latestCaptureProgress, sample);
            }
        }
    }

    private void RequestCaptureRecovery(
        CaptureRecoveryReason reason,
        string diagnostic)
    {
        var retryNotBefore = Volatile.Read(ref _recoveryRetryNotBefore);
        if (Volatile.Read(ref _isStopping) != 0 ||
            (retryNotBefore > 0 && Stopwatch.GetTimestamp() < retryNotBefore) ||
            Interlocked.Exchange(ref _recoveryRequested, 1) != 0)
        {
            return;
        }

        EnqueueDiagnostic($"Capture recovery requested: {diagnostic}");
        var handlers = CaptureRecoveryRequested;
        if (handlers is null)
        {
            return;
        }

        var args = new CaptureRecoveryRequestedEventArgs(
            reason,
            diagnostic,
            CaptureProcessId);
        foreach (EventHandler<CaptureRecoveryRequestedEventArgs> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, args);
            }
            catch
            {
                // Recovery subscribers cannot be allowed to stop health monitoring.
            }
        }
    }

    private void DeferCaptureRecoveryRetry(int expectedProcessId)
    {
        var delayTicks = checked((long)(
            CaptureRecoveryRetryDelay.TotalSeconds * Stopwatch.Frequency));
        Volatile.Write(
            ref _recoveryRetryNotBefore,
            checked(Stopwatch.GetTimestamp() + delayTicks));
        Volatile.Write(ref _recoveryRequested, 0);
        var generation = Interlocked.Increment(ref _recoveryRetryGeneration);
        var cancellationToken = _sessionCancellation?.Token ?? CancellationToken.None;
        _ = RetryCaptureRecoveryAfterDelayAsync(
            expectedProcessId,
            generation,
            cancellationToken);
    }

    private async Task RetryCaptureRecoveryAfterDelayAsync(
        int expectedProcessId,
        int generation,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(CaptureRecoveryRetryDelay, cancellationToken).ConfigureAwait(false);
            if (generation != Volatile.Read(ref _recoveryRetryGeneration) ||
                !IsRunning ||
                CaptureProcessId != expectedProcessId)
            {
                return;
            }

            Volatile.Write(ref _recoveryRetryNotBefore, 0);
            RequestCaptureRecovery(
                CaptureRecoveryReason.ScheduledRefresh,
                "Retrying a capture-process renewal after a transient executable or buffer preflight failure.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The original capture process stopped or was replaced first.
        }
    }

    private void EnqueueDiagnostic(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        var trimmed = line.Trim();
        if (trimmed.Length > MaximumDiagnosticLineCharacters)
        {
            trimmed = trimmed[^MaximumDiagnosticLineCharacters..];
        }

        lock (_diagnosticGate)
        {
            _diagnosticLines.Enqueue(trimmed);
            while (_diagnosticLines.Count > MaximumDiagnosticLines)
            {
                _ = _diagnosticLines.Dequeue();
            }
        }
    }

    private string BuildCaptureFailureMessage()
    {
        string? detail;
        lock (_diagnosticGate)
        {
            detail = SelectMostUsefulDiagnostic(_diagnosticLines);
        }

        var engine = string.IsNullOrWhiteSpace(_activeEncoderDescription)
            ? "The capture engine"
            : $"The capture engine ({_activeEncoderDescription})";
        return string.IsNullOrWhiteSpace(detail)
            ? $"{engine} stopped unexpectedly. Check that the selected display and audio devices are available."
            : $"{engine} stopped unexpectedly. {detail}";
    }

    internal static string? SelectMostUsefulDiagnostic(IEnumerable<string> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        var lines = diagnostics.Where(line => !string.IsNullOrWhiteSpace(line)).ToArray();
        string[] highValueMarkers =
        [
            "failed to capture",
            "error opening input",
            "error while opening encoder",
            "failed to setup",
            "no capable devices",
            "access is denied",
            "permission denied"
        ];

        foreach (var marker in highValueMarkers)
        {
            var match = lines.LastOrDefault(line =>
                line.Contains(marker, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return match;
            }
        }

        return lines.LastOrDefault(line =>
                   !line.Contains("nothing was written", StringComparison.OrdinalIgnoreCase) &&
                   !line.Contains("output file does not contain", StringComparison.OrdinalIgnoreCase))
               ?? lines.LastOrDefault();
    }

    private static async Task RunExportProcessAsync(
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        using var process = CreateProcess(executable, arguments, redirectStandardInput: false);
        if (!process.Start())
        {
            throw new InvalidOperationException("Windows could not start the clip exporter.");
        }

        _ = ProcessTuning.TryApplyLowImpactPriority(process);

        // FFmpeg can repeat warnings for every frame. Drain stderr continuously
        // so the process cannot block, but retain only the final bounded line
        // instead of growing an in-memory string for the whole export.
        var errorTask = ReadLastDiagnosticLineAsync(process.StandardError);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            TryKill(process);
            _ = errorTask.ContinueWith(
                task => _ = task.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
            throw;
        }

        var error = await errorTask.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(error)
                    ? "The capture engine could not assemble the clip."
                    : $"The capture engine could not assemble the clip. {error}");
        }
    }

    private static async Task<string?> ReadLastDiagnosticLineAsync(StreamReader reader)
    {
        string? lastLine = null;
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var trimmed = line.Trim();
            lastLine = trimmed.Length <= MaximumDiagnosticLineCharacters
                ? trimmed
                : trimmed[^MaximumDiagnosticLineCharacters..];
        }

        return lastLine;
    }

    private static Process CreateProcess(
        string executable,
        IReadOnlyList<string> arguments,
        bool redirectStandardInput,
        bool redirectStandardOutput = false)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardInput = redirectStandardInput,
            RedirectStandardOutput = redirectStandardOutput
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return new Process { StartInfo = startInfo };
    }

    private static async Task StopProcessGracefullyAsync(
        Process process,
        TimeSpan? gracefulTimeout = null)
    {
        if (process.HasExited)
        {
            return;
        }

        try
        {
            using var signalTimeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
            await process.StandardInput.WriteLineAsync("q")
                .WaitAsync(signalTimeout.Token)
                .ConfigureAwait(false);
            await process.StandardInput.FlushAsync(signalTimeout.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is IOException or InvalidOperationException or ObjectDisposedException or
            OperationCanceledException)
        {
            // The process may have already closed its control stream.
        }

        using var timeout = new CancellationTokenSource(
            gracefulTimeout ?? TimeSpan.FromSeconds(5));
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            using var killTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            try
            {
                await process.WaitForExitAsync(killTimeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // The owning CaptureProcessJob is disposed by the caller and
                // provides the final kill-on-close boundary. Never wait forever
                // while holding ClipForge's save and lifecycle gates.
            }
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Best effort; the process may have exited between the checks.
        }
    }

    private void CleanupStaleBuffers()
    {
        if (!IsSafeBufferRootPath(_bufferRoot))
        {
            return;
        }

        try
        {
            foreach (var directory in Directory.EnumerateDirectories(_bufferRoot, "session-*"))
            {
                // Single-instance ownership is established before this service
                // is created, so every pre-existing session is crash residue.
                // Purge it immediately instead of retaining screen/audio data.
                TryDeleteBufferDirectory(directory);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Stale cleanup must never prevent a new capture session.
        }
    }

    private void TryDeleteBufferDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !IsSafeBufferRootPath(_bufferRoot))
        {
            return;
        }

        try
        {
            var directory = new DirectoryInfo(Path.GetFullPath(path));
            if (directory.Exists &&
                IsSafeBufferDirectoryPath(_bufferRoot, directory.FullName, directory.Attributes) &&
                IsSafeBufferRootPath(_bufferRoot))
            {
                Directory.Delete(directory.FullName, recursive: true);
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // A later startup can retry stale session cleanup.
        }
    }

    private bool IsSafeActiveBufferDirectory(string path)
    {
        try
        {
            var directory = new DirectoryInfo(Path.GetFullPath(path));
            return directory.Exists &&
                   IsSafeBufferDirectoryPath(
                       _bufferRoot,
                       directory.FullName,
                       directory.Attributes);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or
                NotSupportedException or SecurityException)
        {
            return false;
        }
    }

    /// <summary>
    /// Restricts recursive deletion to regular, top-level session directories under the configured
    /// buffer root. In particular, junctions and symbolic links are never followed by cleanup.
    /// </summary>
    internal static bool IsSafeBufferDirectoryPath(
        string bufferRoot,
        string candidatePath,
        FileAttributes attributes)
    {
        if ((attributes & FileAttributes.Directory) == 0 ||
            (attributes & FileAttributes.ReparsePoint) != 0 ||
            !IsSafeBufferRootPath(bufferRoot) ||
            !Path.IsPathFullyQualified(bufferRoot) ||
            !Path.IsPathFullyQualified(candidatePath))
        {
            return false;
        }

        try
        {
            var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(bufferRoot));
            var normalizedCandidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidatePath));
            var parent = Path.GetDirectoryName(normalizedCandidate);
            var name = Path.GetFileName(normalizedCandidate);

            return normalizedRoot.Equals(parent, StringComparison.OrdinalIgnoreCase) &&
                   name.StartsWith("session-", StringComparison.OrdinalIgnoreCase) &&
                   name.Length > "session-".Length;
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Rejects an app-owned replay root when it or any existing ancestor is a
    /// reparse point. Recursive cleanup must never traverse a junction or
    /// symbolic link into an unrelated directory.
    /// </summary>
    internal static bool IsSafeBufferRootPath(string bufferRoot)
    {
        if (string.IsNullOrWhiteSpace(bufferRoot) ||
            !Path.IsPathFullyQualified(bufferRoot))
        {
            return false;
        }

        try
        {
            var current = Path.TrimEndingDirectorySeparator(Path.GetFullPath(bufferRoot));
            while (!string.IsNullOrWhiteSpace(current))
            {
                try
                {
                    var attributes = File.GetAttributes(current);
                    if ((attributes & FileAttributes.Directory) == 0 ||
                        (attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        return false;
                    }
                }
                catch (Exception exception) when (
                    exception is FileNotFoundException or DirectoryNotFoundException)
                {
                    // A not-yet-created leaf is safe only if every existing
                    // parent that will contain it is a regular directory.
                }

                var parent = Path.GetDirectoryName(current);
                if (string.IsNullOrWhiteSpace(parent) ||
                    parent.Equals(current, StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                current = parent;
            }

            return true;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or
                NotSupportedException or SecurityException)
        {
            return false;
        }
    }

    private void EnsureSafeBufferRoot()
    {
        if (!IsSafeBufferRootPath(_bufferRoot))
        {
            throw new InvalidOperationException(
                "The replay buffer path or one of its parent folders is a junction or symbolic link. " +
                "ClipForge refused to use it to protect unrelated files.");
        }
    }

    private static bool TryDeleteFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return true;
        }

        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Buffer pruning/export cleanup is best effort.
            return false;
        }
    }

    private static string EscapeConcatPath(string path) =>
        Path.GetFullPath(path)
            .Replace('\\', '/')
            .Replace("'", "'\\''", StringComparison.Ordinal);

    private static string GetUniqueClipPath(string saveDirectory)
    {
        var stem = $"Clip_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}";
        var candidate = Path.Combine(saveDirectory, $"{stem}.mp4");
        var suffix = 2;
        while (File.Exists(candidate))
        {
            candidate = Path.Combine(saveDirectory, $"{stem}_{suffix}.mp4");
            suffix++;
        }

        return candidate;
    }

    private static void ValidateConfiguration(CaptureConfiguration configuration)
    {
        if (configuration.Retention < TimeSpan.FromSeconds(FfmpegArgumentBuilder.SegmentSeconds) ||
            configuration.Retention > TimeSpan.FromHours(1))
        {
            throw new ArgumentOutOfRangeException(
                nameof(configuration),
                "Replay length must be between two seconds and one hour.");
        }

        if (configuration.CaptureSystemAudio && configuration.OutputAudioDevice is null)
        {
            throw new ArgumentException("Desktop audio is enabled without an output device.", nameof(configuration));
        }

        if (configuration.CaptureMicrophone && configuration.MicrophoneDevice is null)
        {
            throw new ArgumentException("Microphone capture is enabled without a microphone.", nameof(configuration));
        }
    }

    private void Publish(ReplayStateSnapshot snapshot)
    {
        _state = snapshot;
        var handlers = StateChanged;
        if (handlers is null)
        {
            return;
        }

        foreach (EventHandler<ReplayStateSnapshot> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, snapshot);
            }
            catch
            {
                // UI or telemetry subscribers must not be able to stop capture.
            }
        }
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    private readonly record struct BufferedSegment(string Path, long Length);
}
