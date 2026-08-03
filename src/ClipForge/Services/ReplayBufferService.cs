using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security;
using System.Text;
using System.Text.Json;
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
    private const int MaximumLegacyCleanupDirectories = 2;
    private const int MaximumLegacyDirectoryCandidates = 64;
    private const int MaximumLegacyCleanupFilesPerDirectory = 10_000;
    private const int MaximumInactiveWindowsSessionRootsPerRun = 2;
    private const int MaximumWindowsSessionRootCandidates = 16;
    private const int MaximumWindowsSessionRootInspectionsPerRun = 64;
    private const int MaximumWindowsSessionCleanupSuffixLength = 10;
    private const string WindowsSessionCleanupCursorFileName =
        ".windows-session-cleanup.cursor";
    private const int MaximumWindowsSessionDirectoriesPerRoot = 8;
    private const int MaximumWindowsSessionFilesPerDirectory = 4_096;
    internal const int MaximumCurrentSessionCleanupDirectoryCandidatesPerRun = 64;
    internal const int MaximumCurrentSessionCleanupFilesPerRun = 2_048;
    internal const int MaximumSegmentDeleteAttemptsPerRefresh = 64;
    private static readonly TimeSpan CaptureHealthPollInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan BufferRefreshInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan CapturePriorityRefreshInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CaptureBoundaryWaitTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan CaptureRefreshGracefulStopTimeout = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan CaptureCleanupTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan CaptureRecoveryRetryDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CaptureRecoveryRetryExhaustedCooldown =
        TimeSpan.FromMinutes(5);
    private const int MaximumCaptureRecoveryRetryAttempts = 2;
    private static readonly TimeSpan ExportProcessMaximumRuntime = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan LegacyBufferMinimumInactivity = TimeSpan.FromHours(24);
    internal static readonly TimeSpan CurrentSessionCleanupTimeBudget =
        TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan InitialBufferMaintenanceStartWaitBudget =
        TimeSpan.FromSeconds(1);
    private static readonly TimeSpan DegradedCaptureReprobeInitialDelay =
        TimeSpan.FromMinutes(5);
    private static readonly TimeSpan DegradedCaptureReprobeMaximumDelay =
        TimeSpan.FromMinutes(30);
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
    private readonly object _stateGate = new();
    private readonly object _statePublicationGate = new();
    private readonly object _diagnosticGate = new();
    private readonly object _discontinuousRefreshGate = new();
    private readonly object _captureRecoveryRetryGate = new();
    private readonly HashSet<string> _protectedSegments = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _diagnosticLines = new();
    private readonly ConcurrentQueue<CaptureProgressEnvelope> _captureProgressSamples = new();
    private readonly List<WasapiAudioPipe> _audioPipes = [];
    private readonly List<BufferedSegment> _segments = [];
    private readonly CaptureRecoveryRequestGate _captureRecoveryRequestGate = new();
    private readonly CaptureRuntimeJournal _runtimeJournal = new();
    private readonly ScheduledCaptureRefreshCoordinator _scheduledCaptureRefreshCoordinator;
    private readonly ScheduledCaptureRefreshCoordinator
        _discontinuousCaptureRefreshCoordinator;
    private readonly ScheduledCaptureRefreshCoordinator _degradedCaptureReprobeCoordinator;
    private readonly CancellationTokenSource _initialBufferMaintenanceCancellation = new();
    private readonly CancellationTokenSource _captureRecoveryRetryCancellation = new();
    private readonly TaskCompletionSource _disposeCompletion = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _initialBufferMaintenanceTask;

    private Process? _captureProcess;
    private CaptureProcessJob? _captureProcessJob;
    private CancellationTokenSource? _sessionCancellation;
    private Task? _monitorTask;
    private Task? _diagnosticTask;
    private Task? _captureProgressTask;
    private string? _lastCaptureCleanupFailure;
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
    private CapturePerformanceProfile _activeCapturePerformanceProfile =
        CapturePerformanceProfile.LowImpact;
    private string? _activeFfmpegPath;
    private CaptureSessionPlan? _lastCapturePlan;
    private CaptureProgressSample? _latestCaptureProgress;
    private CaptureStarvationWatchdog? _captureStarvationWatchdog;
    private long _bufferBytes;
    private long _reportedDroppedAudioBlocks;
    private int _nextSegmentNumber;
    private int _activeCaptureGeneration;
    private int _quarantinedGenerationHeadSegmentNumber = -1;
    private int _exportBlockedCaptureGeneration = -1;
    private CancellationTokenSource? _activeSaveInvalidation;
    private int _activeSaveCaptureGeneration = -1;
    private int _untrustedDeleteCursorSegmentNumber = -1;
    private int _trustedDeleteCursorSegmentNumber = -1;
    private int _isRunning;
    private int _isSaving;
    private int _saveOperationPending;
    private int _isStopping;
    private int _captureRecoveryRetrySequence;
    private CaptureRecoveryRetryState _captureRecoveryRetryState =
        new(
            CurrentGeneration: 0,
            TaskGeneration: 0,
            NotBeforeTimestamp: 0,
            Task.CompletedTask);
    private int _capturePriorityRefreshWarningReported;
    private int _activeStrategyUsesCapabilityProbe;
    private int _degradedCaptureReprobeAttempt;
    private long _degradedCaptureReprobeNotBeforeUtcTicks;
    private long _statePublicationVersion;
    private int _disposeStarted;
    private int _disposed;
    private int _captureSessionIdentity;
    private long _discontinuousRefreshRequestedEpoch;
    private long _discontinuousRefreshCompletedEpoch;
    private int _discontinuousRefreshSessionIdentity;
    private string _discontinuousRefreshDiagnostic = string.Empty;
    private int _discontinuousRefreshContinuationScheduled;
    private Task _discontinuousRefreshContinuationTask = Task.CompletedTask;

    public ReplayBufferService(
        FfmpegSetupService? ffmpegSetupService = null,
        string? bufferRoot = null)
        : this(
            ffmpegSetupService,
            bufferRoot,
            initialBufferMaintenanceOverride: null,
            initialize: true)
    {
    }

    private ReplayBufferService(
        FfmpegSetupService? ffmpegSetupService,
        string? bufferRoot,
        Func<Task>? initialBufferMaintenanceOverride,
        bool initialize)
    {
        _ = initialize;
        _ffmpegSetupService = ffmpegSetupService ?? new FfmpegSetupService();
        _bufferRoot = Path.GetFullPath(bufferRoot ?? GetDefaultBufferRoot());
        _scheduledCaptureRefreshCoordinator = new ScheduledCaptureRefreshCoordinator(
            RunScheduledCaptureRefreshAsync,
            (processId, diagnostic, exception) =>
                EnqueueDiagnostic(
                    $"Background WGC renewal for process {processId} failed: " +
                    $"{diagnostic} {exception.GetBaseException().Message}"));
        _discontinuousCaptureRefreshCoordinator =
            new ScheduledCaptureRefreshCoordinator(
                RunDiscontinuousCaptureRefreshAsync,
                (processId, diagnostic, exception) =>
                    EnqueueDiagnostic(
                        $"Background display-transition capture renewal for process {processId} failed: " +
                        $"{diagnostic} {exception.GetBaseException().Message}"));
        _degradedCaptureReprobeCoordinator = new ScheduledCaptureRefreshCoordinator(
            RunDegradedCaptureReprobeAsync,
            (processId, diagnostic, exception) =>
            {
                DeferDegradedCaptureReprobe();
                EnqueueDiagnostic(
                    $"Background degraded-capture reprobe for process {processId} failed: " +
                    $"{diagnostic} {exception.GetBaseException().Message}");
            });

        // Crash residue can contain thousands of large two-second segments.
        // Never enumerate or recursively delete it on the WPF constructor path.
        // StartAsync awaits this worker before it creates a new session, so
        // capture and disk/AV maintenance still cannot overlap.
        _initialBufferMaintenanceTask = Task.Run(
            () => RunInitialBufferMaintenanceAsync(
                initialBufferMaintenanceOverride,
                _initialBufferMaintenanceCancellation.Token));
    }

    internal ReplayBufferService(
        FfmpegSetupService ffmpegSetupService,
        string bufferRoot,
        Func<Task> initialBufferMaintenanceOverride)
        : this(
            ffmpegSetupService,
            bufferRoot,
            initialBufferMaintenanceOverride ??
            throw new ArgumentNullException(nameof(initialBufferMaintenanceOverride)),
            initialize: true)
    {
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

    internal Task WaitForInitialBufferMaintenanceAsync() =>
        _initialBufferMaintenanceTask;

    public event EventHandler<ReplayStateSnapshot>? StateChanged;

    internal event EventHandler<CaptureRecoveryRequestedEventArgs>? CaptureRecoveryRequested;

    public bool IsRunning => Volatile.Read(ref _isRunning) != 0;

    public string? ActiveEncoderDescription => _activeEncoderDescription;

    internal CaptureSessionPlan? LastCapturePlan => Volatile.Read(ref _lastCapturePlan);

    internal CapturePerformanceProfile ActiveCapturePerformanceProfile =>
        _activeCapturePerformanceProfile;

    // Capture smoke tests must distinguish a real numbering hole from a
    // generation head that the engine deliberately quarantined and deleted.
    // Return only provenance; the mutable segment index remains encapsulated.
    internal ReplayCaptureGenerationSnapshot ReadCaptureGenerationSnapshotForTesting()
    {
        lock (_fileGate)
        {
            return new ReplayCaptureGenerationSnapshot(
                _activeCaptureGeneration,
                _quarantinedGenerationHeadSegmentNumber);
        }
    }

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
            sourceSafetyMode: false,
            performanceProfileOverride: null);

    internal async Task StartAsync(
        CaptureConfiguration configuration,
        CancellationToken cancellationToken,
        VideoEncodingStrategy? sessionStrategyOverride,
        bool sourceSafetyMode,
        CapturePerformanceProfile? performanceProfileOverride = null)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await StartCoreAsync(
                        configuration,
                        cancellationToken,
                        sessionStrategyOverride,
                        sourceSafetyMode,
                        performanceProfileOverride,
                        allowFreshCapabilityRetry: attempt == 0)
                    .ConfigureAwait(false);
                return;
            }
            catch (RetryableCaptureLaunchException) when (attempt == 0)
            {
                // The exact capability entry was removed by StartCoreAsync after
                // the verified strategy failed in the real capture graph. Retry
                // the complete lifecycle once so a fresh probe can choose another
                // WGC/transfer/encoder path without creating a restart loop.
            }
        }
    }

    private async Task StartCoreAsync(
        CaptureConfiguration configuration,
        CancellationToken cancellationToken,
        VideoEncodingStrategy? sessionStrategyOverride,
        bool sourceSafetyMode,
        CapturePerformanceProfile? performanceProfileOverride,
        bool allowFreshCapabilityRetry)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ThrowIfDisposed();

        FfmpegCapabilitySelection? selectedCapability = null;
        string? selectedFfmpegPath = null;
        var capabilityWasProbed = false;
        var realCaptureLaunchFailed = false;
        var selectedPerformanceProfile =
            performanceProfileOverride ??
            (sourceSafetyMode
                ? CapturePerformanceProfile.Resilient
                : CapturePerformanceProfile.LowImpact);
        try
        {
            await _initialBufferMaintenanceTask
                .WaitAsync(InitialBufferMaintenanceStartWaitBudget, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // A local antivirus/filter driver can make one synchronous directory
            // operation ignore the cleanup worker's soft time budget. Cancel the
            // entire pipeline before creating a live session so a delayed
            // enumerator can neither delete that session nor compete with capture.
            _initialBufferMaintenanceCancellation.Cancel();
            EnqueueDiagnostic(
                "Initial replay maintenance exceeded one second and was cancelled before capture.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // Cleanup is maintenance, not a condition for safe capture. Expected
            // filesystem failures are already contained inside the worker; an
            // unexpected fault is retained as a diagnostic and replay continues.
            EnqueueDiagnostic(
                $"Initial replay maintenance did not complete: {exception.GetBaseException().Message}");
        }

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
                var captureSessionIdentity =
                    Interlocked.Increment(
                        ref _captureSessionIdentity);
                lock (_discontinuousRefreshGate)
                {
                    // Only a start that owns the lifecycle gate and passed the
                    // already-running check establishes a new frame-pool
                    // identity. A duplicate/cancelled StartAsync must not make
                    // transition work for the live session stale.
                    _discontinuousRefreshCompletedEpoch =
                        _discontinuousRefreshRequestedEpoch;
                    _discontinuousRefreshSessionIdentity =
                        captureSessionIdentity;
                }

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
                selectedFfmpegPath = ffmpegPath;

                EnsureSafeBufferRoot();
                Directory.CreateDirectory(_bufferRoot);
                EnsureSafeBufferRoot();
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
                    BeginCaptureGenerationLocked(firstSegmentNumber: 0);
                }

                _sessionCancellation = new CancellationTokenSource();
                _reportedDroppedAudioBlocks = 0;
                ResetCaptureProgress();
                _captureStarvationWatchdog = new CaptureStarvationWatchdog(
                    configuration.FramesPerSecond);
                _captureRecoveryRequestGate.ResetForSession();
                InvalidateCaptureRecoveryRetry();
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
                        .SelectAsync(
                            ffmpegPath,
                            configuration,
                            cancellationToken,
                            selectedPerformanceProfile)
                        .ConfigureAwait(false)
                    : new FfmpegCapabilitySelection(
                        effectiveStrategyOverride,
                        sessionStrategyOverride is null
                            ? $"Capture smoke override selected {effectiveStrategyOverride.Description}."
                            : $"Capture recovery retained the verified {effectiveStrategyOverride.Description} path.");
                selectedCapability = capabilitySelection;
                capabilityWasProbed =
                    effectiveStrategyOverride is null &&
                    !sourceSafetyMode;
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
                _activeCapturePerformanceProfile =
                    selectedPerformanceProfile;
                _activeFfmpegPath = ffmpegPath;
                Volatile.Write(
                    ref _activeStrategyUsesCapabilityProbe,
                    capabilityWasProbed ? 1 : 0);
                ResetDegradedCaptureReprobe();
                if (capabilityWasProbed &&
                    capabilitySelection.Strategy.CaptureBackend ==
                    DesktopCaptureBackend.Gdi)
                {
                    ScheduleNextDegradedCaptureReprobe(
                        capabilitySelection.CacheExpiresAtUtc);
                }
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
                    _segmentDirectory,
                    performanceProfile: selectedPerformanceProfile);

                var captureProcess = CreateProcess(
                    ffmpegPath,
                    arguments,
                    redirectStandardInput: true,
                    redirectStandardOutput: true);
                var captureProcessStarted = false;
                try
                {
                    if (!captureProcess.Start())
                    {
                        throw new InvalidOperationException("Windows could not start the capture engine.");
                    }

                    captureProcessStarted = true;
                    // Attach immediately after Process.Start. If ClipForge is
                    // terminated before graceful cleanup, closing this job's
                    // last handle makes Windows terminate FFmpeg as well.
                    _captureProcessJob = CaptureProcessJob.Attach(captureProcess);
                }
                catch
                {
                    realCaptureLaunchFailed =
                        captureProcessStarted &&
                        HasProcessExitedSafely(captureProcess);
                    TryKill(captureProcess);
                    _captureProcessJob?.Dispose();
                    _captureProcessJob = null;
                    captureProcess.Dispose();
                    throw;
                }

                _captureProcess = captureProcess;
                if (!ProcessTuning.TryApplyCapturePriority(
                        captureProcess,
                        capabilitySelection.Strategy,
                        CaptureGeometry.ResolveOutputSize(
                            configuration.Display,
                            configuration.Resolution).RequiresScaling,
                        selectedPerformanceProfile))
                {
                    EnqueueDiagnostic(
                        "Windows did not allow ClipForge to apply the capture process priority policy.");
                }

                captureProcess.StandardInput.AutoFlush = true;
                _diagnosticTask = PumpDiagnosticsAsync(captureProcess);
                _captureProgressTask = PumpCaptureProgressAsync(
                    captureProcess,
                    configuration.Display);
                Volatile.Write(ref _isRunning, 1);

                if (_audioPipes.Count > 0)
                {
                    var connectionTasks = _audioPipes
                        .Select(pipe => pipe.ConnectAndStartAsync(_sessionCancellation.Token))
                        .ToArray();
                    try
                    {
                        await Task.WhenAll(connectionTasks)
                            .WaitAsync(TimeSpan.FromSeconds(15), cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch when (
                        !cancellationToken.IsCancellationRequested &&
                        HasProcessExitedSafely(captureProcess))
                    {
                        realCaptureLaunchFailed = true;
                        throw;
                    }
                }

                await Task.Delay(500, cancellationToken).ConfigureAwait(false);
                if (captureProcess.HasExited)
                {
                    realCaptureLaunchFailed = true;
                    throw new InvalidOperationException(BuildCaptureFailureMessage());
                }

                // gfxcapture creates its D3D11/WGC graph asynchronously after
                // Process.Start. Runtime validation observed the process back at
                // Normal GPU scheduling after that graph initialized even though
                // the early policy call succeeded. Reapply here so the observed
                // live capture contexts use the intended class.
                if (!ProcessTuning.TryApplyCapturePriority(
                        captureProcess,
                        capabilitySelection.Strategy,
                        CaptureGeometry.ResolveOutputSize(
                            configuration.Display,
                            configuration.Resolution).RequiresScaling,
                        selectedPerformanceProfile))
                {
                    EnqueueDiagnostic(
                        "Windows did not allow ClipForge to finalize the live capture priority policy.");
                }

                Volatile.Write(ref _isStopping, 0);
                _monitorTask = MonitorCaptureAsync(
                    captureProcess,
                    configuration,
                    capabilitySelection.Strategy,
                    _sessionCancellation.Token);
                RecordCaptureRuntimeEvent(
                    "capture_started",
                    captureProcess,
                    configuration,
                    capabilitySelection.Strategy,
                    $"Initial capture generation is active; profile={selectedPerformanceProfile}.");
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
                var confirmedProbedLaunchFailure =
                    capabilityWasProbed &&
                    realCaptureLaunchFailed;
                if (confirmedProbedLaunchFailure &&
                    selectedCapability is { } failedCapability &&
                    selectedFfmpegPath is { Length: > 0 } failedFfmpegPath)
                {
                    _ = _capabilityProbe.Invalidate(
                        failedFfmpegPath,
                        configuration,
                        failedCapability.Strategy,
                        selectedPerformanceProfile);
                }

                var retryWithFreshCapability =
                    ShouldRetryCaptureLaunch(
                        allowFreshCapabilityRetry,
                        capabilityWasProbed,
                        realCaptureLaunchFailed);

                try
                {
                    await StopCoreAsync(deleteBuffer: true, publishStopped: false).ConfigureAwait(false);
                }
                catch
                {
                    // Preserve the original startup failure.
                }

                if (retryWithFreshCapability)
                {
                    Publish(new ReplayStateSnapshot(
                        ReplayState.Starting,
                        TimeSpan.Zero,
                        _retention,
                        0,
                        "The verified capture path changed at launch; probing once more before giving up."));
                    throw new RetryableCaptureLaunchException(message, exception);
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
        CancellationToken cancellationToken,
        bool preserveCompletedSegments = true,
        FfmpegCapabilitySelection? verifiedReplacement = null,
        CapturePerformanceProfile? performanceProfileOverride = null,
        bool requireCompletedSegmentBoundary = false,
        int? expectedSessionIdentity = null,
        bool invalidateCapabilitySelectionBeforeRefresh = false,
        int recoveryRetryAttempt = 0,
        int boundaryRetryAttempt = 0)
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
                var replacementStrategy = verifiedReplacement?.Strategy ?? strategy;
                var replacementPerformanceProfile =
                    performanceProfileOverride ?? _activeCapturePerformanceProfile;
                var ffmpegPath = _activeFfmpegPath;
                var segmentDirectory = _segmentDirectory;
                if (!IsRunning ||
                    process is null ||
                    configuration is null ||
                    strategy is null ||
                    replacementStrategy is null ||
                    string.IsNullOrWhiteSpace(ffmpegPath) ||
                    string.IsNullOrWhiteSpace(segmentDirectory) ||
                    expectedProcessId is null ||
                    CaptureProcessId != expectedProcessId ||
                    expectedSessionIdentity is { } sessionIdentity &&
                    Volatile.Read(ref _captureSessionIdentity) !=
                        sessionIdentity)
                {
                    return false;
                }

                if (invalidateCapabilitySelectionBeforeRefresh &&
                    Volatile.Read(
                        ref _activeStrategyUsesCapabilityProbe) != 0)
                {
                    _capabilityProbe.InvalidateConfiguration(
                        ffmpegPath,
                        configuration,
                        _activeCapturePerformanceProfile);
                }

                var promotesDegradedCapture = verifiedReplacement is not null;
                if (promotesDegradedCapture
                        ? !CanPromoteDegradedCapture(
                            strategy,
                            replacementStrategy,
                            Volatile.Read(ref _activeStrategyUsesCapabilityProbe) != 0)
                        : !CanRefreshCaptureBackend(
                            strategy.CaptureBackend))
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
                    var recoveryRetryScheduled =
                        DeferCaptureRecoveryRetry(
                        process.Id,
                        preserveCompletedSegments,
                        replacementPerformanceProfile,
                        verifiedReplacement,
                        requireCompletedSegmentBoundary,
                        expectedSessionIdentity,
                        invalidateCapabilitySelectionBeforeRefresh,
                        checked(recoveryRetryAttempt + 1),
                        boundaryRetryAttempt);
                    if (!recoveryRetryScheduled &&
                        IsActiveCaptureGenerationExportBlocked())
                    {
                        await StopCoreAsync(
                                deleteBuffer: true,
                                publishStopped: false)
                            .ConfigureAwait(false);
                        Publish(new ReplayStateSnapshot(
                            ReplayState.Faulted,
                            TimeSpan.Zero,
                            _retention,
                            0,
                            "ClipForge stopped Instant Replay because the verified capture engine remained unavailable during bounded recovery.",
                            _lastSavedPath));
                    }

                    throw;
                }

                // Keep logical replay running while maintenance owns _saveGate.
                // A hotkey arriving now waits and saves the retained ring after
                // renewal instead of being rejected as though replay were off.
                Publish(_state with
                {
                    Message = promotesDegradedCapture
                        ? "Restoring Windows Graphics Capture while keeping your replay buffer..."
                        : "Refreshing the capture engine while keeping your replay buffer..."
                });

                var replacementCaptureLaunchFailed = false;
                try
                {
                    var boundaryWaitStarted = Stopwatch.GetTimestamp();
                    var reachedSegmentBoundary = await WaitForNextCaptureSegmentBoundaryAsync(
                            process,
                            cancellationToken)
                        .ConfigureAwait(false);
                    var boundaryWait = Stopwatch.GetElapsedTime(boundaryWaitStarted);
                    var replacementStarted = Stopwatch.GetTimestamp();

                    if (ShouldDeferDegradedCapturePromotion(
                            promotesDegradedCapture,
                            reachedSegmentBoundary))
                    {
                        EnqueueDiagnostic(
                            "Deferred optional WGC promotion because the healthy GDI recorder did not reach a completed segment boundary.");
                        RefreshBufferState();
                        return false;
                    }

                    if (ShouldDeferNonDestructiveCaptureRefresh(
                            requireCompletedSegmentBoundary,
                            reachedSegmentBoundary))
                    {
                        // Ambiguous content cadence can justify promoting the
                        // capture process, but it does not prove the existing
                        // replay generation is damaged. Do not turn that
                        // one-way tuning change into a destructive restart
                        // when the current writer misses the short boundary
                        // window. Keep recording and retry later.
                        DeferCaptureRecoveryRetry(
                            process.Id,
                            preserveCompletedSegments,
                            replacementPerformanceProfile,
                            verifiedReplacement,
                            requireCompletedSegmentBoundary,
                            expectedSessionIdentity,
                            invalidateCapabilitySelectionBeforeRefresh,
                            recoveryRetryAttempt,
                            checked(boundaryRetryAttempt + 1));
                        EnqueueDiagnostic(
                            "Deferred the non-destructive capture-profile promotion because no completed segment boundary was observed.");
                        _captureStarvationWatchdog =
                            new CaptureStarvationWatchdog(
                                configuration.FramesPerSecond);
                        RefreshBufferState();
                        return false;
                    }

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
                        var cleanupDetail = Volatile.Read(ref _lastCaptureCleanupFailure);
                        throw new InvalidOperationException(
                            "The previous capture resources did not finish bounded cleanup. " +
                            "ClipForge refused to start an overlapping recorder." +
                            (string.IsNullOrWhiteSpace(cleanupDetail)
                                ? string.Empty
                                : $" {cleanupDetail}"));
                    }

                    int segmentStartNumber;
                    var retainedCompletedSegments = ShouldRetainCompletedSegments(
                        preserveCompletedSegments,
                        reachedSegmentBoundary);
                    lock (_fileGate)
                    {
                        RefreshSegmentIndexLocked();
                        if (retainedCompletedSegments)
                        {
                            DiscardNewestCaptureTailLocked();
                        }
                        else
                        {
                            InvalidateRetainedCaptureGenerationLocked();
                        }

                        segmentStartNumber = _nextSegmentNumber;
                        BeginCaptureGenerationLocked(segmentStartNumber);
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    _sessionCancellation = new CancellationTokenSource();
                    _reportedDroppedAudioBlocks = 0;
                    ResetCaptureProgress();
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
                        replacementStrategy,
                        segmentDirectory,
                        segmentStartNumber,
                        replacementPerformanceProfile);
                    var replacement = CreateProcess(
                        verifiedFfmpegPath,
                        arguments,
                        redirectStandardInput: true,
                        redirectStandardOutput: true);
                    var replacementStartedProcess = false;
                    try
                    {
                        if (!replacement.Start())
                        {
                            throw new InvalidOperationException(
                                "Windows could not renew the capture engine.");
                        }

                        replacementStartedProcess = true;
                        _captureProcessJob = CaptureProcessJob.Attach(replacement);
                    }
                    catch
                    {
                        replacementCaptureLaunchFailed =
                            replacementStartedProcess &&
                            HasProcessExitedSafely(replacement);
                        TryKill(replacement);
                        _captureProcessJob?.Dispose();
                        _captureProcessJob = null;
                        replacement.Dispose();
                        throw;
                    }

                    _captureProcess = replacement;
                    if (!ProcessTuning.TryApplyCapturePriority(
                            replacement,
                            replacementStrategy,
                            CaptureGeometry.ResolveOutputSize(
                                configuration.Display,
                                configuration.Resolution).RequiresScaling,
                            replacementPerformanceProfile))
                    {
                        EnqueueDiagnostic(
                            "Windows did not allow ClipForge to reapply the capture process priority policy.");
                    }

                    replacement.StandardInput.AutoFlush = true;
                    _diagnosticTask = PumpDiagnosticsAsync(replacement);
                    _captureProgressTask = PumpCaptureProgressAsync(
                        replacement,
                        configuration.Display);
                    if (_audioPipes.Count > 0)
                    {
                        var connectionTasks = _audioPipes
                            .Select(pipe => pipe.ConnectAndStartAsync(_sessionCancellation.Token))
                            .ToArray();
                        try
                        {
                            await Task.WhenAll(connectionTasks)
                                .WaitAsync(TimeSpan.FromSeconds(15), cancellationToken)
                                .ConfigureAwait(false);
                        }
                        catch when (
                            !cancellationToken.IsCancellationRequested &&
                            HasProcessExitedSafely(replacement))
                        {
                            replacementCaptureLaunchFailed = true;
                            throw;
                        }
                    }

                    await Task.Delay(500, cancellationToken).ConfigureAwait(false);
                    if (replacement.HasExited)
                    {
                        replacementCaptureLaunchFailed = true;
                        throw new InvalidOperationException(BuildCaptureFailureMessage());
                    }

                    if (!ProcessTuning.TryApplyCapturePriority(
                            replacement,
                            replacementStrategy,
                            CaptureGeometry.ResolveOutputSize(
                                configuration.Display,
                                configuration.Resolution).RequiresScaling,
                            replacementPerformanceProfile))
                    {
                        EnqueueDiagnostic(
                            "Windows did not allow ClipForge to finalize the renewed capture priority policy.");
                    }

                    InvalidateCaptureRecoveryRetry();
                    Volatile.Write(ref _isStopping, 0);
                    Volatile.Write(ref _isRunning, 1);
                    _activeCaptureStrategy = replacementStrategy;
                    _activeCapturePerformanceProfile = replacementPerformanceProfile;
                    _activeEncoderDescription = replacementStrategy.Description;
                    Volatile.Write(
                        ref _lastCapturePlan,
                        new CaptureSessionPlan(
                            configuration.Display,
                            configuration.Resolution,
                            replacementStrategy));
                    ResetDegradedCaptureReprobe();
                    if (ShouldMaintainDegradedCaptureReprobe(
                            replacementStrategy.CaptureBackend,
                            Volatile.Read(
                                ref _activeStrategyUsesCapabilityProbe) != 0))
                    {
                        // A health/profile refresh must not silently disable the
                        // background opportunity to return from compatibility
                        // GDI to WGC.
                        ScheduleNextDegradedCaptureReprobe();
                    }

                    _monitorTask = MonitorCaptureAsync(
                        replacement,
                        configuration,
                        replacementStrategy,
                        _sessionCancellation.Token);
                    RecordCaptureRuntimeEvent(
                        promotesDegradedCapture
                            ? "capture_promoted"
                            : "capture_renewed",
                        replacement,
                        configuration,
                        replacementStrategy,
                        string.Create(
                            CultureInfo.InvariantCulture,
                            $"BoundaryAligned={reachedSegmentBoundary}; boundaryWaitMs={boundaryWait.TotalMilliseconds:0}; replacementMs={Stopwatch.GetElapsedTime(replacementStarted).TotalMilliseconds:0}; profile={replacementPerformanceProfile}."));
                    EnqueueDiagnostic(
                        string.Create(
                            CultureInfo.InvariantCulture,
                            $"{(promotesDegradedCapture
                                ? "Promoted capture from GDI to verified WGC"
                                : $"Renewed the {replacementStrategy.CaptureBackend} capture process")} at segment {segmentStartNumber}; " +
                            $"{(retainedCompletedSegments
                                ? "completed replay segments were retained. "
                                : "the previous capture generation was invalidated and is rebuffering. ")}" +
                            $"BoundaryAligned={reachedSegmentBoundary}; " +
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
                    if (replacementCaptureLaunchFailed &&
                        Volatile.Read(
                            ref _activeStrategyUsesCapabilityProbe) != 0)
                    {
                        _capabilityProbe.Invalidate(
                            ffmpegPath,
                            configuration,
                            strategy,
                            _activeCapturePerformanceProfile);
                        _capabilityProbe.Invalidate(
                            ffmpegPath,
                            configuration,
                            replacementStrategy,
                            replacementPerformanceProfile);
                    }

                    Volatile.Write(ref _isStopping, 1);
                    var failedReplacementCleaned =
                        await StopCaptureResourcesForRefreshAsync().ConfigureAwait(false);
                    if (promotesDegradedCapture &&
                        failedReplacementCleaned &&
                        await TryRestoreDegradedCaptureAfterPromotionFailureAsync(
                                verifiedFfmpegPath,
                                configuration,
                                strategy,
                                segmentDirectory,
                                replacementPerformanceProfile,
                                cancellationToken,
                                exception)
                            .ConfigureAwait(false))
                    {
                        return false;
                    }

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

        ReplayStateSnapshot snapshot;
        lock (_fileGate)
        {
            _retention = retention;
            snapshot = BuildRetentionUpdateSnapshot(
                _state,
                retention,
                IsRunning,
                _activeEncoderDescription);
        }

        // Pruning can require deleting almost 1,800 files after a 1h -> 30s
        // change. Publish the clamped state in O(1) and let MonitorCaptureAsync
        // perform bounded deletion batches away from the WPF event handler.
        Publish(snapshot);
    }

    internal static ReplayStateSnapshot BuildRetentionUpdateSnapshot(
        ReplayStateSnapshot current,
        TimeSpan retention,
        bool isRunning,
        string? encoderDescription)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (retention < TimeSpan.FromSeconds(FfmpegArgumentBuilder.SegmentSeconds) ||
            retention > TimeSpan.FromHours(1))
        {
            throw new ArgumentOutOfRangeException(nameof(retention));
        }

        var available = current.AvailableDuration > retention
            ? retention
            : current.AvailableDuration;
        var state = current.State;
        if (isRunning && state is ReplayState.Buffering or ReplayState.Ready)
        {
            state = available >= retention
                ? ReplayState.Ready
                : ReplayState.Buffering;
        }

        var engine = string.IsNullOrWhiteSpace(encoderDescription)
            ? "the capture engine"
            : encoderDescription;
        var message = state switch
        {
            ReplayState.Ready when isRunning =>
                $"Instant Replay is ready using {engine}.",
            ReplayState.Buffering when isRunning =>
                $"Instant Replay is filling its buffer using {engine}.",
            _ => current.Message
        };
        return current with
        {
            State = state,
            AvailableDuration = available,
            Retention = retention,
            Message = message
        };
    }

    /// <summary>
    /// Acknowledges that MainWindow reached the bounded health-recovery limit.
    /// The UI immediately follows this acknowledgement with an orderly stop so
    /// a blocked generation can never remain presented as a usable replay.
    /// </summary>
    internal bool SuppressCaptureFaultRecovery(CaptureRecoveryRequestedEventArgs request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!IsRunning ||
            request.ProcessId is null ||
            CaptureProcessId != request.ProcessId)
        {
            return false;
        }

        if (!_captureRecoveryRequestGate.SuppressFaults(request.RequestId))
        {
            return false;
        }

        EnqueueDiagnostic(
            "Automatic fault recovery reached its per-session limit; the current request was suppressed pending an orderly replay stop.");
        return true;
    }

    internal void CompleteCaptureRecoveryRequest(CaptureRecoveryRequestedEventArgs request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_captureRecoveryRequestGate.Complete(
                request.RequestId,
                out var scheduledRefreshQueued))
        {
            DispatchPendingScheduledRefresh(scheduledRefreshQueued);
        }
    }

    internal bool RequestScheduledCaptureRefresh(string diagnostic)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(diagnostic);
        return QueueScheduledCaptureRefresh(CaptureProcessId, diagnostic);
    }

    internal bool RequestDiscontinuousCaptureRefresh(string diagnostic)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(diagnostic);
        if (!TryInvalidateCaptureGenerationForDisplayTransition(
                diagnostic,
                out var processId,
                out var sessionIdentity))
        {
            return false;
        }

        long requestedEpoch;
        lock (_discontinuousRefreshGate)
        {
            requestedEpoch = ++_discontinuousRefreshRequestedEpoch;
            _discontinuousRefreshSessionIdentity = sessionIdentity;
            _discontinuousRefreshDiagnostic = diagnostic;
        }

        var queued = _discontinuousCaptureRefreshCoordinator.TrySchedule(
            processId,
            diagnostic);
        if (ShouldScheduleDiscontinuousRefreshContinuation(queued))
        {
            SchedulePendingDiscontinuousRefresh();
            return true;
        }

        if (queued)
        {
            EnqueueDiagnostic(
                $"Queued a discontinuous capture renewal for process {processId}: {diagnostic}");
            RecordCaptureRuntimeEvent(
                "capture_display_transition_queued",
                _captureProcess,
                detail:
                    $"epoch={requestedEpoch}; session={sessionIdentity}; {diagnostic}");
        }

        return queued;
    }

    /// <summary>
    /// Immediately makes media from the pre-transition capture generation
    /// unexportable. Windows display/resume notifications arrive before display
    /// enumeration stabilizes, so this safety action must not wait for the UI's
    /// debounce or for a save that already owns <see cref="_saveGate"/>.
    /// </summary>
    internal bool InvalidateCaptureGenerationForDisplayTransition(
        string diagnostic)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(diagnostic);
        return TryInvalidateCaptureGenerationForDisplayTransition(
            diagnostic,
            out _,
            out _);
    }

    private bool TryInvalidateCaptureGenerationForDisplayTransition(
        string diagnostic,
        out int processId,
        out int sessionIdentity)
    {
        processId = 0;
        sessionIdentity = 0;
        CancellationTokenSource? invalidatedSave = null;
        var newlyBlocked = false;
        var blockedGeneration = -1;

        lock (_fileGate)
        {
            var currentProcessId = CaptureProcessId;
            var captureBackend = _activeCaptureStrategy?.CaptureBackend;
            if (currentProcessId is not { } currentPid ||
                currentPid <= 0 ||
                Volatile.Read(ref _disposed) != 0 ||
                Volatile.Read(ref _isStopping) != 0 ||
                !IsRunning ||
                captureBackend is not { } backend ||
                !CanRefreshCaptureBackend(backend))
            {
                return false;
            }

            processId = currentPid;
            sessionIdentity =
                Volatile.Read(ref _captureSessionIdentity);
            blockedGeneration = _activeCaptureGeneration;
            newlyBlocked =
                _exportBlockedCaptureGeneration != blockedGeneration;
            _exportBlockedCaptureGeneration = blockedGeneration;
            if (_activeSaveCaptureGeneration == blockedGeneration)
            {
                invalidatedSave = _activeSaveInvalidation;
            }
        }

        try
        {
            // A save can otherwise keep _saveGate through the display debounce
            // and publish held-frame/audio media after the transition was known.
            invalidatedSave?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Commit revalidates under _fileGate, so a save that completed
            // between the snapshot and cancellation still cannot publish.
        }

        RefreshBufferState();
        if (newlyBlocked)
        {
            EnqueueDiagnostic(
                $"Blocked capture generation {blockedGeneration} immediately after a display/graphics transition: {diagnostic}");
            RecordCaptureRuntimeEvent(
                "capture_display_transition_invalidated",
                _captureProcess,
                detail:
                    $"generation={blockedGeneration}; session={sessionIdentity}; {diagnostic}");
        }

        return true;
    }

    internal async Task WaitForScheduledCaptureRefreshIdleAsync()
    {
        await Task.WhenAll(
                _scheduledCaptureRefreshCoordinator.WaitForIdleAsync(),
                WaitForDiscontinuousCaptureRefreshIdleAsync(),
                _degradedCaptureReprobeCoordinator.WaitForIdleAsync())
            .ConfigureAwait(false);
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

        if (Interlocked.CompareExchange(ref _saveOperationPending, 1, 0) != 0)
        {
            throw new InvalidOperationException(
                "A clip is already being saved. ClipForge ignored the duplicate request.");
        }

        var enteredSaveGate = false;
        IReadOnlyList<string> selectedSegments = [];
        string? manifestPath = null;
        string? partialPath = null;
        var saveStartedAt = Stopwatch.GetTimestamp();
        var saveJournalStarted = false;
        var selectedFirstSegmentNumber = -1;
        var selectedLastSegmentNumber = -1;
        var selectedGeneration = -1;
        CancellationTokenSource? saveCancellation = null;

        try
        {
            // Always leave the caller before any path can reach the synchronous
            // segment snapshot (including cancellation while waiting for the
            // save gate).
            await SwitchToThreadPool();
            await _saveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            enteredSaveGate = true;
            saveCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var saveToken = saveCancellation.Token;
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
                var selectedSegmentSet = selectedSegments.ToHashSet(
                    StringComparer.OrdinalIgnoreCase);
                var selectedEntries = _segments
                    .Where(segment => selectedSegmentSet.Contains(
                        segment.Path))
                    .ToArray();
                if (selectedEntries.Length > 0)
                {
                    selectedFirstSegmentNumber =
                        selectedEntries[0].SegmentNumber;
                    selectedLastSegmentNumber =
                        selectedEntries[^1].SegmentNumber;
                    selectedGeneration =
                        selectedEntries[^1].GenerationId;
                }

                foreach (var path in selectedSegments)
                {
                    _protectedSegments.Add(path);
                }

                _activeSaveCaptureGeneration = selectedGeneration;
                _activeSaveInvalidation = saveCancellation;
                actualDuration = TimeSpan.FromSeconds(Math.Min(
                    requestedDuration.TotalSeconds,
                    selectedCount * FfmpegArgumentBuilder.SegmentSeconds));
            }

            saveJournalStarted = true;
            RecordCaptureRuntimeEvent(
                "clip_save_started",
                detail: string.Create(
                    CultureInfo.InvariantCulture,
                    $"requestedSeconds={requestedDuration.TotalSeconds:0.###}; " +
                    $"actualSeconds={actualDuration.TotalSeconds:0.###}; " +
                    $"segments={selectedSegments.Count}; " +
                    $"firstSegment={selectedFirstSegmentNumber}; " +
                    $"lastSegment={selectedLastSegmentNumber}; " +
                    $"generation={selectedGeneration}."),
                includeResourceSnapshots: false);
            Volatile.Write(ref _isSaving, 1);
            Publish(_state with
            {
                State = ReplayState.Saving,
                Message = "Saving your clip…"
            });

            var ffmpegPath = _ffmpegSetupService.FindExecutable()
                ?? throw new InvalidOperationException("The capture engine is no longer available.");
            var ffprobePath = _ffmpegSetupService.FindProbeExecutable()
                ?? throw new InvalidOperationException("The clip validator is no longer available.");
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
                    saveToken)
                .ConfigureAwait(false);

            var selectedDuration = TimeSpan.FromSeconds(
                selectedSegments.Count * FfmpegArgumentBuilder.SegmentSeconds);
            var trimFromStart = selectedDuration - actualDuration;
            var arguments = FfmpegArgumentBuilder.BuildConcatArguments(
                manifestPath,
                partialPath,
                trimFromStart,
                actualDuration);
            await RunExportProcessAsync(ffmpegPath, arguments, saveToken)
                .ConfigureAwait(false);

            if (!File.Exists(partialPath) || new FileInfo(partialPath).Length == 0)
            {
                throw new InvalidDataException("The capture engine produced an empty clip.");
            }

            var expectedFramesPerSecond = _activeConfiguration?.FramesPerSecond ?? 60;
            var expectedAudio = _activeConfiguration is
            {
                CaptureSystemAudio: true
            } or
            {
                CaptureMicrophone: true
            };
            await ValidateExportAsync(
                    ffprobePath,
                    partialPath,
                    actualDuration,
                    expectedFramesPerSecond,
                    expectedAudio,
                    saveToken)
                .ConfigureAwait(false);

            lock (_fileGate)
            {
                if (!IsCaptureGenerationExportable(
                        selectedGeneration,
                        _exportBlockedCaptureGeneration))
                {
                    throw new InvalidDataException(
                        "ClipForge stopped this save because capture pacing became unstable. " +
                        "Wait for the replay buffer to refill, then clip again.");
                }

                // Commit while holding the same gate used to invalidate a
                // generation. A fault observed before this point therefore
                // wins the race and can never publish a known-bad clip.
                File.Move(partialPath, finalPath);
            }

            partialPath = null;
            _lastSavedPath = finalPath;
            RecordCaptureRuntimeEvent(
                "clip_save_completed",
                detail: string.Create(
                    CultureInfo.InvariantCulture,
                    $"elapsedMs={Stopwatch.GetElapsedTime(saveStartedAt).TotalMilliseconds:0}; " +
                    $"segments={selectedSegments.Count}; generation={selectedGeneration}."),
                includeResourceSnapshots: false);
            return finalPath;
        }
        catch (Exception exception)
        {
            var reportedException =
                exception is OperationCanceledException &&
                !cancellationToken.IsCancellationRequested &&
                IsExportGenerationBlocked(selectedGeneration)
                    ? new InvalidDataException(
                        "ClipForge stopped this save because capture pacing became unstable. " +
                        "Wait for the replay buffer to refill, then clip again.",
                        exception)
                    : exception;
            if (saveJournalStarted)
            {
                RecordCaptureRuntimeEvent(
                    "clip_save_failed",
                    detail: string.Create(
                        CultureInfo.InvariantCulture,
                        $"elapsedMs={Stopwatch.GetElapsedTime(saveStartedAt).TotalMilliseconds:0}; " +
                        $"segments={selectedSegments.Count}; generation={selectedGeneration}; " +
                        $"error={reportedException.GetType().Name}."),
                    includeResourceSnapshots: false);
            }

            if (!ReferenceEquals(reportedException, exception))
            {
                throw reportedException;
            }

            throw;
        }
        finally
        {
            Volatile.Write(ref _isSaving, 0);
            lock (_fileGate)
            {
                if (ReferenceEquals(_activeSaveInvalidation, saveCancellation))
                {
                    _activeSaveInvalidation = null;
                    _activeSaveCaptureGeneration = -1;
                }

                foreach (var path in selectedSegments)
                {
                    _protectedSegments.Remove(path);
                }
            }

            TryDeleteFile(manifestPath);
            TryDeleteFile(partialPath);
            saveCancellation?.Dispose();
            if (enteredSaveGate)
            {
                _saveGate.Release();
            }

            Volatile.Write(ref _saveOperationPending, 0);
            RefreshBufferStateAfterSave(_lastSavedPath);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.CompareExchange(ref _disposeStarted, 1, 0) != 0)
        {
            await _disposeCompletion.Task.ConfigureAwait(false);
            return;
        }

        // Reject new public work immediately; the owner below still uses the
        // internal cleanup path. Concurrent DisposeAsync callers await the same
        // completion instead of racing semaphore disposal.
        Volatile.Write(ref _disposed, 1);
        _initialBufferMaintenanceCancellation.Cancel();
        _captureRecoveryRetryCancellation.Cancel();
        try
        {
            // Stop accepting maintenance before waiting for the lifecycle gates.
            // This cancels a coordinator that is queued behind a long save and lets
            // an in-flight refresh perform its normal bounded capture cleanup.
            await Task.WhenAll(
                    _scheduledCaptureRefreshCoordinator.DisposeAsync().AsTask(),
                    _discontinuousCaptureRefreshCoordinator.DisposeAsync().AsTask(),
                    _degradedCaptureReprobeCoordinator.DisposeAsync().AsTask())
                .ConfigureAwait(false);

            await _saveGate.WaitAsync().ConfigureAwait(false);
            try
            {
                await _lifecycleGate.WaitAsync().ConfigureAwait(false);
                try
                {
                    await StopCoreAsync(deleteBuffer: true, publishStopped: false).ConfigureAwait(false);
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

            while (true)
            {
                var recoveryRetry =
                    Volatile.Read(ref _captureRecoveryRetryState);
                await recoveryRetry.Task.ConfigureAwait(false);
                if (ReferenceEquals(
                        recoveryRetry,
                        Volatile.Read(ref _captureRecoveryRetryState)))
                {
                    break;
                }
            }

            _lifecycleGate.Dispose();
            _saveGate.Dispose();
            await _runtimeJournal.DisposeAsync().ConfigureAwait(false);
            _disposeCompletion.TrySetResult();
        }
        catch (Exception exception)
        {
            _disposeCompletion.TrySetException(exception);
            throw;
        }
        finally
        {
            _captureRecoveryRetryCancellation.Dispose();
            DisposeInitialBufferMaintenanceCancellationWhenSafe();
        }
    }

    private void DisposeInitialBufferMaintenanceCancellationWhenSafe()
    {
        if (_initialBufferMaintenanceTask.IsCompleted)
        {
            _initialBufferMaintenanceCancellation.Dispose();
            return;
        }

        _ = _initialBufferMaintenanceTask.ContinueWith(
            static (task, state) =>
            {
                _ = task.Exception;
                ((CancellationTokenSource)state!).Dispose();
            },
            _initialBufferMaintenanceCancellation,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
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
        Volatile.Write(ref _lastCaptureCleanupFailure, null);
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
                    // observed. Give FFmpeg a short, bounded opportunity to run
                    // gfxcapture's orderly WGC teardown (remove frame handlers,
                    // close the capture session/frame pool, and release D3D/COM
                    // resources). Local WGC/NVENC probes complete this path in
                    // roughly 0.2 seconds. StopProcessGracefullyAsync still kills
                    // the process after the bound, and CaptureProcessJob disposal
                    // remains the final containment guarantee.
                    await StopProcessGracefullyAsync(
                            process,
                            CaptureRefreshGracefulStopTimeout)
                        .ConfigureAwait(false);
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
                const string diagnostic =
                    "The previous capture process did not confirm exit after job containment closed.";
                Volatile.Write(ref _lastCaptureCleanupFailure, diagnostic);
                EnqueueDiagnostic(diagnostic);
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
                var diagnostic =
                    $"An audio input reported a cleanup error during renewal: {exception.GetBaseException().Message}";
                Volatile.Write(ref _lastCaptureCleanupFailure, diagnostic);
                EnqueueDiagnostic(diagnostic);
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
        ResetCaptureProgress();
        _captureStarvationWatchdog = null;
        return true;
    }

    private async Task<bool> TryRestoreDegradedCaptureAfterPromotionFailureAsync(
        string ffmpegPath,
        CaptureConfiguration configuration,
        VideoEncodingStrategy degradedStrategy,
        string segmentDirectory,
        CapturePerformanceProfile performanceProfile,
        CancellationToken cancellationToken,
        Exception promotionFailure)
    {
        Process? restoredProcess = null;
        try
        {
            if (degradedStrategy.CaptureBackend != DesktopCaptureBackend.Gdi ||
                !IsSafeActiveBufferDirectory(segmentDirectory))
            {
                return false;
            }

            int segmentStartNumber;
            lock (_fileGate)
            {
                RefreshSegmentIndexLocked();
                var failedGeneration = _activeCaptureGeneration;
                for (var index = _segments.Count - 1; index >= 0; index--)
                {
                    var segment = _segments[index];
                    if (segment.GenerationId != failedGeneration)
                    {
                        continue;
                    }

                    segment = segment with { IsTrusted = false };
                    if (!_protectedSegments.Contains(segment.Path) &&
                        TryDeleteFile(segment.Path))
                    {
                        _bufferBytes = Math.Max(0, _bufferBytes - segment.Length);
                        _segments.RemoveAt(index);
                    }
                    else
                    {
                        _segments[index] = segment;
                    }
                }

                _nextSegmentNumber = _segments.Count == 0
                    ? 0
                    : checked(_segments.Max(segment => segment.SegmentNumber) + 1);
                segmentStartNumber = _nextSegmentNumber;
                BeginCaptureGenerationLocked(segmentStartNumber);
            }

            cancellationToken.ThrowIfCancellationRequested();
            _sessionCancellation = new CancellationTokenSource();
            _reportedDroppedAudioBlocks = 0;
            ResetCaptureProgress();
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
                degradedStrategy,
                segmentDirectory,
                segmentStartNumber,
                performanceProfile);
            restoredProcess = CreateProcess(
                ffmpegPath,
                arguments,
                redirectStandardInput: true,
                redirectStandardOutput: true);
            if (!restoredProcess.Start())
            {
                throw new InvalidOperationException(
                    "Windows could not restore the previous GDI capture path.");
            }

            _captureProcessJob = CaptureProcessJob.Attach(restoredProcess);
            _captureProcess = restoredProcess;
            _ = ProcessTuning.TryApplyCapturePriority(
                restoredProcess,
                degradedStrategy,
                CaptureGeometry.ResolveOutputSize(
                    configuration.Display,
                    configuration.Resolution).RequiresScaling,
                performanceProfile);
            restoredProcess.StandardInput.AutoFlush = true;
            _diagnosticTask = PumpDiagnosticsAsync(restoredProcess);
            _captureProgressTask = PumpCaptureProgressAsync(
                restoredProcess,
                configuration.Display);
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
            if (restoredProcess.HasExited)
            {
                throw new InvalidOperationException(BuildCaptureFailureMessage());
            }

            _ = ProcessTuning.TryApplyCapturePriority(
                restoredProcess,
                degradedStrategy,
                CaptureGeometry.ResolveOutputSize(
                    configuration.Display,
                    configuration.Resolution).RequiresScaling,
                performanceProfile);
            _activeCaptureStrategy = degradedStrategy;
            _activeCapturePerformanceProfile = performanceProfile;
            _activeEncoderDescription = degradedStrategy.Description;
            Volatile.Write(ref _isStopping, 0);
            Volatile.Write(ref _isRunning, 1);
            Volatile.Write(
                ref _lastCapturePlan,
                new CaptureSessionPlan(
                    configuration.Display,
                    configuration.Resolution,
                    degradedStrategy));
            ScheduleNextDegradedCaptureReprobe();
            _monitorTask = MonitorCaptureAsync(
                restoredProcess,
                configuration,
                degradedStrategy,
                _sessionCancellation.Token);
            RecordCaptureRuntimeEvent(
                "capture_promotion_rolled_back",
                restoredProcess,
                configuration,
                degradedStrategy,
                $"The optional WGC promotion failed and verified GDI was restored. {promotionFailure.GetBaseException().Message}");
            EnqueueDiagnostic(
                "The optional WGC promotion failed; ClipForge restored the verified GDI capture path and kept completed replay segments.");
            RefreshBufferState();
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (restoredProcess is not null)
            {
                TryKill(restoredProcess);
            }

            _ = await StopCaptureResourcesForRefreshAsync().ConfigureAwait(false);
            Volatile.Write(ref _isRunning, 0);
            Volatile.Write(ref _isStopping, 0);
            throw;
        }
        catch (Exception restoreFailure)
        {
            if (restoredProcess is not null)
            {
                TryKill(restoredProcess);
            }

            _ = await StopCaptureResourcesForRefreshAsync().ConfigureAwait(false);
            EnqueueDiagnostic(
                $"The optional WGC promotion failed and GDI restoration also failed: {restoreFailure.GetBaseException().Message}");
            return false;
        }
        finally
        {
            if (restoredProcess is not null &&
                !ReferenceEquals(_captureProcess, restoredProcess))
            {
                restoredProcess.Dispose();
            }
        }
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
            var diagnostic =
                $"The previous capture {componentName} did not finish within the bounded cleanup window.";
            Volatile.Write(ref _lastCaptureCleanupFailure, diagnostic);
            EnqueueDiagnostic(diagnostic);
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
        var tail = _segments[tailIndex] with { IsTrusted = false };
        _segments[tailIndex] = tail;
        if (_protectedSegments.Contains(tail.Path) || !TryDeleteFile(tail.Path))
        {
            // Never overwrite an uncertain file. If deletion is unavailable,
            // the replacement starts at the next number. The short tail stays
            // indexed only for later cleanup and can never enter an export.
            return;
        }

        _bufferBytes = Math.Max(0, _bufferBytes - tail.Length);
        _segments.RemoveAt(tailIndex);
        _nextSegmentNumber = Math.Max(0, _nextSegmentNumber - 1);
    }

    private async Task StopCoreAsync(bool deleteBuffer, bool publishStopped)
    {
        Volatile.Write(ref _isStopping, 1);
        InvalidateCaptureRecoveryRetry();
        if (_degradedCaptureReprobeCoordinator.IsActive &&
            _activeConfiguration is { } reprobeConfiguration &&
            _activeFfmpegPath is { Length: > 0 } reprobeFfmpegPath)
        {
            // A session stop can race a long real GDI-to-WGC probe. Remove any
            // result already committed for that exact graph and advance its
            // cache revision so a cancellation-ignoring stale flight cannot
            // repopulate it after the next session begins.
            _capabilityProbe.InvalidateConfiguration(
                reprobeFfmpegPath,
                reprobeConfiguration,
                _activeCapturePerformanceProfile);
        }

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
        if (hadSession)
        {
            RecordCaptureRuntimeEvent(
                "capture_stopping",
                _captureProcess,
                detail: publishStopped ? "Manual or application stop." : "Internal cleanup.");
        }

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
        _activeCapturePerformanceProfile = CapturePerformanceProfile.LowImpact;
        _activeFfmpegPath = null;
        Volatile.Write(ref _activeStrategyUsesCapabilityProbe, 0);
        ResetDegradedCaptureReprobe();
        if (hadSession)
        {
            RecordCaptureRuntimeEvent(
                "capture_stopped",
                detail: "Capture resources released.");
        }

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
        var lastRuntimeSample = captureStarted;
        var lastCadenceJournalSample = captureStarted;
        var lastPriorityRefresh = captureStarted;
        var lastCaptureActivity = captureStarted;
        long lastProgressFrame = -1;
        long lastProgressOutputTime = -1;
        long lastEvaluatedProgressTimestamp = -1;
        CaptureProgressSample? previousCadenceIntervalProgress = null;
        CaptureProgressSample? previousCadenceJournalProgress = null;
        var worstCadenceIntervalDuplicateRatio = 0d;
        var slowestCadenceIntervalOutputSpeed = double.PositiveInfinity;
        var latestForegroundContext =
            new CaptureForegroundContext(false, false);
        var lastSegmentNumber = -1;
        long lastBufferBytes = -1;
        var outputRequiresScaling = CaptureGeometry.ResolveOutputSize(
            configuration.Display,
            configuration.Resolution).RequiresScaling;

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
                    !_captureRecoveryRequestGate.IsPending)
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
                var degradedReprobeDeadline = Volatile.Read(
                    ref _degradedCaptureReprobeNotBeforeUtcTicks);
                if (ShouldRunDegradedCaptureReprobe(
                        strategy.CaptureBackend,
                        Volatile.Read(ref _activeStrategyUsesCapabilityProbe) != 0,
                        degradedReprobeDeadline,
                        DateTimeOffset.UtcNow.UtcDateTime.Ticks,
                        latestForegroundContext))
                {
                    _ = QueueDegradedCaptureReprobe(
                        process.Id,
                        "The active degraded-capability cache entry reached its retry deadline.");
                }

                if (Stopwatch.GetElapsedTime(lastPriorityRefresh) >=
                    CapturePriorityRefreshInterval)
                {
                    lastPriorityRefresh = Stopwatch.GetTimestamp();
                    if (!ProcessTuning.TryEnsureCapturePriority(
                            process,
                            strategy,
                            CaptureGeometry.ResolveOutputSize(
                                configuration.Display,
                                configuration.Resolution).RequiresScaling,
                            _activeCapturePerformanceProfile))
                    {
                        if (Interlocked.Exchange(
                                ref _capturePriorityRefreshWarningReported,
                                1) == 0)
                        {
                            EnqueueDiagnostic(
                                "Windows did not allow ClipForge to refresh the live capture priority policy.");
                        }
                    }
                    else
                    {
                        Volatile.Write(ref _capturePriorityRefreshWarningReported, 0);
                    }
                }

                if (Stopwatch.GetElapsedTime(lastRuntimeSample) >= TimeSpan.FromMinutes(5))
                {
                    lastRuntimeSample = Stopwatch.GetTimestamp();
                    RecordCaptureRuntimeEvent(
                        "capture_runtime_sample",
                        process,
                        configuration,
                        strategy,
                        string.Create(
                            CultureInfo.InvariantCulture,
                            $"uptimeMinutes={Stopwatch.GetElapsedTime(captureStarted).TotalMinutes:0.0}."));
                }

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
                    _ = QueueScheduledCaptureRefresh(
                        process.Id,
                        string.Create(
                            CultureInfo.InvariantCulture,
                            $"The WGC capture process reached its bounded {CaptureProcessMaximumAge.TotalMinutes:0}-minute lifetime; renewing it prevents long-session frame-pool degradation."));
                }

                if (strategy.CaptureBackend is
                        DesktopCaptureBackend.WindowsGraphicsCapture or
                        DesktopCaptureBackend.Gdi &&
                    progress is not null)
                {
                    CaptureStarvationAssessment? assessment = null;
                    CaptureForegroundContext? assessmentForegroundContext = null;
                    while (_captureProgressSamples.TryDequeue(out var envelope))
                    {
                        if (envelope.ProcessId != process.Id ||
                            envelope.Sample.Timestamp <= lastEvaluatedProgressTimestamp)
                        {
                            continue;
                        }

                        lastEvaluatedProgressTimestamp = envelope.Sample.Timestamp;
                        latestForegroundContext = envelope.ForegroundContext;
                        if (previousCadenceIntervalProgress is { } intervalPrevious)
                        {
                            var interval = Stopwatch.GetElapsedTime(
                                intervalPrevious.Timestamp,
                                envelope.Sample.Timestamp);
                            var intervalFrames =
                                envelope.Sample.Frame - intervalPrevious.Frame;
                            var intervalDuplicates =
                                envelope.Sample.DuplicatedFrames -
                                intervalPrevious.DuplicatedFrames;
                            var intervalOutputTime =
                                envelope.Sample.OutputTimeMicroseconds -
                                intervalPrevious.OutputTimeMicroseconds;
                            if (interval > TimeSpan.Zero &&
                                intervalFrames > 0 &&
                                intervalDuplicates >= 0 &&
                                intervalOutputTime >= 0)
                            {
                                worstCadenceIntervalDuplicateRatio = Math.Max(
                                    worstCadenceIntervalDuplicateRatio,
                                    intervalDuplicates /
                                    (double)intervalFrames);
                                slowestCadenceIntervalOutputSpeed = Math.Min(
                                    slowestCadenceIntervalOutputSpeed,
                                    intervalOutputTime /
                                    1_000_000d /
                                    interval.TotalSeconds);
                            }
                        }

                        previousCadenceIntervalProgress = envelope.Sample;
                        var isWindowsGraphicsCapture =
                            strategy.CaptureBackend ==
                            DesktopCaptureBackend.WindowsGraphicsCapture;
                        var isInitialNativeLowImpactProfile =
                            isWindowsGraphicsCapture &&
                            !outputRequiresScaling &&
                            _activeCapturePerformanceProfile ==
                                CapturePerformanceProfile.LowImpact;
                        var deferredRecoveryIsPending =
                            ReadCurrentCaptureRecoveryRetryNotBefore() >
                            Stopwatch.GetTimestamp();
                        var suppressNonObjectiveSourceCadence =
                            ShouldSuppressNonObjectiveCaptureCadence(
                                envelope.ForegroundContext
                                    .UsedCustomFullscreenFallback,
                                outputRequiresScaling,
                                _activeCapturePerformanceProfile,
                                deferredRecoveryIsPending);
                        var allowInitialProfileCadence =
                            isInitialNativeLowImpactProfile &&
                            !suppressNonObjectiveSourceCadence;
                        var observedAssessment =
                            _captureStarvationWatchdog?.Observe(
                                envelope.Sample,
                                envelope.ForegroundContext,
                                captureProcessUptime,
                                allowSchedulingPressure:
                                    allowInitialProfileCadence,
                                allowOutputThroughput: true,
                                allowChronicLowCadence:
                                    allowInitialProfileCadence,
                                allowSourceCadence:
                                    isWindowsGraphicsCapture &&
                                    !suppressNonObjectiveSourceCadence);
                        if (assessment is null &&
                            observedAssessment is not null)
                        {
                            assessmentForegroundContext =
                                envelope.ForegroundContext;
                        }

                        assessment = RetainFirstCaptureAssessment(
                            assessment,
                            observedAssessment);
                    }

                    previousCadenceJournalProgress ??= progress;
                    if (Stopwatch.GetElapsedTime(lastCadenceJournalSample) >=
                        TimeSpan.FromMinutes(1))
                    {
                        lastCadenceJournalSample = Stopwatch.GetTimestamp();
                        var previous = previousCadenceJournalProgress;
                        previousCadenceJournalProgress = progress;
                        if (previous is not null &&
                            progress.Frame >= previous.Frame &&
                            progress.DuplicatedFrames >= previous.DuplicatedFrames &&
                            progress.OutputTimeMicroseconds >=
                                previous.OutputTimeMicroseconds)
                        {
                            var sampleWindow = Stopwatch.GetElapsedTime(
                                previous.Timestamp,
                                progress.Timestamp);
                            var frameDelta = progress.Frame - previous.Frame;
                            var duplicateDelta =
                                progress.DuplicatedFrames - previous.DuplicatedFrames;
                            var uniqueFps = sampleWindow > TimeSpan.Zero
                                ? Math.Max(0, frameDelta - duplicateDelta) /
                                  sampleWindow.TotalSeconds
                                : 0;
                            var duplicateRatio = frameDelta > 0
                                ? duplicateDelta / (double)frameDelta
                                : 0;
                            var outputSpeed = sampleWindow > TimeSpan.Zero
                                ? (progress.OutputTimeMicroseconds -
                                   previous.OutputTimeMicroseconds) /
                                  1_000_000d / sampleWindow.TotalSeconds
                                : 0;
                            RecordCaptureRuntimeEvent(
                                "capture_cadence_sample",
                                process,
                                configuration,
                                strategy,
                                string.Create(
                                    CultureInfo.InvariantCulture,
                                    $"windowSeconds={sampleWindow.TotalSeconds:0.0}; " +
                                    $"uniqueFps={uniqueFps:0.0}; duplicateRatio={duplicateRatio:0.000}; " +
                                    $"outputSpeed={outputSpeed:0.000}; dropped={progress.DroppedFrames - previous.DroppedFrames}; " +
                                    $"worstIntervalDuplicateRatio={worstCadenceIntervalDuplicateRatio:0.000}; " +
                                    $"slowestIntervalOutputSpeed={(double.IsPositiveInfinity(slowestCadenceIntervalOutputSpeed) ? 0 : slowestCadenceIntervalOutputSpeed):0.000}; " +
                                    $"fullscreen={latestForegroundContext.IsFullscreenOnCapturedDisplay}; " +
                                    $"coverage={latestForegroundContext.CapturedDisplayCoverage:0.000}; " +
                                    $"customFullscreen={latestForegroundContext.UsedCustomFullscreenFallback}; " +
                                    $"profile={_activeCapturePerformanceProfile}."));
                            worstCadenceIntervalDuplicateRatio = 0;
                            slowestCadenceIntervalOutputSpeed =
                                double.PositiveInfinity;
                        }
                    }

                    if (assessment is not null &&
                        Stopwatch.GetElapsedTime(captureStarted) >= TimeSpan.FromSeconds(8))
                    {
                        var faultContext =
                            assessmentForegroundContext ??
                            latestForegroundContext;
                        var usedCustomFullscreenFallback =
                            assessment.UsedCustomFullscreenFallback ||
                            faultContext.UsedCustomFullscreenFallback;
                        var recoveryReason = SelectSafeCadenceRecoveryReason(
                            outputRequiresScaling,
                            _activeCapturePerformanceProfile,
                            assessment.Kind,
                            usedCustomFullscreenFallback);
                        var diagnostic = string.Create(
                            CultureInfo.InvariantCulture,
                            $"Desktop capture cadence fault backend={strategy.CaptureBackend}; kind={assessment.Kind}; " +
                            $"uniqueFps={assessment.UniqueFramesPerSecond:0.0}; " +
                            $"windowSeconds={assessment.Window.TotalSeconds:0.0}; " +
                            $"duplicateRatio={assessment.DuplicateRatio:0.000}; " +
                            $"outputSpeed={assessment.OutputSpeedRatio:0.000}; " +
                            $"coverage={faultContext.CapturedDisplayCoverage:0.000}; " +
                            $"customFullscreen={usedCustomFullscreenFallback}; " +
                            $"profile={_activeCapturePerformanceProfile}.");
                        if (recoveryReason is null)
                        {
                            // Win32 cannot reliably distinguish a stretched
                            // exclusive game from every borderless top-left
                            // custom-sized application. Once the safe profile
                            // is already active, content-duplicate evidence
                            // alone must not destroy the replay generation.
                            // Continue watching objective output/gap counters.
                            _captureStarvationWatchdog =
                                new CaptureStarvationWatchdog(
                                    configuration.FramesPerSecond);
                            RecordCaptureRuntimeEvent(
                                "capture_cadence_fault_suppressed",
                                process,
                                configuration,
                                strategy,
                                diagnostic);
                            EnqueueDiagnostic(
                                $"Suppressed ambiguous custom-fullscreen source cadence; objective capture throughput monitoring remains active. {diagnostic}");
                            continue;
                        }

                        RecordCaptureRuntimeEvent(
                            "capture_cadence_fault",
                            process,
                            configuration,
                            strategy,
                            diagnostic);
                        RequestCaptureRecovery(
                            recoveryReason.Value,
                            diagnostic);
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

        if (!IsRunning)
        {
            if (lastSavedPath is not null)
            {
                Publish(BuildPostSaveSnapshot(_state, lastSavedPath));
            }

            return;
        }

        TimeSpan available;
        TimeSpan retention;
        long bytes;

        lock (_fileGate)
        {
            RefreshSegmentIndexLocked();
            retention = _retention;
            var maximumCompletedSegments = checked((int)Math.Ceiling(
                retention.TotalSeconds / FfmpegArgumentBuilder.SegmentSeconds));
            var completedRangeLength = Math.Max(0, _segments.Count - 1);
            var trustedCompletedCount = _segments
                .Take(completedRangeLength)
                .Count(segment => segment.Length > 0 && segment.IsTrusted);
            var removableUntrustedSegments = _segments
                .Take(completedRangeLength)
                .Where(segment =>
                    !segment.IsTrusted &&
                    !_protectedSegments.Contains(segment.Path))
                .ToArray();
            var deleteAttemptBudgets = CalculateSegmentDeleteAttemptBudgets(
                trustedCompletedCount,
                maximumCompletedSegments,
                removableUntrustedSegments.Length);
            // Once a later segment exists, a quarantined generation head is
            // closed and can be removed. If Windows temporarily keeps the file
            // open, it remains untrusted but does not consume replay capacity.
            // Separate shares of the bounded per-refresh budget prevent many
            // undeletable quarantined heads from starving trusted retention
            // pruning. Unused capacity is lent to the other class.
            var untrustedStartIndex = FindCyclicCandidateStartIndex(
                removableUntrustedSegments
                    .Select(segment => segment.SegmentNumber)
                    .ToArray(),
                _untrustedDeleteCursorSegmentNumber);
            var untrustedPage = BuildCyclicCandidatePage(
                removableUntrustedSegments.Length,
                deleteAttemptBudgets.UntrustedAttempts,
                untrustedStartIndex);
            if (untrustedPage.CandidateIndices.Length > 0)
            {
                _untrustedDeleteCursorSegmentNumber =
                    removableUntrustedSegments[untrustedPage.NextOffset]
                        .SegmentNumber;
            }
            foreach (var candidateIndex in untrustedPage.CandidateIndices)
            {
                var segment = removableUntrustedSegments[candidateIndex];
                if (!TryDeleteFile(segment.Path))
                {
                    continue;
                }

                var currentIndex = _segments.FindIndex(candidate =>
                    candidate.Path.Equals(
                        segment.Path,
                        StringComparison.OrdinalIgnoreCase));
                if (currentIndex < 0)
                {
                    continue;
                }

                _bufferBytes = Math.Max(0, _bufferBytes - segment.Length);
                _segments.RemoveAt(currentIndex);
            }

            var trustedCompletedSegments = _segments
                .Take(Math.Max(0, _segments.Count - 1))
                .Where(segment =>
                    segment.Length > 0 &&
                    segment.IsTrusted)
                .ToArray();
            var trustedExcessSegmentNumbers =
                SelectTrustedExcessSegmentNumbers(
                    trustedCompletedSegments
                        .Select(segment => segment.SegmentNumber)
                        .ToArray(),
                    maximumCompletedSegments)
                .ToHashSet();
            var trustedCandidates = trustedCompletedSegments
                .Where(segment =>
                    trustedExcessSegmentNumbers.Contains(
                        segment.SegmentNumber) &&
                    !_protectedSegments.Contains(segment.Path))
                .ToArray();
            var trustedStartIndex = FindCyclicCandidateStartIndex(
                trustedCandidates.Select(segment => segment.SegmentNumber).ToArray(),
                _trustedDeleteCursorSegmentNumber);
            var trustedPage = BuildCyclicCandidatePage(
                trustedCandidates.Length,
                deleteAttemptBudgets.TrustedAttempts,
                trustedStartIndex);
            if (trustedPage.CandidateIndices.Length > 0)
            {
                _trustedDeleteCursorSegmentNumber =
                    trustedCandidates[trustedPage.NextOffset].SegmentNumber;
            }

            foreach (var candidateIndex in trustedPage.CandidateIndices)
            {
                var candidate = trustedCandidates[candidateIndex];
                if (!TryDeleteFile(candidate.Path))
                {
                    continue;
                }

                var currentIndex = _segments.FindIndex(segment =>
                    segment.Path.Equals(
                        candidate.Path,
                        StringComparison.OrdinalIgnoreCase));
                if (currentIndex < 0)
                {
                    continue;
                }

                _bufferBytes = Math.Max(0, _bufferBytes - candidate.Length);
                _segments.RemoveAt(currentIndex);
                trustedCompletedCount--;
            }

            var completedRangeCount = Math.Max(0, _segments.Count - 1);
            var completedCount = SelectNewestContiguousTrustedSuffix(
                    _segments,
                    completedRangeCount,
                    maximumCompletedSegments,
                    static segment => segment.SegmentNumber,
                    segment =>
                        segment.Length > 0 &&
                        segment.IsTrusted &&
                        segment.GenerationId != _exportBlockedCaptureGeneration)
                .Count;
            available = TimeSpan.FromSeconds(Math.Min(
                retention.TotalSeconds,
                completedCount * FfmpegArgumentBuilder.SegmentSeconds));
            bytes = _bufferBytes;
        }

        var replayState = Volatile.Read(ref _isSaving) != 0
            ? ReplayState.Saving
            : available >= retention
                ? ReplayState.Ready
                : ReplayState.Buffering;
        if (!IsRunning)
        {
            if (lastSavedPath is not null)
            {
                Publish(BuildPostSaveSnapshot(_state, lastSavedPath));
            }

            return;
        }

        Publish(new ReplayStateSnapshot(
            replayState,
            available,
            retention,
            bytes,
            replayState == ReplayState.Ready
                ? $"Instant Replay is ready using {_activeEncoderDescription}."
                : replayState == ReplayState.Saving
                    ? "Saving your clip…"
                    : $"Instant Replay is filling its buffer using {_activeEncoderDescription}.",
            lastSavedPath ?? _lastSavedPath));
    }

    private void RefreshBufferStateAfterSave(string? lastSavedPath)
    {
        if (!IsRunning)
        {
            Publish(BuildPostSaveSnapshot(_state, lastSavedPath));
            return;
        }

        RefreshBufferState(lastSavedPath);
    }

    internal static ReplayThreadPoolHopAwaitable SwitchToThreadPool() =>
        default;

    internal static ReplayStateSnapshot BuildPostSaveSnapshot(
        ReplayStateSnapshot current,
        string? lastSavedPath)
    {
        ArgumentNullException.ThrowIfNull(current);
        return current with
        {
            LastSavedPath = lastSavedPath ?? current.LastSavedPath
        };
    }

    internal static int CalculateSegmentDeleteAttemptBudget(
        int trustedCompletedCount,
        int maximumCompletedSegments,
        int removableUntrustedCount)
    {
        var budgets = CalculateSegmentDeleteAttemptBudgets(
            trustedCompletedCount,
            maximumCompletedSegments,
            removableUntrustedCount);
        return budgets.UntrustedAttempts + budgets.TrustedAttempts;
    }

    internal static (
        int UntrustedAttempts,
        int TrustedAttempts) CalculateSegmentDeleteAttemptBudgets(
        int trustedCompletedCount,
        int maximumCompletedSegments,
        int removableUntrustedCount)
    {
        if (trustedCompletedCount < 0 ||
            maximumCompletedSegments < 0 ||
            removableUntrustedCount < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(trustedCompletedCount),
                "Segment counts cannot be negative.");
        }

        var excessTrusted = Math.Max(
            0L,
            (long)trustedCompletedCount - maximumCompletedSegments);
        if (excessTrusted == 0)
        {
            return (
                Math.Min(
                    MaximumSegmentDeleteAttemptsPerRefresh,
                    removableUntrustedCount),
                0);
        }

        if (removableUntrustedCount == 0)
        {
            return (
                0,
                (int)Math.Min(
                    MaximumSegmentDeleteAttemptsPerRefresh,
                    excessTrusted));
        }

        var fairShare = MaximumSegmentDeleteAttemptsPerRefresh / 2;
        var untrustedAttempts = Math.Min(
            fairShare,
            removableUntrustedCount);
        var trustedAttempts = (int)Math.Min(fairShare, excessTrusted);
        var remaining = MaximumSegmentDeleteAttemptsPerRefresh -
                        untrustedAttempts -
                        trustedAttempts;

        var additionalTrusted = (int)Math.Min(
            remaining,
            excessTrusted - trustedAttempts);
        trustedAttempts += additionalTrusted;
        remaining -= additionalTrusted;

        untrustedAttempts += Math.Min(
            remaining,
            removableUntrustedCount - untrustedAttempts);
        return (untrustedAttempts, trustedAttempts);
    }

    internal static (
        int[] CandidateIndices,
        int NextOffset) BuildCyclicCandidatePage(
        int candidateCount,
        int maximumAttempts,
        int startOffset)
    {
        if (candidateCount < 0 || maximumAttempts < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(candidateCount),
                "Candidate counts and attempt limits cannot be negative.");
        }

        if (candidateCount == 0 || maximumAttempts == 0)
        {
            return ([], 0);
        }

        var normalizedStart = (int)(
            ((long)startOffset % candidateCount + candidateCount) %
            candidateCount);
        var attemptCount = Math.Min(candidateCount, maximumAttempts);
        var indices = new int[attemptCount];
        for (var offset = 0; offset < attemptCount; offset++)
        {
            indices[offset] = (normalizedStart + offset) % candidateCount;
        }

        return (
            indices,
            (normalizedStart + attemptCount) % candidateCount);
    }

    internal static int FindCyclicCandidateStartIndex(
        IReadOnlyList<int> orderedCandidateIds,
        int nextCandidateId)
    {
        ArgumentNullException.ThrowIfNull(orderedCandidateIds);
        if (orderedCandidateIds.Count == 0 || nextCandidateId < 0)
        {
            return 0;
        }

        for (var index = 0; index < orderedCandidateIds.Count; index++)
        {
            if (orderedCandidateIds[index] >= nextCandidateId)
            {
                return index;
            }
        }

        return 0;
    }

    internal static IReadOnlyList<int> SelectTrustedExcessSegmentNumbers(
        IReadOnlyList<int> orderedTrustedSegmentNumbers,
        int maximumCompletedSegments)
    {
        ArgumentNullException.ThrowIfNull(orderedTrustedSegmentNumbers);
        if (maximumCompletedSegments < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumCompletedSegments));
        }

        var excessCount = Math.Max(
            0,
            orderedTrustedSegmentNumbers.Count - maximumCompletedSegments);
        return orderedTrustedSegmentNumbers
            .Take(excessCount)
            .ToArray();
    }

    internal static IReadOnlyList<T> SelectNewestContiguousTrustedSuffix<T>(
        IReadOnlyList<T> orderedSegments,
        int completedCount,
        int maximumCount,
        Func<T, int> getSegmentNumber,
        Func<T, bool> isTrusted)
    {
        ArgumentNullException.ThrowIfNull(orderedSegments);
        ArgumentNullException.ThrowIfNull(getSegmentNumber);
        ArgumentNullException.ThrowIfNull(isTrusted);
        if (completedCount < 0 || completedCount > orderedSegments.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(completedCount));
        }

        if (maximumCount <= 0)
        {
            return [];
        }

        var result = new List<T>(Math.Min(maximumCount, completedCount));
        int? expectedSegmentNumber = null;
        for (var index = completedCount - 1;
             index >= 0 && result.Count < maximumCount;
             index--)
        {
            var segment = orderedSegments[index];
            var segmentNumber = getSegmentNumber(segment);
            if (!isTrusted(segment) ||
                expectedSegmentNumber is { } expected &&
                segmentNumber != expected)
            {
                break;
            }

            result.Add(segment);
            expectedSegmentNumber = segmentNumber - 1;
        }

        result.Reverse();
        return result;
    }

    private bool IsExportGenerationBlocked(int generation)
    {
        lock (_fileGate)
        {
            return generation >= 0 &&
                   generation == _exportBlockedCaptureGeneration;
        }
    }

    private bool IsActiveCaptureGenerationExportBlocked()
    {
        lock (_fileGate)
        {
            return _activeCaptureGeneration >= 0 &&
                   _activeCaptureGeneration ==
                       _exportBlockedCaptureGeneration;
        }
    }

    internal static bool IsCaptureGenerationExportable(
        int selectedGeneration,
        int blockedGeneration) =>
        selectedGeneration >= 0 &&
        selectedGeneration != blockedGeneration;

    internal static CaptureRecoveryReason SelectCadenceRecoveryReason(
        bool outputRequiresScaling,
        CapturePerformanceProfile performanceProfile,
        CaptureStarvationKind starvationKind =
            CaptureStarvationKind.Severe) =>
        !outputRequiresScaling &&
        performanceProfile == CapturePerformanceProfile.LowImpact
            ? starvationKind == CaptureStarvationKind.ChronicLowCadence
                ? CaptureRecoveryReason.SourceProfilePromotion
                : CaptureRecoveryReason.SourcePressure
            : CaptureRecoveryReason.SourceStarvation;

    internal static CaptureRecoveryReason? SelectSafeCadenceRecoveryReason(
        bool outputRequiresScaling,
        CapturePerformanceProfile performanceProfile,
        CaptureStarvationKind starvationKind,
        bool usedCustomFullscreenFallback)
    {
        if (!usedCustomFullscreenFallback ||
            !IsContentCadenceAssessment(starvationKind))
        {
            return SelectCadenceRecoveryReason(
                outputRequiresScaling,
                performanceProfile,
                starvationKind);
        }

        // A custom/stretched fullscreen candidate is necessarily heuristic:
        // Win32 can expose the game's pre-stretch client rectangle. In the
        // initial native low-impact profile, content cadence may safely request
        // one boundary-aligned priority/queue promotion. Once scaling or the
        // resilient profile is active, only objective graph throughput/gap
        // evidence may invalidate and restart capture.
        return !outputRequiresScaling &&
               performanceProfile == CapturePerformanceProfile.LowImpact
            ? CaptureRecoveryReason.SourceProfilePromotion
            : null;
    }

    internal static bool IsContentCadenceAssessment(
        CaptureStarvationKind starvationKind) =>
        starvationKind is
            CaptureStarvationKind.Severe or
            CaptureStarvationKind.ModerateDegradation or
            CaptureStarvationKind.SchedulingPressure or
            CaptureStarvationKind.ChronicLowCadence;

    internal static bool ShouldSuppressNonObjectiveCaptureCadence(
        bool usedCustomFullscreenFallback,
        bool outputRequiresScaling,
        CapturePerformanceProfile performanceProfile,
        bool deferredRecoveryIsPending) =>
        deferredRecoveryIsPending ||
        usedCustomFullscreenFallback &&
        (outputRequiresScaling ||
         performanceProfile == CapturePerformanceProfile.Resilient);

    internal static bool CanRefreshCaptureBackend(
        DesktopCaptureBackend captureBackend) =>
        captureBackend is DesktopCaptureBackend.WindowsGraphicsCapture or
            DesktopCaptureBackend.Gdi;

    internal static bool ShouldInvalidateCaptureGeneration(
        CaptureRecoveryReason reason) =>
        reason is not (
            CaptureRecoveryReason.ScheduledRefresh or
            CaptureRecoveryReason.SourceProfilePromotion);

    internal static CaptureStarvationAssessment? RetainFirstCaptureAssessment(
        CaptureStarvationAssessment? retained,
        CaptureStarvationAssessment? observed) =>
        retained ?? observed;

    internal static bool ShouldScheduleDiscontinuousRefreshContinuation(
        bool refreshWasQueued) =>
        !refreshWasQueued;

    internal static bool IsDiscontinuousRefreshQuiescent(
        bool hasActionablePendingRequest,
        bool coordinatorIsActive,
        bool continuationIsScheduled) =>
        !hasActionablePendingRequest &&
        !coordinatorIsActive &&
        !continuationIsScheduled;

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

        // Export only the newest contiguous trusted suffix. Crossing an
        // untrusted generation head, a deleted file, or a numbering gap could
        // otherwise splice pre-recovery audio/video onto a healthy new
        // generation and make the final MP4 appear to freeze at the join.
        return SelectNewestContiguousTrustedSuffix(
                _segments,
                completedCount,
                maximumCount,
                static segment => segment.SegmentNumber,
                segment =>
                    segment.IsTrusted &&
                    segment.GenerationId != _exportBlockedCaptureGeneration &&
                    GetFileLengthSafely(segment.Path) > 0)
            .Select(segment => segment.Path)
            .ToList();
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
            var segmentNumber = _nextSegmentNumber;
            _segments.Add(new BufferedSegment(
                nextPath,
                0,
                segmentNumber,
                _activeCaptureGeneration,
                IsCaptureSegmentTrusted(
                    segmentNumber,
                    _quarantinedGenerationHeadSegmentNumber)));
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
        _activeCaptureGeneration = 0;
        _quarantinedGenerationHeadSegmentNumber = -1;
        _exportBlockedCaptureGeneration = -1;
        _untrustedDeleteCursorSegmentNumber = -1;
        _trustedDeleteCursorSegmentNumber = -1;
    }

    private void BeginCaptureGenerationLocked(int firstSegmentNumber)
    {
        if (firstSegmentNumber < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(firstSegmentNumber));
        }

        _activeCaptureGeneration = checked(_activeCaptureGeneration + 1);
        // The first segment of a new WGC process can contain a delayed first
        // video frame while audio already begins at zero. Never concatenate
        // that startup segment into a user clip; the next closed segment proves
        // the replacement process has reached its steady two-second cadence.
        _quarantinedGenerationHeadSegmentNumber = firstSegmentNumber;
    }

    internal static bool ShouldRetainCompletedSegments(
        bool preserveCompletedSegments,
        bool reachedSegmentBoundary) =>
        preserveCompletedSegments && reachedSegmentBoundary;

    internal static bool ShouldDeferNonDestructiveCaptureRefresh(
        bool requireCompletedSegmentBoundary,
        bool reachedSegmentBoundary) =>
        requireCompletedSegmentBoundary && !reachedSegmentBoundary;

    internal static bool IsCaptureSegmentTrusted(
        int segmentNumber,
        int quarantinedGenerationHeadSegmentNumber) =>
        segmentNumber >= 0 &&
        segmentNumber != quarantinedGenerationHeadSegmentNumber;

    private void InvalidateRetainedCaptureGenerationLocked()
    {
        // A timed-out boundary or a health-triggered renewal means the old
        // process may have continued writing audio while video was stalled.
        // Keeping any of that generation can create a nominal 180-second MP4
        // whose video starts tens of seconds late. Mark every entry untrusted
        // even when Windows prevents immediate deletion, then rebuffer from the
        // replacement generation without reusing segment numbers.
        for (var index = _segments.Count - 1; index >= 0; index--)
        {
            var segment = _segments[index] with { IsTrusted = false };
            if (!_protectedSegments.Contains(segment.Path) &&
                TryDeleteFile(segment.Path))
            {
                _bufferBytes = Math.Max(0, _bufferBytes - segment.Length);
                _segments.RemoveAt(index);
                continue;
            }

            _segments[index] = segment;
        }
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

    private async Task PumpCaptureProgressAsync(
        Process process,
        DisplayOption? capturedDisplay)
    {
        var parser = new CaptureProgressParser();
        var processId = process.Id;
        while (await process.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            if (parser.TryParse(
                    line,
                    Stopwatch.GetTimestamp(),
                    out var sample) &&
                sample is not null)
            {
                Volatile.Write(ref _latestCaptureProgress, sample);
                if (ReferenceEquals(_captureProcess, process))
                {
                    while (_captureProgressSamples.Count >= 256 &&
                           _captureProgressSamples.TryDequeue(out _))
                    {
                    }

                    var foregroundContext = capturedDisplay is null
                        ? new CaptureForegroundContext(false, false)
                        : CaptureForegroundContextProbe.Read(capturedDisplay);
                    _captureProgressSamples.Enqueue(
                        new CaptureProgressEnvelope(
                            processId,
                            sample,
                            foregroundContext));
                }
            }
        }
    }

    private void ResetCaptureProgress()
    {
        Volatile.Write(ref _latestCaptureProgress, null);
        while (_captureProgressSamples.TryDequeue(out _))
        {
        }
    }

    private void RequestCaptureRecovery(
        CaptureRecoveryReason reason,
        string diagnostic)
    {
        if (reason == CaptureRecoveryReason.ScheduledRefresh)
        {
            // Routine maintenance must never depend on a WPF Dispatcher
            // subscriber. Health and hang policy remains UI-owned below.
            _ = QueueScheduledCaptureRefresh(CaptureProcessId, diagnostic);
            return;
        }

        CancellationTokenSource? invalidatedSave = null;
        if (ShouldInvalidateCaptureGeneration(reason))
        {
            lock (_fileGate)
            {
                // Safety is independent from the bounded notification/restart
                // budget. If later recovery requests are suppressed, a newly
                // degraded generation must still become immediately unexportable.
                _exportBlockedCaptureGeneration = _activeCaptureGeneration;
                if (_activeSaveCaptureGeneration == _activeCaptureGeneration)
                {
                    invalidatedSave = _activeSaveInvalidation;
                }
            }
        }

        try
        {
            // Release _saveGate promptly so recovery is not held behind a long
            // export/validation operation from the now-untrusted generation.
            invalidatedSave?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The save completed its bounded cleanup between the snapshot and
            // cancellation. The generation check at commit remains authoritative.
        }

        RefreshBufferState();
        var retryNotBefore =
            ReadCurrentCaptureRecoveryRetryNotBefore();
        var objectiveFaultMaySupersedeDeferredMaintenance =
            CanRecoverySupersedeDeferredMaintenance(reason);
        if (Volatile.Read(ref _isStopping) != 0 ||
            !objectiveFaultMaySupersedeDeferredMaintenance &&
            retryNotBefore > 0 &&
            Stopwatch.GetTimestamp() < retryNotBefore)
        {
            return;
        }

        if (!_captureRecoveryRequestGate.TryBegin(
                reason,
                out var requestId,
                out _))
        {
            return;
        }

        EnqueueDiagnostic($"Capture recovery requested: {diagnostic}");
        var handlers = CaptureRecoveryRequested;
        if (handlers is null)
        {
            if (_captureRecoveryRequestGate.Complete(
                    requestId,
                    out var scheduledRefreshQueued))
            {
                DispatchPendingScheduledRefresh(scheduledRefreshQueued);
            }

            return;
        }

        var args = new CaptureRecoveryRequestedEventArgs(
            reason,
            diagnostic,
            CaptureProcessId,
            requestId);
        foreach (EventHandler<CaptureRecoveryRequestedEventArgs> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, args);
            }
            catch
            {
                // Recovery subscribers cannot be allowed to stop health monitoring.
                if (_captureRecoveryRequestGate.Complete(
                        requestId,
                        out var scheduledRefreshQueued))
                {
                    DispatchPendingScheduledRefresh(scheduledRefreshQueued);
                }
            }
        }
    }

    private void DispatchPendingScheduledRefresh(bool scheduledRefreshQueued)
    {
        if (!scheduledRefreshQueued)
        {
            return;
        }

        _ = QueueScheduledCaptureRefresh(
            CaptureProcessId,
            "Running one coalesced WGC refresh that arrived while another recovery request was active.");
    }

    private bool QueueDegradedCaptureReprobe(
        int? expectedProcessId,
        string diagnostic)
    {
        var deadlineUtcTicks = Volatile.Read(
            ref _degradedCaptureReprobeNotBeforeUtcTicks);
        if (expectedProcessId is not { } processId ||
            processId <= 0 ||
            Volatile.Read(ref _disposed) != 0 ||
            Volatile.Read(ref _isStopping) != 0 ||
            !IsRunning ||
            IsActiveCaptureGenerationExportBlocked() ||
            !ShouldScheduleDegradedCaptureReprobe(
                _activeCaptureStrategy?.CaptureBackend,
                Volatile.Read(ref _activeStrategyUsesCapabilityProbe) != 0,
                deadlineUtcTicks,
                DateTimeOffset.UtcNow.UtcDateTime.Ticks) ||
            CaptureProcessId != processId)
        {
            return false;
        }

        var queued = _degradedCaptureReprobeCoordinator.TrySchedule(
            processId,
            diagnostic);
        if (queued)
        {
            EnqueueDiagnostic(
                $"Queued a bounded WGC capability recheck for degraded capture process {processId}: {diagnostic}");
            RecordCaptureRuntimeEvent(
                "capture_degraded_reprobe_queued",
                _captureProcess,
                detail: diagnostic);
        }

        return queued;
    }

    private async Task RunDegradedCaptureReprobeAsync(
        int expectedProcessId,
        string diagnostic,
        CancellationToken cancellationToken)
    {
        var configuration = _activeConfiguration;
        var ffmpegPath = _activeFfmpegPath;
        var currentStrategy = _activeCaptureStrategy;
        var performanceProfile = _activeCapturePerformanceProfile;
        var sessionIdentity =
            Volatile.Read(ref _captureSessionIdentity);
        if (!TryReadCancellationToken(
                _sessionCancellation,
                out var sessionCancellation))
        {
            return;
        }

        if (!IsCurrentDegradedCapture(
                expectedProcessId,
                sessionIdentity,
                configuration,
                ffmpegPath,
                currentStrategy))
        {
            return;
        }

        var foregroundContext =
            CaptureForegroundContextProbe.Read(configuration!.Display);
        if (ShouldDeferDegradedCaptureReprobeForGameplay(
                foregroundContext))
        {
            // The capability probe is a real three-second capture/encode graph.
            // Never launch that competing workload while the user is actively
            // playing; the monitor will retry the already-due check after an
            // alt-tab or idle period.
            return;
        }

        RecordCaptureRuntimeEvent(
            "capture_degraded_reprobe_started",
            _captureProcess,
            configuration,
            currentStrategy,
            diagnostic);
        var verifiedFfmpegPath = _ffmpegSetupService.FindExecutable()
            ?? throw new InvalidOperationException(
                "The verified capture engine is unavailable for the background WGC recheck.");
        if (!Path.GetFullPath(verifiedFfmpegPath).Equals(
                Path.GetFullPath(ffmpegPath!),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new SecurityException(
                "The capture engine path changed during the background WGC recheck.");
        }

        using var verifiedExecutableLease =
            _ffmpegSetupService.OpenVerifiedExecutableLease(verifiedFfmpegPath)
            ?? throw new SecurityException(
                "The capture engine failed its final pinned-file verification before the background WGC recheck.");
        using var probeCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                sessionCancellation);
        using var inputMonitorCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                sessionCancellation);
        var inputMonitorTask = CancelDegradedCaptureReprobeOnInputAsync(
            configuration.Display,
            probeCancellation,
            inputMonitorCancellation.Token);
        FfmpegCapabilitySelection selection;
        try
        {
            selection = await _capabilityProbe.SelectAsync(
                    verifiedFfmpegPath,
                    configuration,
                    probeCancellation.Token,
                    performanceProfile)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (
            sessionCancellation.IsCancellationRequested)
        {
            // A manual stop, display restart, or another capture replacement
            // superseded this old session's capability flight.
            return;
        }
        catch (OperationCanceledException) when (
            !cancellationToken.IsCancellationRequested &&
            probeCancellation.IsCancellationRequested)
        {
            RecordCaptureRuntimeEvent(
                "capture_degraded_reprobe_deferred",
                _captureProcess,
                configuration,
                currentStrategy,
                "Foreground input resumed while the compatibility re-probe was running; the competing probe was stopped.");
            return;
        }
        finally
        {
            inputMonitorCancellation.Cancel();
            try
            {
                await inputMonitorTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (
                inputMonitorCancellation.IsCancellationRequested)
            {
                // Probe completion or replay shutdown stops the input watcher.
            }
        }

        EnqueueDiagnostic(selection.Diagnostics);

        if (!IsCurrentDegradedCapture(
                expectedProcessId,
                sessionIdentity,
                configuration,
                ffmpegPath,
                currentStrategy))
        {
            return;
        }

        if (selection.Strategy.CaptureBackend !=
            DesktopCaptureBackend.WindowsGraphicsCapture)
        {
            ScheduleNextDegradedCaptureReprobe(selection.CacheExpiresAtUtc);
            RecordCaptureRuntimeEvent(
                "capture_degraded_reprobe_retained",
                _captureProcess,
                configuration,
                selection.Strategy,
                "WGC was still unavailable; the verified GDI session remains active.");
            return;
        }

        var refreshed = await RefreshCaptureAsync(
                expectedProcessId,
                cancellationToken,
                preserveCompletedSegments: true,
                verifiedReplacement: selection,
                performanceProfileOverride: performanceProfile)
            .ConfigureAwait(false);
        if (!refreshed)
        {
            if (IsCurrentDegradedCapture(
                    expectedProcessId,
                    sessionIdentity,
                    configuration,
                    ffmpegPath,
                    currentStrategy))
            {
                ScheduleNextDegradedCaptureReprobe(selection.CacheExpiresAtUtc);
            }

            EnqueueDiagnostic(
                $"Skipped stale degraded-capture promotion for process {expectedProcessId}.");
        }
    }

    private static async Task CancelDegradedCaptureReprobeOnInputAsync(
        DisplayOption display,
        CancellationTokenSource probeCancellation,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            await Task.Delay(
                    TimeSpan.FromMilliseconds(250),
                    cancellationToken)
                .ConfigureAwait(false);
            if (!CaptureForegroundContextProbe.Read(display).HasRecentInput)
            {
                continue;
            }

            try
            {
                probeCancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The probe completed between the observation and cancellation.
            }

            return;
        }
    }

    private bool IsCurrentDegradedCapture(
        int expectedProcessId,
        int expectedSessionIdentity,
        CaptureConfiguration? expectedConfiguration,
        string? expectedFfmpegPath,
        VideoEncodingStrategy? expectedStrategy) =>
        IsRunning &&
        Volatile.Read(ref _disposed) == 0 &&
        Volatile.Read(ref _isStopping) == 0 &&
        Volatile.Read(ref _activeStrategyUsesCapabilityProbe) != 0 &&
        Volatile.Read(ref _captureSessionIdentity) ==
            expectedSessionIdentity &&
        CaptureProcessId == expectedProcessId &&
        ReferenceEquals(_activeConfiguration, expectedConfiguration) &&
        string.Equals(
            _activeFfmpegPath,
            expectedFfmpegPath,
            StringComparison.OrdinalIgnoreCase) &&
        ReferenceEquals(_activeCaptureStrategy, expectedStrategy) &&
        expectedStrategy?.CaptureBackend == DesktopCaptureBackend.Gdi;

    private void ResetDegradedCaptureReprobe()
    {
        Interlocked.Exchange(ref _degradedCaptureReprobeAttempt, 0);
        Volatile.Write(
            ref _degradedCaptureReprobeNotBeforeUtcTicks,
            0);
    }

    private void ScheduleNextDegradedCaptureReprobe(
        DateTimeOffset? capabilityCacheExpiresAtUtc = null)
    {
        var attempt = Math.Max(
            0,
            Interlocked.Increment(ref _degradedCaptureReprobeAttempt) - 1);
        var serviceDeadline = DateTimeOffset.UtcNow +
            GetDegradedCaptureReprobeDelay(attempt);
        var deadline = capabilityCacheExpiresAtUtc is { } cacheDeadline &&
                       cacheDeadline > serviceDeadline
            ? cacheDeadline
            : serviceDeadline;
        Volatile.Write(
            ref _degradedCaptureReprobeNotBeforeUtcTicks,
            deadline.UtcDateTime.Ticks);
    }

    private void DeferDegradedCaptureReprobe() =>
        ScheduleNextDegradedCaptureReprobe();

    internal static TimeSpan GetDegradedCaptureReprobeDelay(int attempt)
    {
        if (attempt < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(attempt));
        }

        var exponent = Math.Min(attempt, 6);
        var ticks = DegradedCaptureReprobeInitialDelay.Ticks * (1L << exponent);
        return TimeSpan.FromTicks(Math.Min(
            ticks,
            DegradedCaptureReprobeMaximumDelay.Ticks));
    }

    internal static bool ShouldScheduleDegradedCaptureReprobe(
        DesktopCaptureBackend? captureBackend,
        bool strategyUsesCapabilityProbe,
        long deadlineUtcTicks,
        long nowUtcTicks) =>
        captureBackend == DesktopCaptureBackend.Gdi &&
        strategyUsesCapabilityProbe &&
        deadlineUtcTicks > 0 &&
        nowUtcTicks >= deadlineUtcTicks;

    internal static bool ShouldRunDegradedCaptureReprobe(
        DesktopCaptureBackend? captureBackend,
        bool strategyUsesCapabilityProbe,
        long deadlineUtcTicks,
        long nowUtcTicks,
        CaptureForegroundContext foregroundContext) =>
        ShouldScheduleDegradedCaptureReprobe(
            captureBackend,
            strategyUsesCapabilityProbe,
            deadlineUtcTicks,
            nowUtcTicks) &&
        !ShouldDeferDegradedCaptureReprobeForGameplay(
            foregroundContext);

    internal static bool ShouldDeferDegradedCaptureReprobeForGameplay(
        CaptureForegroundContext foregroundContext) =>
        foregroundContext.HasRecentInput;

    internal static bool ShouldMaintainDegradedCaptureReprobe(
        DesktopCaptureBackend? captureBackend,
        bool strategyUsesCapabilityProbe) =>
        captureBackend == DesktopCaptureBackend.Gdi &&
        strategyUsesCapabilityProbe;

    internal static bool CanRecoverySupersedeDeferredMaintenance(
        CaptureRecoveryReason reason) =>
        reason is
            CaptureRecoveryReason.SourcePressure or
            CaptureRecoveryReason.SourceStarvation or
            CaptureRecoveryReason.CaptureHang;

    internal static bool HasCaptureRecoveryRetryCapacity(int attempt)
    {
        if (attempt < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(attempt));
        }

        return attempt <= MaximumCaptureRecoveryRetryAttempts;
    }

    internal static bool CanPromoteDegradedCapture(
        VideoEncodingStrategy currentStrategy,
        VideoEncodingStrategy replacementStrategy,
        bool strategyUsesCapabilityProbe)
    {
        ArgumentNullException.ThrowIfNull(currentStrategy);
        ArgumentNullException.ThrowIfNull(replacementStrategy);
        return strategyUsesCapabilityProbe &&
               currentStrategy.CaptureBackend == DesktopCaptureBackend.Gdi &&
               replacementStrategy.CaptureBackend ==
               DesktopCaptureBackend.WindowsGraphicsCapture;
    }

    internal static bool ShouldDeferDegradedCapturePromotion(
        bool promotesDegradedCapture,
        bool reachedSegmentBoundary) =>
        promotesDegradedCapture && !reachedSegmentBoundary;

    internal static bool ShouldRetryCaptureLaunch(
        bool allowFreshCapabilityRetry,
        bool strategyUsesCapabilityProbe,
        bool realCaptureLaunchFailed) =>
        allowFreshCapabilityRetry &&
        strategyUsesCapabilityProbe &&
        realCaptureLaunchFailed;

    private bool QueueScheduledCaptureRefresh(
        int? expectedProcessId,
        string diagnostic)
    {
        var retryNotBefore =
            ReadCurrentCaptureRecoveryRetryNotBefore();
        if (expectedProcessId is not { } processId ||
            processId <= 0 ||
            Volatile.Read(ref _disposed) != 0 ||
            Volatile.Read(ref _isStopping) != 0 ||
            retryNotBefore > 0 && Stopwatch.GetTimestamp() < retryNotBefore ||
            !IsRunning ||
            _activeCaptureStrategy?.CaptureBackend !=
                DesktopCaptureBackend.WindowsGraphicsCapture ||
            CaptureProcessId != processId)
        {
            return false;
        }

        var queued = _scheduledCaptureRefreshCoordinator.TrySchedule(
            processId,
            diagnostic);
        if (queued)
        {
            EnqueueDiagnostic(
                $"Queued service-owned WGC renewal for capture process {processId}: {diagnostic}");
            RecordCaptureRuntimeEvent(
                "capture_renewal_queued",
                _captureProcess,
                detail: diagnostic);
        }

        return queued;
    }

    private async Task RunScheduledCaptureRefreshAsync(
        int expectedProcessId,
        string diagnostic,
        CancellationToken cancellationToken)
    {
        RecordCaptureRuntimeEvent(
            "capture_renewal_started",
            _captureProcess,
            detail: diagnostic);
        var refreshed = await RefreshCaptureAsync(
                expectedProcessId,
                cancellationToken)
            .ConfigureAwait(false);
        if (!refreshed)
        {
            EnqueueDiagnostic(
                $"Skipped stale service-owned WGC renewal for capture process {expectedProcessId}: {diagnostic}");
            RecordCaptureRuntimeEvent(
                "capture_renewal_skipped",
                _captureProcess,
                detail: diagnostic);
        }
    }

    private async Task RunDiscontinuousCaptureRefreshAsync(
        int expectedProcessId,
        string diagnostic,
        CancellationToken cancellationToken)
    {
        var refreshCompleted = false;
        long requestedEpoch;
        long completedEpoch;
        int requestedSessionIdentity;
        lock (_discontinuousRefreshGate)
        {
            requestedEpoch = _discontinuousRefreshRequestedEpoch;
            completedEpoch = _discontinuousRefreshCompletedEpoch;
            requestedSessionIdentity = _discontinuousRefreshSessionIdentity;
            diagnostic = _discontinuousRefreshDiagnostic;
        }

        if (requestedSessionIdentity !=
                Volatile.Read(ref _captureSessionIdentity) ||
            requestedEpoch <= completedEpoch)
        {
            return;
        }

        try
        {
            RecordCaptureRuntimeEvent(
                "capture_display_transition_started",
                _captureProcess,
                detail:
                    $"epoch={requestedEpoch}; session={requestedSessionIdentity}; {diagnostic}");

            // A graphics-device/display-mode transition is not a clean segment
            // boundary. Audio can continue while an old capture source supplies a
            // held frame, so never concatenate the previous generation onto clips
            // produced after the transition.
            var currentProcessId = CaptureProcessId;
            var targetProcessId = currentProcessId == expectedProcessId
                ? expectedProcessId
                : currentProcessId;
            var refreshed = targetProcessId is { } processId &&
                await RefreshCaptureAsync(
                    processId,
                    cancellationToken,
                    preserveCompletedSegments: false,
                    expectedSessionIdentity: requestedSessionIdentity,
                    invalidateCapabilitySelectionBeforeRefresh: true)
                .ConfigureAwait(false);
            if (refreshed)
            {
                RecordCaptureRuntimeEvent(
                    "capture_display_transition_completed",
                    _captureProcess,
                    detail:
                        $"epoch={requestedEpoch}; session={requestedSessionIdentity}.");
                CompleteDiscontinuousRefresh(
                    requestedEpoch,
                    requestedSessionIdentity);
                refreshCompleted = true;
                return;
            }

            RecordCaptureRuntimeEvent(
                "capture_display_transition_skipped",
                _captureProcess,
                detail:
                    $"epoch={requestedEpoch}; session={requestedSessionIdentity}; {diagnostic}");
        }
        finally
        {
            // RefreshCaptureAsync can throw after scheduling its bounded
            // verification retry. Keep ownership of the epoch in that path too;
            // the continuation waits for the retry window instead of spinning.
            if (!refreshCompleted &&
                HasActionablePendingDiscontinuousRefresh())
            {
                SchedulePendingDiscontinuousRefresh();
            }
        }
    }

    private void CompleteDiscontinuousRefresh(
        long completedEpoch,
        int sessionIdentity)
    {
        var hasNewerRequest = false;
        lock (_discontinuousRefreshGate)
        {
            if (sessionIdentity != _discontinuousRefreshSessionIdentity)
            {
                return;
            }

            _discontinuousRefreshCompletedEpoch = Math.Max(
                _discontinuousRefreshCompletedEpoch,
                completedEpoch);
            hasNewerRequest =
                _discontinuousRefreshRequestedEpoch >
                _discontinuousRefreshCompletedEpoch;
        }

        if (hasNewerRequest)
        {
            SchedulePendingDiscontinuousRefresh();
        }
    }

    private void SchedulePendingDiscontinuousRefresh()
    {
        lock (_discontinuousRefreshGate)
        {
            if (_discontinuousRefreshContinuationScheduled != 0)
            {
                return;
            }

            _discontinuousRefreshContinuationScheduled = 1;
            _discontinuousRefreshContinuationTask = Task.Run(
                ResumePendingDiscontinuousRefreshAsync);
        }
    }

    private async Task ResumePendingDiscontinuousRefreshAsync()
    {
        try
        {
            if (!TryReadCancellationToken(
                    _captureRecoveryRetryCancellation,
                    out var serviceCancellation))
            {
                return;
            }

            while (true)
            {
                try
                {
                    await _discontinuousCaptureRefreshCoordinator
                        .WaitForIdleAsync()
                        .ConfigureAwait(false);
                }
                catch
                {
                    // The coordinator reports its own bounded failure diagnostic.
                }

                await WaitForCaptureRecoveryRetryWindowAsync(
                        serviceCancellation)
                    .ConfigureAwait(false);

                var readiness =
                    ReadPendingDiscontinuousRefreshReadiness(
                        out _,
                        out var diagnostic);
                if (readiness ==
                    DiscontinuousRefreshReadiness.Terminal)
                {
                    return;
                }

                if (readiness ==
                    DiscontinuousRefreshReadiness.Wait)
                {
                    // Another same-session refresh temporarily owns the process
                    // and can leave it null while _isStopping is set. Retain the
                    // sole continuation through that replacement instead of
                    // dropping a coalesced display epoch.
                    await Task.Delay(
                            TimeSpan.FromMilliseconds(250),
                            serviceCancellation)
                        .ConfigureAwait(false);
                    continue;
                }

                if (CaptureProcessId is not { } processId)
                {
                    // The readiness snapshot raced a process replacement.
                    // Re-read the pending epoch instead of releasing ownership.
                    await Task.Delay(
                            TimeSpan.FromMilliseconds(250),
                            serviceCancellation)
                        .ConfigureAwait(false);
                    continue;
                }

                if (_discontinuousCaptureRefreshCoordinator.TrySchedule(
                        processId,
                        diagnostic))
                {
                    return;
                }

                // A worker won the coordinator between WaitForIdleAsync and
                // TrySchedule. Follow it and retry the still-owned epoch.
                await Task.Yield();
            }
        }
        catch (OperationCanceledException)
        {
            // The replay session stopped before its retry window opened.
        }
        finally
        {
            lock (_discontinuousRefreshGate)
            {
                _discontinuousRefreshContinuationScheduled = 0;
            }
        }

        // Re-check after releasing the continuation flag even when TrySchedule
        // accepted a worker. That worker can skip/fail before this continuation
        // clears its flag; its own reschedule attempt would otherwise be lost.
        // The next continuation safely waits for an accepted active worker.
        if (HasActionablePendingDiscontinuousRefresh())
        {
            SchedulePendingDiscontinuousRefresh();
        }
    }

    private async Task WaitForDiscontinuousCaptureRefreshIdleAsync()
    {
        while (true)
        {
            Task continuationTask;
            lock (_discontinuousRefreshGate)
            {
                continuationTask =
                    _discontinuousRefreshContinuationTask;
            }

            await Task.WhenAll(
                    _discontinuousCaptureRefreshCoordinator.WaitForIdleAsync(),
                    continuationTask)
                .ConfigureAwait(false);

            bool continuationScheduled;
            lock (_discontinuousRefreshGate)
            {
                continuationScheduled =
                    _discontinuousRefreshContinuationScheduled != 0;
            }

            if (IsDiscontinuousRefreshQuiescent(
                    HasActionablePendingDiscontinuousRefresh(),
                    _discontinuousCaptureRefreshCoordinator.IsActive,
                    continuationScheduled))
            {
                return;
            }

            if (HasActionablePendingDiscontinuousRefresh())
            {
                SchedulePendingDiscontinuousRefresh();
            }

            await Task.Yield();
        }
    }

    private bool HasActionablePendingDiscontinuousRefresh()
    {
        return ReadPendingDiscontinuousRefreshReadiness(
                out _,
                out _) ==
            DiscontinuousRefreshReadiness.Actionable;
    }

    private async Task WaitForCaptureRecoveryRetryWindowAsync(
        CancellationToken serviceCancellation)
    {
        while (true)
        {
            if (ReadPendingDiscontinuousRefreshReadiness(
                    out _,
                    out _) ==
                DiscontinuousRefreshReadiness.Terminal)
            {
                return;
            }

            var recoveryRetry =
                Volatile.Read(ref _captureRecoveryRetryState);
            if (!IsCurrentCaptureRecoveryRetry(
                    recoveryRetry.CurrentGeneration,
                    recoveryRetry.TaskGeneration))
            {
                // A successful start/refresh/stop superseded the registered
                // delay. Never hold a newer display epoch behind that stale
                // task even if its cancellation continuation has not run yet.
                return;
            }

            var retryNotBefore =
                recoveryRetry.NotBeforeTimestamp;
            var now = Stopwatch.GetTimestamp();
            if (recoveryRetry.Task.IsCompleted &&
                ReferenceEquals(
                    recoveryRetry,
                    Volatile.Read(ref _captureRecoveryRetryState)) &&
                retryNotBefore <= now)
            {
                return;
            }

            var pollDelay = TimeSpan.FromMilliseconds(250);
            if (retryNotBefore > now)
            {
                var retryDelay = TimeSpan.FromSeconds(
                    (retryNotBefore - now) /
                    (double)Stopwatch.Frequency);
                if (retryDelay < pollDelay)
                {
                    pollDelay = retryDelay;
                }
            }

            await Task.Delay(
                    pollDelay,
                    serviceCancellation)
                .ConfigureAwait(false);
        }
    }

    private DiscontinuousRefreshReadiness
        ReadPendingDiscontinuousRefreshReadiness(
            out DiscontinuousRefreshTicket ticket,
            out string diagnostic)
    {
        var hasCurrentPendingRequest =
            TryReadCurrentPendingDiscontinuousRefresh(
                out ticket,
                out diagnostic);
        var captureBackend =
            _activeCaptureStrategy?.CaptureBackend;
        return GetDiscontinuousRefreshReadiness(
            hasCurrentPendingRequest,
            Volatile.Read(ref _disposed) != 0,
            IsRunning,
            Volatile.Read(ref _isStopping) != 0,
            CaptureProcessId is not null,
            captureBackend is { } backend &&
            CanRefreshCaptureBackend(backend));
    }

    internal static DiscontinuousRefreshReadiness
        GetDiscontinuousRefreshReadiness(
            bool hasCurrentPendingRequest,
            bool isDisposed,
            bool isRunning,
            bool isStopping,
            bool hasCaptureProcess,
            bool canRefreshCaptureBackend)
    {
        if (!hasCurrentPendingRequest || isDisposed)
        {
            return DiscontinuousRefreshReadiness.Terminal;
        }

        if (isStopping)
        {
            return DiscontinuousRefreshReadiness.Wait;
        }

        if (!isRunning || !canRefreshCaptureBackend)
        {
            return DiscontinuousRefreshReadiness.Terminal;
        }

        return hasCaptureProcess
            ? DiscontinuousRefreshReadiness.Actionable
            : DiscontinuousRefreshReadiness.Wait;
    }

    private void InvalidateCaptureRecoveryRetry()
    {
        lock (_captureRecoveryRetryGate)
        {
            var current =
                Volatile.Read(ref _captureRecoveryRetryState);
            var generation = unchecked(
                ++_captureRecoveryRetrySequence);
            Volatile.Write(
                ref _captureRecoveryRetryState,
                new CaptureRecoveryRetryState(
                    generation,
                    current.TaskGeneration,
                    NotBeforeTimestamp: 0,
                    current.Task));
        }
    }

    private long ReadCurrentCaptureRecoveryRetryNotBefore()
    {
        var retry =
            Volatile.Read(ref _captureRecoveryRetryState);
        return IsCurrentCaptureRecoveryRetry(
                retry.CurrentGeneration,
                retry.TaskGeneration)
            ? retry.NotBeforeTimestamp
            : 0;
    }

    internal static bool IsCurrentCaptureRecoveryRetry(
        int currentGeneration,
        int taskGeneration) =>
        currentGeneration == taskGeneration;

    private static bool TryReadCancellationToken(
        CancellationTokenSource? source,
        out CancellationToken cancellationToken)
    {
        if (source is null)
        {
            cancellationToken = default;
            return false;
        }

        try
        {
            cancellationToken = source.Token;
            return true;
        }
        catch (ObjectDisposedException)
        {
            cancellationToken = default;
            return false;
        }
    }

    private DiscontinuousRefreshTicket? ReadPendingDiscontinuousRefreshTicket()
    {
        return TryReadCurrentPendingDiscontinuousRefresh(
                out var ticket,
                out _)
            ? ticket
            : null;
    }

    private bool TryReadCurrentPendingDiscontinuousRefresh(
        out DiscontinuousRefreshTicket ticket,
        out string diagnostic)
    {
        lock (_discontinuousRefreshGate)
        {
            var sessionIdentity =
                Volatile.Read(ref _captureSessionIdentity);
            if (_discontinuousRefreshSessionIdentity !=
                    sessionIdentity ||
                _discontinuousRefreshRequestedEpoch <=
                    _discontinuousRefreshCompletedEpoch)
            {
                ticket = default;
                diagnostic = string.Empty;
                return false;
            }

            ticket = new DiscontinuousRefreshTicket(
                _discontinuousRefreshRequestedEpoch,
                sessionIdentity);
            diagnostic = _discontinuousRefreshDiagnostic;
            return true;
        }
    }

    private bool DeferCaptureRecoveryRetry(
        int expectedProcessId,
        bool preserveCompletedSegments,
        CapturePerformanceProfile performanceProfile,
        FfmpegCapabilitySelection? verifiedReplacement,
        bool requireCompletedSegmentBoundary,
        int? expectedSessionIdentity,
        bool invalidateCapabilitySelectionBeforeRefresh,
        int recoveryRetryAttempt,
        int boundaryRetryAttempt)
    {
        if (recoveryRetryAttempt < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(recoveryRetryAttempt));
        }

        if (boundaryRetryAttempt < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(boundaryRetryAttempt));
        }

        var retryDelay = CaptureRecoveryRetryDelay;
        if (recoveryRetryAttempt > 0 &&
            !HasCaptureRecoveryRetryCapacity(
                recoveryRetryAttempt))
        {
            if (IsActiveCaptureGenerationExportBlocked())
            {
                EnqueueDiagnostic(
                    $"Capture recovery exhausted {MaximumCaptureRecoveryRetryAttempts} delayed verification attempts while the active generation was blocked.");
                return false;
            }

            // Routine renewal or a boundary-safe profile promotion still has
            // a healthy old recorder. Keep it running and retry much later,
            // with a fresh bounded verification chain.
            recoveryRetryAttempt = 0;
            retryDelay =
                CaptureRecoveryRetryExhaustedCooldown;
            EnqueueDiagnostic(
                $"Capture maintenance exhausted {MaximumCaptureRecoveryRetryAttempts} delayed verification attempts; the healthy recorder remains active and verification will retry in {retryDelay.TotalMinutes:0} minutes.");
        }

        if (boundaryRetryAttempt > 0 &&
            !HasCaptureRecoveryRetryCapacity(
                boundaryRetryAttempt))
        {
            // A missing boundary is not a verification failure and must not
            // consume a later objective/display recovery's retry budget. Back
            // off the optional non-destructive promotion, then start its own
            // fresh boundary chain.
            boundaryRetryAttempt = 0;
            retryDelay =
                CaptureRecoveryRetryExhaustedCooldown;
            EnqueueDiagnostic(
                $"Capture profile promotion did not observe a completed boundary after {MaximumCaptureRecoveryRetryAttempts} delayed attempts; the healthy recorder remains active and promotion will retry in {retryDelay.TotalMinutes:0} minutes.");
        }

        var delayTicks = checked((long)(
            retryDelay.TotalSeconds * Stopwatch.Frequency));
        var retryNotBefore =
            checked(Stopwatch.GetTimestamp() + delayTicks);
        expectedSessionIdentity ??=
            Volatile.Read(ref _captureSessionIdentity);
        var cancellationToken = _captureRecoveryRetryCancellation.Token;
        int generation;
        TaskCompletionSource retryCompletion;
        lock (_captureRecoveryRetryGate)
        {
            generation = unchecked(
                ++_captureRecoveryRetrySequence);
            retryCompletion = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            Volatile.Write(
                ref _captureRecoveryRetryState,
                new CaptureRecoveryRetryState(
                    generation,
                    generation,
                    retryNotBefore,
                    retryCompletion.Task));
        }

        _ = RunPublishedCaptureRecoveryRetryAsync(
            retryCompletion,
            expectedProcessId,
            generation,
            preserveCompletedSegments,
            performanceProfile,
            verifiedReplacement,
            requireCompletedSegmentBoundary,
            expectedSessionIdentity,
            invalidateCapabilitySelectionBeforeRefresh,
            recoveryRetryAttempt,
            boundaryRetryAttempt,
            retryDelay,
            cancellationToken);
        return true;
    }

    private async Task RunPublishedCaptureRecoveryRetryAsync(
        TaskCompletionSource retryCompletion,
        int expectedProcessId,
        int generation,
        bool preserveCompletedSegments,
        CapturePerformanceProfile performanceProfile,
        FfmpegCapabilitySelection? verifiedReplacement,
        bool requireCompletedSegmentBoundary,
        int? expectedSessionIdentity,
        bool invalidateCapabilitySelectionBeforeRefresh,
        int recoveryRetryAttempt,
        int boundaryRetryAttempt,
        TimeSpan retryDelay,
        CancellationToken cancellationToken)
    {
        try
        {
            await RetryCaptureRecoveryAfterDelayAsync(
                    expectedProcessId,
                    generation,
                    preserveCompletedSegments,
                    performanceProfile,
                    verifiedReplacement,
                    requireCompletedSegmentBoundary,
                    expectedSessionIdentity,
                    invalidateCapabilitySelectionBeforeRefresh,
                    recoveryRetryAttempt,
                    boundaryRetryAttempt,
                    retryDelay,
                    cancellationToken)
                .ConfigureAwait(false);
            retryCompletion.TrySetResult();
        }
        catch (Exception exception)
        {
            retryCompletion.TrySetException(exception);
        }
    }

    private async Task RetryCaptureRecoveryAfterDelayAsync(
        int expectedProcessId,
        int generation,
        bool preserveCompletedSegments,
        CapturePerformanceProfile performanceProfile,
        FfmpegCapabilitySelection? verifiedReplacement,
        bool requireCompletedSegmentBoundary,
        int? expectedSessionIdentity,
        bool invalidateCapabilitySelectionBeforeRefresh,
        int recoveryRetryAttempt,
        int boundaryRetryAttempt,
        TimeSpan retryDelay,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(retryDelay, cancellationToken).ConfigureAwait(false);
            if (Volatile.Read(ref _disposed) != 0 ||
                !IsCurrentCaptureRecoveryRetry(
                    Volatile.Read(
                        ref _captureRecoveryRetryState).CurrentGeneration,
                    generation) ||
                !IsRunning ||
                CaptureProcessId != expectedProcessId ||
                expectedSessionIdentity is { } sessionIdentity &&
                Volatile.Read(ref _captureSessionIdentity) != sessionIdentity)
            {
                return;
            }

            var discontinuousRefreshTicket = preserveCompletedSegments
                ? null
                : ReadPendingDiscontinuousRefreshTicket();
            var refreshed = await RefreshCaptureAsync(
                    expectedProcessId,
                    cancellationToken,
                    preserveCompletedSegments,
                    verifiedReplacement,
                    performanceProfile,
                    requireCompletedSegmentBoundary,
                    expectedSessionIdentity,
                    invalidateCapabilitySelectionBeforeRefresh ||
                    discontinuousRefreshTicket is not null,
                    recoveryRetryAttempt,
                    boundaryRetryAttempt)
                .ConfigureAwait(false);
            if (refreshed &&
                discontinuousRefreshTicket is { } completedTransition)
            {
                // The retry created a clean generation with the same invalidating
                // semantics requested by the display-transition worker. Complete
                // only the epoch observed before that replacement started; a
                // transition arriving during it remains pending.
                CompleteDiscontinuousRefresh(
                    completedTransition.Epoch,
                    completedTransition.SessionIdentity);
            }

            if (!refreshed)
            {
                EnqueueDiagnostic(
                    $"Skipped stale capture-recovery retry for process {expectedProcessId}.");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Service disposal superseded the delayed retry. Process-generation
            // replacement intentionally does not cancel this token.
        }
        catch (Exception exception)
        {
            // RefreshCaptureAsync schedules another bounded retry when the same
            // transient preflight fails again. Observe this task locally so a
            // background retry can never become an unobserved exception.
            EnqueueDiagnostic(
                $"Capture-recovery retry for process {expectedProcessId} failed: " +
                exception.GetBaseException().Message);
        }
        finally
        {
            if (HasActionablePendingDiscontinuousRefresh())
            {
                SchedulePendingDiscontinuousRefresh();
            }
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

    private void RecordCaptureRuntimeEvent(
        string eventName,
        Process? process = null,
        CaptureConfiguration? configuration = null,
        VideoEncodingStrategy? strategy = null,
        string? detail = null,
        bool includeResourceSnapshots = true)
    {
        try
        {
            configuration ??= _activeConfiguration;
            strategy ??= _activeCaptureStrategy;
            CaptureOutputSize? outputSize = configuration is null
                ? null
                : CaptureGeometry.ResolveOutputSize(
                    configuration.Display,
                    configuration.Resolution);
            if (configuration is not null && outputSize is not null)
            {
                var captureContext = string.Create(
                    CultureInfo.InvariantCulture,
                    $"source={configuration.Display.Width}x{configuration.Display.Height}; " +
                    $"output={outputSize.Value.Width}x{outputSize.Value.Height}; " +
                    $"resolution={configuration.Resolution.Id}; " +
                    $"requiresScaling={outputSize.Value.RequiresScaling}; " +
                    $"monitorIndex={configuration.Display.MonitorIndex}; " +
                    $"profile={_activeCapturePerformanceProfile}.");
                detail = string.IsNullOrWhiteSpace(detail)
                    ? captureContext
                    : $"{captureContext} {detail}";
            }

            _runtimeJournal.Record(
                eventName,
                process,
                strategy?.CaptureBackend.ToString(),
                outputSize?.Width,
                outputSize?.Height,
                configuration?.FramesPerSecond,
                configuration?.CaptureCursor,
                detail,
                includeResourceSnapshots);
        }
        catch
        {
            // Optional local observability must never affect capture or cleanup.
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

    internal static IReadOnlyList<string> BuildExportValidationArguments(string mediaPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaPath);
        return
        [
            "-v", "error",
            "-protocol_whitelist", "file",
            "-show_entries",
            "stream=codec_type,start_time,duration,avg_frame_rate,r_frame_rate,nb_frames:format=duration",
            "-of", "json",
            mediaPath
        ];
    }

    internal static bool TryValidateExportProbe(
        string probeJson,
        TimeSpan expectedDuration,
        int expectedFramesPerSecond,
        bool expectedAudio,
        out string failure)
    {
        failure = string.Empty;
        if (expectedDuration <= TimeSpan.Zero ||
            expectedFramesPerSecond is < 1 or > 240)
        {
            failure = "The expected clip timeline is invalid.";
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(probeJson, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 16
            });
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("streams", out var streams) ||
                streams.ValueKind != JsonValueKind.Array)
            {
                failure = "The validator did not find an MP4 stream table.";
                return false;
            }

            JsonElement? video = null;
            JsonElement? audio = null;
            foreach (var stream in streams.EnumerateArray())
            {
                if (stream.ValueKind != JsonValueKind.Object ||
                    !stream.TryGetProperty("codec_type", out var codecType) ||
                    codecType.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                switch (codecType.GetString())
                {
                    case "video" when video is null:
                        video = stream.Clone();
                        break;
                    case "audio" when audio is null:
                        audio = stream.Clone();
                        break;
                }
            }

            if (video is null)
            {
                failure = "The generated clip has no video stream.";
                return false;
            }

            if (expectedAudio && audio is null)
            {
                failure = "The generated clip is missing its expected audio stream.";
                return false;
            }

            if (!TryReadProbeSeconds(video.Value, "start_time", out var videoStart) ||
                !TryReadProbeSeconds(video.Value, "duration", out var videoDuration))
            {
                failure = "The video timeline is missing start or duration metadata.";
                return false;
            }

            var frameTolerance = Math.Max(0.05, 2d / expectedFramesPerSecond);
            var durationTolerance = Math.Max(0.15, 3d / expectedFramesPerSecond);
            if (Math.Abs(videoStart) > frameTolerance)
            {
                failure = string.Create(
                    CultureInfo.InvariantCulture,
                    $"The video begins at {videoStart:0.###}s instead of zero.");
                return false;
            }

            if (Math.Abs(videoDuration - expectedDuration.TotalSeconds) > durationTolerance)
            {
                failure = string.Create(
                    CultureInfo.InvariantCulture,
                    $"The video duration is {videoDuration:0.###}s; expected {expectedDuration.TotalSeconds:0.###}s.");
                return false;
            }

            if (!TryReadProbeRate(video.Value, "avg_frame_rate", out var averageRate) ||
                Math.Abs(averageRate - expectedFramesPerSecond) >
                Math.Max(1d, expectedFramesPerSecond * 0.08))
            {
                failure = string.Create(
                    CultureInfo.InvariantCulture,
                    $"The average video cadence is {averageRate:0.##} FPS; expected {expectedFramesPerSecond} FPS.");
                return false;
            }

            if (TryReadProbeLong(video.Value, "nb_frames", out var frameCount))
            {
                var expectedFrameCount = expectedDuration.TotalSeconds * expectedFramesPerSecond;
                var frameCountTolerance = Math.Max(3d, expectedFramesPerSecond * 0.25);
                if (Math.Abs(frameCount - expectedFrameCount) > frameCountTolerance)
                {
                    failure = string.Create(
                        CultureInfo.InvariantCulture,
                        $"The video contains {frameCount} frames; expected about {expectedFrameCount:0}.");
                    return false;
                }
            }

            if (!document.RootElement.TryGetProperty("format", out var format) ||
                !TryReadProbeSeconds(format, "duration", out var containerDuration) ||
                Math.Abs(containerDuration - expectedDuration.TotalSeconds) >
                Math.Max(0.25, durationTolerance))
            {
                failure = "The MP4 container duration does not match the requested clip.";
                return false;
            }

            if (audio is { } audioStream)
            {
                if (!TryReadProbeSeconds(audioStream, "start_time", out var audioStart) ||
                    !TryReadProbeSeconds(audioStream, "duration", out var audioDuration))
                {
                    failure = "The audio timeline is missing start or duration metadata.";
                    return false;
                }

                if (Math.Abs(audioStart) > 0.1 ||
                    Math.Abs(audioDuration - videoDuration) > 0.25)
                {
                    failure = string.Create(
                        CultureInfo.InvariantCulture,
                        $"Audio/video timing differs (audio start {audioStart:0.###}s, " +
                        $"audio {audioDuration:0.###}s, video {videoDuration:0.###}s).");
                    return false;
                }
            }

            return true;
        }
        catch (Exception exception) when (
            exception is JsonException or InvalidOperationException or FormatException)
        {
            failure = "The clip validator returned malformed metadata.";
            return false;
        }
    }

    private static bool TryReadProbeSeconds(
        JsonElement element,
        string propertyName,
        out double value)
    {
        value = 0;
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return false;
        }

        if (property.ValueKind == JsonValueKind.Number)
        {
            return property.TryGetDouble(out value) && double.IsFinite(value);
        }

        return property.ValueKind == JsonValueKind.String &&
               double.TryParse(
                   property.GetString(),
                   NumberStyles.Float,
                   CultureInfo.InvariantCulture,
                   out value) &&
               double.IsFinite(value);
    }

    private static bool TryReadProbeRate(
        JsonElement element,
        string propertyName,
        out double value)
    {
        value = 0;
        if (!element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var text = property.GetString();
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var slash = text.IndexOf('/');
        if (slash < 0)
        {
            return double.TryParse(
                       text,
                       NumberStyles.Float,
                       CultureInfo.InvariantCulture,
                       out value) &&
                   double.IsFinite(value) &&
                   value > 0;
        }

        return double.TryParse(
                   text.AsSpan(0, slash),
                   NumberStyles.Float,
                   CultureInfo.InvariantCulture,
                   out var numerator) &&
               double.TryParse(
                   text.AsSpan(slash + 1),
                   NumberStyles.Float,
                   CultureInfo.InvariantCulture,
                   out var denominator) &&
               double.IsFinite(numerator) &&
               double.IsFinite(denominator) &&
               denominator != 0 &&
               double.IsFinite(value = numerator / denominator) &&
               value > 0;
    }

    private static bool TryReadProbeLong(
        JsonElement element,
        string propertyName,
        out long value)
    {
        value = 0;
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return false;
        }

        if (property.ValueKind == JsonValueKind.Number)
        {
            return property.TryGetInt64(out value) && value >= 0;
        }

        return property.ValueKind == JsonValueKind.String &&
               long.TryParse(
                   property.GetString(),
                   NumberStyles.None,
                   CultureInfo.InvariantCulture,
                   out value) &&
               value >= 0;
    }

    private static async Task ValidateExportAsync(
        string ffprobePath,
        string mediaPath,
        TimeSpan expectedDuration,
        int expectedFramesPerSecond,
        bool expectedAudio,
        CancellationToken cancellationToken)
    {
        using var process = CreateProcess(
            ffprobePath,
            BuildExportValidationArguments(mediaPath),
            redirectStandardInput: false,
            redirectStandardOutput: true);
        if (!process.Start())
        {
            throw new InvalidOperationException("Windows could not start the clip validator.");
        }

        using var processJob = AttachAuxiliaryProcessLifetime(process);
        _ = ProcessTuning.TryApplyLowImpactPriority(process);
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = ReadLastDiagnosticLineAsync(process.StandardError);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeout.Token);
        try
        {
            await process.WaitForExitAsync(linkedCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (
            timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            await WaitForExportTerminationAsync(process).ConfigureAwait(false);
            _ = outputTask.ContinueWith(
                task => _ = task.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
            _ = errorTask.ContinueWith(
                task => _ = task.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
            throw new InvalidDataException("Clip validation timed out.");
        }
        catch
        {
            TryKill(process);
            await WaitForExportTerminationAsync(process).ConfigureAwait(false);
            _ = outputTask.ContinueWith(
                task => _ = task.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
            _ = errorTask.ContinueWith(
                task => _ = task.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
            throw;
        }

        var output = await outputTask.ConfigureAwait(false);
        var error = await errorTask.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new InvalidDataException(
                string.IsNullOrWhiteSpace(error)
                    ? "The generated clip could not be read back by ClipForge."
                    : $"The generated clip could not be validated. {error}");
        }

        if (!TryValidateExportProbe(
                output,
                expectedDuration,
                expectedFramesPerSecond,
                expectedAudio,
                out var failure))
        {
            throw new InvalidDataException(
                $"ClipForge rejected a clip with a broken media timeline. {failure}");
        }
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

        using var processJob = AttachAuxiliaryProcessLifetime(process);
        _ = ProcessTuning.TryApplyLowImpactPriority(process);

        // FFmpeg can repeat warnings for every frame. Drain stderr continuously
        // so the process cannot block, but retain only the final bounded line
        // instead of growing an in-memory string for the whole export.
        var errorTask = ReadLastDiagnosticLineAsync(process.StandardError);
        using var timeout = new CancellationTokenSource(ExportProcessMaximumRuntime);
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeout.Token);
        try
        {
            await process.WaitForExitAsync(linkedCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (
            timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            await WaitForExportTerminationAsync(process).ConfigureAwait(false);
            _ = errorTask.ContinueWith(
                task => _ = task.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
            throw new TimeoutException(
                "The clip exporter exceeded its bounded runtime and was stopped.");
        }
        catch
        {
            TryKill(process);
            await WaitForExportTerminationAsync(process).ConfigureAwait(false);
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

    private static async Task WaitForExportTerminationAsync(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                await process.WaitForExitAsync()
                    .WaitAsync(TimeSpan.FromSeconds(2))
                    .ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or ObjectDisposedException or TimeoutException or
                System.ComponentModel.Win32Exception)
        {
            // The process was already contained with an entire-tree kill. The
            // partial remains hidden and best-effort cleanup runs after the
            // protected segment snapshot is released.
        }
    }

    private static CaptureProcessJob? AttachAuxiliaryProcessLifetime(Process process)
    {
        try
        {
            return CaptureProcessJob.Attach(process);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            var processHasExited = false;
            try
            {
                processHasExited = process.HasExited;
            }
            catch (Exception statusException) when (
                statusException is InvalidOperationException or
                    System.ComponentModel.Win32Exception)
            {
                // Preserve the original ownership failure below.
            }

            if (CaptureProcessJob.IsBenignExitedProcessAttachFailure(
                    exception,
                    processHasExited))
            {
                return null;
            }

            TryKill(process);
            throw;
        }
        catch
        {
            // A helper that cannot be placed in a kill-on-close job must not be
            // allowed to outlive ClipForge or retain protected replay segments.
            TryKill(process);
            throw;
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

    private static bool HasProcessExitedSafely(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or
                System.ComponentModel.Win32Exception or
                NotSupportedException)
        {
            return false;
        }
    }

    private async Task RunInitialBufferMaintenanceAsync(
        Func<Task>? initialBufferMaintenanceOverride,
        CancellationToken cancellationToken)
    {
        if (initialBufferMaintenanceOverride is not null)
        {
            await initialBufferMaintenanceOverride().ConfigureAwait(false);
            return;
        }

        // MainWindow creates this service only after primary single-instance
        // ownership is established, so pre-existing sessions in this Windows
        // session are crash residue.
        CleanupStaleBuffers(cancellationToken);
        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        if (!IsDefaultBufferRoot(_bufferRoot) ||
            !TryGetBufferOwnerSessions(
                out var activeOwnerSessionIds,
                out var anotherPotentialOwnerIsRunning))
        {
            return;
        }

        var bufferParent = Path.GetDirectoryName(_bufferRoot);
        if (!cancellationToken.IsCancellationRequested &&
            !string.IsNullOrWhiteSpace(bufferParent))
        {
            _ = CleanupInactiveWindowsSessionBufferRoots(
                bufferParent,
                _bufferRoot,
                DateTime.UtcNow,
                activeOwnerSessionIds,
                ownershipEstablished: true,
                cancellationToken: cancellationToken);
        }

        // Old builds wrote session-* directly below Buffer. Preserve the older,
        // stricter all-owner gate for that unscoped layout.
        if (!cancellationToken.IsCancellationRequested &&
            !anotherPotentialOwnerIsRunning)
        {
            CleanupLegacyStaleBuffers(
                potentialOwnerRunning: false,
                cancellationToken);
        }
    }

    private void CleanupStaleBuffers(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested ||
            !IsSafeBufferRootPath(_bufferRoot))
        {
            return;
        }

        var startedAt = Stopwatch.GetTimestamp();
        var directoriesInspected = 0;
        var filesDeleted = 0;
        try
        {
            foreach (var directoryPath in Directory.EnumerateDirectories(
                         _bufferRoot,
                         "session-*",
                         SearchOption.TopDirectoryOnly))
            {
                if (cancellationToken.IsCancellationRequested ||
                    !ShouldContinueCurrentSessionCleanup(
                        directoriesInspected,
                        filesDeleted,
                        Stopwatch.GetElapsedTime(startedAt)))
                {
                    break;
                }

                directoriesInspected++;
                // Single-instance ownership is established before this service
                // is created, so every pre-existing session is crash residue.
                // Delete only validated top-level replay files and stop at strict
                // work/time limits. A later launch continues any residue instead
                // of making Start Replay wait on an unbounded recursive delete.
                TryDeleteCurrentSessionResidue(
                    directoryPath,
                    startedAt,
                    ref filesDeleted,
                    cancellationToken);
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or
                NotSupportedException or SecurityException)
        {
            // Stale cleanup must never prevent a new capture session.
        }
    }

    internal static bool ShouldContinueCurrentSessionCleanup(
        int directoriesInspected,
        int filesDeleted,
        TimeSpan elapsed) =>
        directoriesInspected < MaximumCurrentSessionCleanupDirectoryCandidatesPerRun &&
        filesDeleted < MaximumCurrentSessionCleanupFilesPerRun &&
        elapsed < CurrentSessionCleanupTimeBudget;

    private void TryDeleteCurrentSessionResidue(
        string directoryPath,
        long startedAt,
        ref int filesDeleted,
        CancellationToken cancellationToken)
    {
        try
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            var directory = new DirectoryInfo(Path.GetFullPath(directoryPath));
            directory.Refresh();
            if (!directory.Exists ||
                !IsSafeBufferDirectoryPath(
                    _bufferRoot,
                    directory.FullName,
                    directory.Attributes))
            {
                return;
            }

            foreach (var entryPath in Directory.EnumerateFileSystemEntries(
                         directory.FullName,
                         "*",
                         SearchOption.TopDirectoryOnly))
            {
                if (cancellationToken.IsCancellationRequested ||
                    !ShouldContinueCurrentSessionCleanup(
                        directoriesInspected: 0,
                        filesDeleted,
                        Stopwatch.GetElapsedTime(startedAt)))
                {
                    return;
                }

                var normalizedEntry = Path.GetFullPath(entryPath);
                var attributes = File.GetAttributes(normalizedEntry);
                if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0 ||
                    !directory.FullName.Equals(
                        Path.GetDirectoryName(normalizedEntry),
                        StringComparison.OrdinalIgnoreCase) ||
                    !IsReplayBufferFileName(Path.GetFileName(normalizedEntry)))
                {
                    // Unexpected or nested content is never followed or removed.
                    return;
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                File.Delete(normalizedEntry);
                filesDeleted++;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            directory.Refresh();
            if (directory.Exists &&
                IsSafeBufferDirectoryPath(
                    _bufferRoot,
                    directory.FullName,
                    directory.Attributes) &&
                !Directory.EnumerateFileSystemEntries(
                    directory.FullName,
                    "*",
                    SearchOption.TopDirectoryOnly).Any())
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                Directory.Delete(directory.FullName, recursive: false);
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or
                NotSupportedException or SecurityException)
        {
            // A later startup can continue or retry this validated residue.
        }
    }

    private void CleanupLegacyStaleBuffers(
        bool potentialOwnerRunning,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        string expectedRoot;
        try
        {
            expectedRoot = Path.GetFullPath(GetDefaultBufferRoot());
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or
                NotSupportedException or SecurityException)
        {
            return;
        }

        // Custom/test roots and every WindowsSession-* sibling are outside this
        // one-time migration. Old ClipForge builds wrote session-* directly
        // below Buffer; current builds never do.
        if (!Path.GetFullPath(_bufferRoot).Equals(
                expectedRoot,
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var legacyRoot = Path.GetDirectoryName(expectedRoot);
        if (string.IsNullOrWhiteSpace(legacyRoot) || potentialOwnerRunning)
        {
            return;
        }

        _ = CleanupLegacyStaleBufferRoot(
            legacyRoot,
            DateTime.UtcNow,
            potentialOwnerRunning: false,
            cancellationToken: cancellationToken);
    }

    private static bool IsDefaultBufferRoot(string bufferRoot)
    {
        try
        {
            return Path.GetFullPath(bufferRoot).Equals(
                Path.GetFullPath(GetDefaultBufferRoot()),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or
                NotSupportedException or SecurityException)
        {
            return false;
        }
    }

    internal static bool IsSafeWindowsSessionBufferRootPath(
        string bufferParent,
        string candidatePath,
        FileAttributes attributes)
    {
        if ((attributes & FileAttributes.Directory) == 0 ||
            (attributes & FileAttributes.ReparsePoint) != 0 ||
            !IsSafeBufferRootPath(bufferParent) ||
            !Path.IsPathFullyQualified(bufferParent) ||
            !Path.IsPathFullyQualified(candidatePath))
        {
            return false;
        }

        try
        {
            var normalizedParent = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(bufferParent));
            var normalizedCandidate = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(candidatePath));
            return normalizedParent.Equals(
                       Path.GetDirectoryName(normalizedCandidate),
                       StringComparison.OrdinalIgnoreCase) &&
                   TryParseWindowsSessionRootName(
                       Path.GetFileName(normalizedCandidate),
                       out _);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or IOException or
                UnauthorizedAccessException or SecurityException)
        {
            return false;
        }
    }

    internal static int CleanupInactiveWindowsSessionBufferRoots(
        string bufferParent,
        string currentBufferRoot,
        DateTime utcNow,
        IReadOnlySet<int> activeOwnerSessionIds,
        bool ownershipEstablished,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bufferParent);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentBufferRoot);
        ArgumentNullException.ThrowIfNull(activeOwnerSessionIds);
        if (cancellationToken.IsCancellationRequested ||
            !ownershipEstablished ||
            !IsSafeBufferRootPath(bufferParent) ||
            !Path.IsPathFullyQualified(currentBufferRoot))
        {
            return 0;
        }

        try
        {
            var normalizedParent = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(bufferParent));
            var normalizedCurrent = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(currentBufferRoot));
            if (!normalizedParent.Equals(
                    Path.GetDirectoryName(normalizedCurrent),
                    StringComparison.OrdinalIgnoreCase) ||
                !TryParseWindowsSessionRootName(
                    Path.GetFileName(normalizedCurrent),
                    out _))
            {
                return 0;
            }

            if (!TryReadWindowsSessionCleanupCursor(
                    normalizedParent,
                    out var scanSuffix))
            {
                return 0;
            }

            var scan = SelectInactiveWindowsSessionBufferRootCandidates(
                normalizedParent,
                normalizedCurrent,
                utcNow,
                activeOwnerSessionIds,
                scanSuffix,
                Directory.EnumerateDirectories(
                    normalizedParent,
                    BuildWindowsSessionCleanupSearchPattern(scanSuffix),
                    SearchOption.TopDirectoryOnly),
                cancellationToken);
            if (cancellationToken.IsCancellationRequested)
            {
                return 0;
            }

            // Persist the next bounded bucket before deleting. A crash can skip
            // this page until the scan wraps, but can never pin every later
            // page behind the same unsafe prefix across process restarts.
            if (cancellationToken.IsCancellationRequested ||
                !TryPersistWindowsSessionCleanupCursor(
                    normalizedParent,
                    scan.NextSuffix))
            {
                return 0;
            }

            var removed = 0;
            foreach (var candidate in scan.Candidates)
            {
                if (cancellationToken.IsCancellationRequested ||
                    removed >= MaximumInactiveWindowsSessionRootsPerRun)
                {
                    break;
                }

                // Re-check process ownership immediately before each deletion.
                // A new process in this Windows session makes the stale snapshot
                // ineligible even if it appeared after enumeration began.
                if (!TryGetBufferOwnerSessions(
                        out var refreshedOwnerSessionIds,
                        out _) ||
                    refreshedOwnerSessionIds.Contains(candidate.SessionId))
                {
                    continue;
                }

                if (TryDeleteValidatedWindowsSessionBufferRoot(
                        normalizedParent,
                        candidate.Path,
                        utcNow,
                        cancellationToken))
                {
                    removed++;
                }
            }

            return removed;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or
                NotSupportedException or SecurityException)
        {
            return 0;
        }
    }

    internal static WindowsSessionCleanupScanResult
        SelectInactiveWindowsSessionBufferRootCandidates(
            string normalizedParent,
            string normalizedCurrent,
            DateTime utcNow,
            IReadOnlySet<int> activeOwnerSessionIds,
            string scanSuffix,
            IEnumerable<string> candidatePaths,
            CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(normalizedParent);
        ArgumentException.ThrowIfNullOrWhiteSpace(normalizedCurrent);
        ArgumentNullException.ThrowIfNull(activeOwnerSessionIds);
        ArgumentNullException.ThrowIfNull(candidatePaths);
        if (!IsValidWindowsSessionCleanupSuffix(scanSuffix))
        {
            throw new ArgumentException(
                "The Windows-session cleanup suffix is invalid.",
                nameof(scanSuffix));
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return new WindowsSessionCleanupScanResult(
                [],
                scanSuffix,
                EnumeratedCount: 0,
                InspectedCount: 0);
        }

        var now = utcNow.ToUniversalTime();
        var enumeratedPathsBuilder = new List<string>(
            MaximumWindowsSessionRootInspectionsPerRun + 1);
        foreach (var candidatePath in candidatePaths)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return new WindowsSessionCleanupScanResult(
                    [],
                    scanSuffix,
                    enumeratedPathsBuilder.Count,
                    InspectedCount: 0);
            }

            enumeratedPathsBuilder.Add(candidatePath);
            if (enumeratedPathsBuilder.Count >
                MaximumWindowsSessionRootInspectionsPerRun)
            {
                break;
            }
        }

        var enumeratedPaths = enumeratedPathsBuilder.ToArray();
        var bucketOverflowed =
            enumeratedPaths.Length > MaximumWindowsSessionRootInspectionsPerRun;
        IReadOnlyList<string> pathsToInspect;
        if (bucketOverflowed)
        {
            // Children "*0{suffix}" through "*9{suffix}" partition every
            // longer decimal session ID. Inspect the exact suffix separately
            // before descending, because it belongs to no child bucket.
            pathsToInspect = scanSuffix.Length == 0
                ? []
                : [Path.Combine(normalizedParent, $"WindowsSession-{scanSuffix}")];
        }
        else
        {
            pathsToInspect = enumeratedPaths;
        }

        var eligibleCandidates = new List<WindowsSessionCleanupCandidate>();
        var inspectedCount = 0;
        foreach (var path in pathsToInspect)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return new WindowsSessionCleanupScanResult(
                    [],
                    scanSuffix,
                    enumeratedPaths.Length,
                    inspectedCount);
            }

            try
            {
                var normalizedCandidate = Path.TrimEndingDirectorySeparator(
                    Path.GetFullPath(path));
                var candidateName = Path.GetFileName(normalizedCandidate);
                if (normalizedCandidate.Equals(
                        normalizedCurrent,
                        StringComparison.OrdinalIgnoreCase) ||
                    !IsWindowsSessionRootInCleanupBucket(
                        candidateName,
                        scanSuffix))
                {
                    continue;
                }

                inspectedCount++;
                if (!TryInspectWindowsSessionBufferRoot(
                        normalizedParent,
                        normalizedCandidate,
                        cancellationToken,
                        out var candidate) ||
                    activeOwnerSessionIds.Contains(candidate.SessionId) ||
                    now - candidate.LatestWriteTimeUtc <
                    LegacyBufferMinimumInactivity)
                {
                    continue;
                }

                eligibleCandidates.Add(candidate);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or
                    ArgumentException or NotSupportedException or SecurityException)
            {
                // One malformed/inaccessible sibling cannot escape the bounded
                // page or hide the next durable bucket.
            }
        }

        var nextSuffix = bucketOverflowed &&
                         scanSuffix.Length <
                         MaximumWindowsSessionCleanupSuffixLength
            ? $"0{scanSuffix}"
            : AdvanceWindowsSessionCleanupSuffix(scanSuffix);
        return new WindowsSessionCleanupScanResult(
            eligibleCandidates
                .OrderBy(item => item.LatestWriteTimeUtc)
                .ThenBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
                .Take(MaximumWindowsSessionRootCandidates)
                .ToArray(),
            nextSuffix,
            enumeratedPaths.Length,
            inspectedCount);
    }

    private static string BuildWindowsSessionCleanupSearchPattern(
        string scanSuffix) =>
        scanSuffix.Length == 0
            ? "WindowsSession-*"
            : $"WindowsSession-*{scanSuffix}";

    private static bool IsWindowsSessionRootInCleanupBucket(
        string name,
        string scanSuffix)
    {
        if (!TryParseWindowsSessionRootName(name, out var sessionId))
        {
            return false;
        }

        return sessionId
            .ToString(CultureInfo.InvariantCulture)
            .EndsWith(scanSuffix, StringComparison.Ordinal);
    }

    private static bool IsValidWindowsSessionCleanupSuffix(string suffix) =>
        suffix.Length <= MaximumWindowsSessionCleanupSuffixLength &&
        suffix.All(character => character is >= '0' and <= '9');

    internal static string AdvanceWindowsSessionCleanupSuffix(string suffix)
    {
        if (!IsValidWindowsSessionCleanupSuffix(suffix))
        {
            throw new ArgumentException(
                "The Windows-session cleanup suffix is invalid.",
                nameof(suffix));
        }

        var current = suffix;
        while (current.Length > 0)
        {
            var childDigit = current[0];
            var parent = current[1..];
            if (childDigit < '9')
            {
                return $"{(char)(childDigit + 1)}{parent}";
            }

            current = parent;
        }

        return string.Empty;
    }

    private static bool TryReadWindowsSessionCleanupCursor(
        string normalizedParent,
        out string scanSuffix)
    {
        scanSuffix = string.Empty;
        try
        {
            var cursorPath = Path.GetFullPath(Path.Combine(
                normalizedParent,
                WindowsSessionCleanupCursorFileName));
            if (!normalizedParent.Equals(
                    Path.GetDirectoryName(cursorPath),
                    StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(cursorPath))
            {
                return true;
            }

            var cursor = new FileInfo(cursorPath);
            cursor.Refresh();
            if (!cursor.Exists ||
                (cursor.Attributes &
                 (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            {
                return false;
            }

            if (cursor.Length > MaximumWindowsSessionCleanupSuffixLength)
            {
                // Treat a corrupt regular cursor as the root bucket. The
                // subsequent atomic write repairs it without trusting content.
                return true;
            }

            using var stream = new FileStream(
                cursorPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 64,
                FileOptions.SequentialScan);
            if (stream.Length > MaximumWindowsSessionCleanupSuffixLength)
            {
                return true;
            }

            var bytes = new byte[checked((int)stream.Length)];
            stream.ReadExactly(bytes);
            var persisted = Encoding.ASCII.GetString(bytes);
            if (IsValidWindowsSessionCleanupSuffix(persisted))
            {
                scanSuffix = persisted;
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

    private static bool TryPersistWindowsSessionCleanupCursor(
        string normalizedParent,
        string scanSuffix)
    {
        if (!IsValidWindowsSessionCleanupSuffix(scanSuffix) ||
            !IsSafeBufferRootPath(normalizedParent))
        {
            return false;
        }

        string? temporaryPath = null;
        try
        {
            var cursorPath = Path.GetFullPath(Path.Combine(
                normalizedParent,
                WindowsSessionCleanupCursorFileName));
            if (!normalizedParent.Equals(
                    Path.GetDirectoryName(cursorPath),
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (File.Exists(cursorPath))
            {
                var attributes = File.GetAttributes(cursorPath);
                if ((attributes &
                     (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                {
                    return false;
                }
            }

            temporaryPath = Path.Combine(
                normalizedParent,
                $".windows-session-cleanup-{Guid.NewGuid():N}.tmp");
            var bytes = Encoding.ASCII.GetBytes(scanSuffix);
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 64,
                       FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, cursorPath, overwrite: true);
            temporaryPath = null;
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or
                NotSupportedException or SecurityException)
        {
            return false;
        }
        finally
        {
            if (temporaryPath is not null)
            {
                TryDeleteFile(temporaryPath);
            }
        }
    }

    private static bool TryInspectWindowsSessionBufferRoot(
        string bufferParent,
        string candidatePath,
        CancellationToken cancellationToken,
        out WindowsSessionCleanupCandidate candidate)
    {
        candidate = default;
        try
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return false;
            }

            var root = new DirectoryInfo(Path.GetFullPath(candidatePath));
            root.Refresh();
            if (!root.Exists ||
                !IsSafeWindowsSessionBufferRootPath(
                    bufferParent,
                    root.FullName,
                    root.Attributes) ||
                !TryParseWindowsSessionRootName(root.Name, out var sessionId) ||
                !TryCollectWindowsSessionBufferEntries(
                    root,
                    cancellationToken,
                    out _,
                    out _,
                    out var latestWriteTimeUtc))
            {
                return false;
            }

            candidate = new WindowsSessionCleanupCandidate(
                root.FullName,
                sessionId,
                latestWriteTimeUtc);
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or
                NotSupportedException or SecurityException)
        {
            return false;
        }
    }

    private static bool TryCollectWindowsSessionBufferEntries(
        DirectoryInfo root,
        CancellationToken cancellationToken,
        out string[] sessionDirectories,
        out string[] bufferFiles,
        out DateTime latestWriteTimeUtc)
    {
        sessionDirectories = [];
        bufferFiles = [];
        latestWriteTimeUtc = root.LastWriteTimeUtc;
        var rootEntriesBuilder = new List<string>(
            MaximumWindowsSessionDirectoriesPerRoot + 1);
        foreach (var rootEntry in Directory.EnumerateFileSystemEntries(
                     root.FullName,
                     "*",
                     SearchOption.TopDirectoryOnly))
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return false;
            }

            rootEntriesBuilder.Add(rootEntry);
            if (rootEntriesBuilder.Count >
                MaximumWindowsSessionDirectoriesPerRoot)
            {
                break;
            }
        }

        var rootEntries = rootEntriesBuilder.ToArray();
        if (rootEntries.Length > MaximumWindowsSessionDirectoriesPerRoot)
        {
            return false;
        }

        var sessions = new List<string>(rootEntries.Length);
        var files = new List<string>();
        foreach (var sessionPath in rootEntries)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return false;
            }

            var session = new DirectoryInfo(Path.GetFullPath(sessionPath));
            session.Refresh();
            if (!session.Exists ||
                !IsSafeBufferDirectoryPath(
                    root.FullName,
                    session.FullName,
                    session.Attributes))
            {
                return false;
            }

            sessions.Add(session.FullName);
            latestWriteTimeUtc = Later(latestWriteTimeUtc, session.LastWriteTimeUtc);
            var entriesBuilder = new List<string>(
                MaximumWindowsSessionFilesPerDirectory + 1);
            foreach (var entry in Directory.EnumerateFileSystemEntries(
                         session.FullName,
                         "*",
                         SearchOption.TopDirectoryOnly))
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return false;
                }

                entriesBuilder.Add(entry);
                if (entriesBuilder.Count >
                    MaximumWindowsSessionFilesPerDirectory)
                {
                    break;
                }
            }

            var entries = entriesBuilder.ToArray();
            if (entries.Length > MaximumWindowsSessionFilesPerDirectory)
            {
                return false;
            }

            foreach (var entryPath in entries)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return false;
                }

                var normalizedEntry = Path.GetFullPath(entryPath);
                var attributes = File.GetAttributes(normalizedEntry);
                if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0 ||
                    !session.FullName.Equals(
                        Path.GetDirectoryName(normalizedEntry),
                        StringComparison.OrdinalIgnoreCase) ||
                    !IsReplayBufferFileName(Path.GetFileName(normalizedEntry)))
                {
                    return false;
                }

                files.Add(normalizedEntry);
                latestWriteTimeUtc = Later(
                    latestWriteTimeUtc,
                    File.GetLastWriteTimeUtc(normalizedEntry));
            }
        }

        sessionDirectories = sessions.ToArray();
        bufferFiles = files.ToArray();
        return true;
    }

    private static bool TryDeleteValidatedWindowsSessionBufferRoot(
        string bufferParent,
        string candidatePath,
        DateTime utcNow,
        CancellationToken cancellationToken)
    {
        try
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return false;
            }

            var root = new DirectoryInfo(Path.GetFullPath(candidatePath));
            root.Refresh();
            if (!root.Exists ||
                !IsSafeWindowsSessionBufferRootPath(
                    bufferParent,
                    root.FullName,
                    root.Attributes) ||
                !TryCollectWindowsSessionBufferEntries(
                    root,
                    cancellationToken,
                    out var sessionDirectories,
                    out var bufferFiles,
                    out var latestWriteTimeUtc) ||
                utcNow.ToUniversalTime() - latestWriteTimeUtc <
                LegacyBufferMinimumInactivity)
            {
                return false;
            }

            foreach (var file in bufferFiles)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return false;
                }

                var parentPath = Path.GetDirectoryName(file);
                if (string.IsNullOrWhiteSpace(parentPath))
                {
                    return false;
                }

                var parent = new DirectoryInfo(parentPath);
                parent.Refresh();
                var attributes = File.GetAttributes(file);
                if (!parent.Exists ||
                    !IsSafeBufferDirectoryPath(
                        root.FullName,
                        parent.FullName,
                        parent.Attributes) ||
                    (attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0 ||
                    !IsReplayBufferFileName(Path.GetFileName(file)))
                {
                    return false;
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    return false;
                }

                File.Delete(file);
            }

            foreach (var sessionPath in sessionDirectories)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return false;
                }

                var session = new DirectoryInfo(sessionPath);
                session.Refresh();
                if (!session.Exists ||
                    !IsSafeBufferDirectoryPath(
                        root.FullName,
                        session.FullName,
                        session.Attributes) ||
                    Directory.EnumerateFileSystemEntries(
                        session.FullName,
                        "*",
                        SearchOption.TopDirectoryOnly).Any())
                {
                    return false;
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    return false;
                }

                Directory.Delete(session.FullName, recursive: false);
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return false;
            }

            root.Refresh();
            if (!root.Exists ||
                !IsSafeWindowsSessionBufferRootPath(
                    bufferParent,
                    root.FullName,
                    root.Attributes) ||
                Directory.EnumerateFileSystemEntries(
                    root.FullName,
                    "*",
                    SearchOption.TopDirectoryOnly).Any())
            {
                return false;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return false;
            }

            Directory.Delete(root.FullName, recursive: false);
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or
                NotSupportedException or SecurityException)
        {
            return false;
        }
    }

    private static bool TryParseWindowsSessionRootName(
        string name,
        out int sessionId)
    {
        const string prefix = "WindowsSession-";
        sessionId = -1;
        if (!name.StartsWith(prefix, StringComparison.Ordinal) ||
            !int.TryParse(
                name.AsSpan(prefix.Length),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out sessionId) ||
            sessionId < 0)
        {
            return false;
        }

        return name.Equals(
            string.Create(
                CultureInfo.InvariantCulture,
                $"{prefix}{sessionId}"),
            StringComparison.Ordinal);
    }

    private static bool IsReplayBufferFileName(string name)
    {
        const string segmentPrefix = "segment-";
        const string segmentExtension = ".mkv";
        if (name.StartsWith(segmentPrefix, StringComparison.Ordinal) &&
            name.EndsWith(segmentExtension, StringComparison.Ordinal) &&
            name.Length == segmentPrefix.Length + 9 + segmentExtension.Length)
        {
            return int.TryParse(
                name.AsSpan(segmentPrefix.Length, 9),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out _);
        }

        const string exportPrefix = "export-";
        const string exportExtension = ".txt";
        if (!name.StartsWith(exportPrefix, StringComparison.Ordinal) ||
            !name.EndsWith(exportExtension, StringComparison.Ordinal) ||
            name.Length != exportPrefix.Length + 32 + exportExtension.Length)
        {
            return false;
        }

        return name.AsSpan(exportPrefix.Length, 32)
            .IndexOfAnyExcept(
                "0123456789abcdefABCDEF".AsSpan()) < 0;
    }

    private static DateTime Later(DateTime first, DateTime second) =>
        first >= second ? first : second;

    /// <summary>
    /// Deletes only the obsolete pre-WindowsSession layout. Cleanup is
    /// deliberately conservative and bounded: active-process evidence,
    /// recently-written folders, nested content, reparse points, unexpected
    /// filenames, and oversized trees all leave the candidate untouched.
    /// </summary>
    internal static int CleanupLegacyStaleBufferRoot(
        string legacyRoot,
        DateTime utcNow,
        bool potentialOwnerRunning,
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested ||
            potentialOwnerRunning ||
            !IsSafeBufferRootPath(legacyRoot))
        {
            return 0;
        }

        try
        {
            var candidatesBuilder = new List<DirectoryInfo>(
                MaximumLegacyDirectoryCandidates);
            foreach (var path in Directory.EnumerateDirectories(
                         legacyRoot,
                         "session-*",
                         SearchOption.TopDirectoryOnly))
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return 0;
                }

                var directory = new DirectoryInfo(path);
                if (directory.Exists &&
                    IsSafeBufferDirectoryPath(
                        legacyRoot,
                        directory.FullName,
                        directory.Attributes) &&
                    utcNow.ToUniversalTime() - directory.LastWriteTimeUtc >=
                    LegacyBufferMinimumInactivity)
                {
                    candidatesBuilder.Add(directory);
                }

                if (candidatesBuilder.Count >= MaximumLegacyDirectoryCandidates)
                {
                    break;
                }
            }

            var candidates = candidatesBuilder
                .OrderBy(directory => directory.LastWriteTimeUtc)
                .Take(MaximumLegacyCleanupDirectories)
                .ToArray();

            var removed = 0;
            foreach (var candidate in candidates)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                if (TryDeleteValidatedLegacyBufferDirectory(
                        legacyRoot,
                        candidate,
                        cancellationToken))
                {
                    removed++;
                }
            }

            return removed;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or
                NotSupportedException or SecurityException)
        {
            return 0;
        }
    }

    private static bool TryDeleteValidatedLegacyBufferDirectory(
        string legacyRoot,
        DirectoryInfo candidate,
        CancellationToken cancellationToken)
    {
        try
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return false;
            }

            candidate.Refresh();
            if (!candidate.Exists ||
                !IsSafeBufferDirectoryPath(
                    legacyRoot,
                    candidate.FullName,
                    candidate.Attributes))
            {
                return false;
            }

            var entriesBuilder = new List<string>(
                MaximumLegacyCleanupFilesPerDirectory + 1);
            foreach (var entry in Directory.EnumerateFileSystemEntries(
                         candidate.FullName,
                         "*",
                         SearchOption.TopDirectoryOnly))
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return false;
                }

                entriesBuilder.Add(entry);
                if (entriesBuilder.Count >
                    MaximumLegacyCleanupFilesPerDirectory)
                {
                    break;
                }
            }

            var entries = entriesBuilder.ToArray();
            if (entries.Length > MaximumLegacyCleanupFilesPerDirectory)
            {
                return false;
            }

            var normalizedCandidate = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(candidate.FullName));
            foreach (var entry in entries)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return false;
                }

                var normalizedEntry = Path.GetFullPath(entry);
                var attributes = File.GetAttributes(normalizedEntry);
                var name = Path.GetFileName(normalizedEntry);
                if (!normalizedCandidate.Equals(
                        Path.GetDirectoryName(normalizedEntry),
                        StringComparison.OrdinalIgnoreCase) ||
                    (attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0 ||
                    !IsLegacyBufferFileName(name))
                {
                    return false;
                }
            }

            foreach (var entry in entries)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return false;
                }

                var attributes = File.GetAttributes(entry);
                if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                {
                    return false;
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    return false;
                }

                File.Delete(entry);
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return false;
            }

            candidate.Refresh();
            if (!candidate.Exists ||
                !IsSafeBufferDirectoryPath(
                    legacyRoot,
                    candidate.FullName,
                    candidate.Attributes))
            {
                return false;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return false;
            }

            Directory.Delete(candidate.FullName, recursive: false);
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or
                NotSupportedException or SecurityException)
        {
            return false;
        }
    }

    private static bool IsLegacyBufferFileName(string name) =>
        name.StartsWith("segment-", StringComparison.OrdinalIgnoreCase) &&
        name.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("export-", StringComparison.OrdinalIgnoreCase) &&
        name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase);

    private static bool TryGetBufferOwnerSessions(
        out HashSet<int> activeSessionIds,
        out bool anotherPotentialOwnerIsRunning)
    {
        activeSessionIds = [];
        anotherPotentialOwnerIsRunning = false;
        using var current = Process.GetCurrentProcess();
        foreach (var processName in new[] { "ClipForge", "ffmpeg" })
        {
            Process[] processes;
            try
            {
                processes = Process.GetProcessesByName(processName);
            }
            catch (Exception exception) when (
                exception is InvalidOperationException or System.ComponentModel.Win32Exception or
                    NotSupportedException)
            {
                // Fail closed: if process ownership cannot be established, leave
                // session-scoped and legacy buffers untouched for a later startup.
                activeSessionIds.Clear();
                anotherPotentialOwnerIsRunning = true;
                return false;
            }

            foreach (var process in processes)
            {
                using (process)
                {
                    try
                    {
                        activeSessionIds.Add(process.SessionId);
                        if (process.Id != current.Id)
                        {
                            anotherPotentialOwnerIsRunning = true;
                        }
                    }
                    catch (Exception exception) when (
                        exception is InvalidOperationException or System.ComponentModel.Win32Exception or
                            NotSupportedException)
                    {
                        // Fail closed. A racing or inaccessible process may still
                        // own a replay buffer in the session being inspected.
                        activeSessionIds.Clear();
                        anotherPotentialOwnerIsRunning = true;
                        return false;
                    }
                }
            }
        }

        return true;
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
        lock (_statePublicationGate)
        {
            long publicationVersion;
            lock (_stateGate)
            {
                // A save-buffer refresh can race the monitor's terminal transition.
                // Once capture is no longer running, never replace an already
                // published terminal state with Ready/Buffering/Saving; only carry
                // the successful export path forward.
                if (!IsRunning &&
                    (snapshot.State is ReplayState.Ready or
                        ReplayState.Buffering or
                        ReplayState.Saving) &&
                    (_state.State is ReplayState.Faulted or ReplayState.Stopped))
                {
                    snapshot = BuildPostSaveSnapshot(
                        _state,
                        snapshot.LastSavedPath);
                }

                _state = snapshot;
                publicationVersion = Interlocked.Increment(
                    ref _statePublicationVersion);
            }

            var handlers = StateChanged;
            if (handlers is null)
            {
                return;
            }

            foreach (EventHandler<ReplayStateSnapshot> handler in handlers.GetInvocationList())
            {
                if (publicationVersion != Volatile.Read(ref _statePublicationVersion))
                {
                    // A re-entrant subscriber published a newer state. Never
                    // deliver the superseded snapshot to later subscribers.
                    break;
                }

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
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    private readonly record struct CaptureProgressEnvelope(
        int ProcessId,
        CaptureProgressSample Sample,
        CaptureForegroundContext ForegroundContext);

    private readonly record struct DiscontinuousRefreshTicket(
        long Epoch,
        int SessionIdentity);

    private sealed record CaptureRecoveryRetryState(
        int CurrentGeneration,
        int TaskGeneration,
        long NotBeforeTimestamp,
        Task Task);

    internal enum DiscontinuousRefreshReadiness
    {
        Terminal,
        Wait,
        Actionable
    }

    private readonly record struct BufferedSegment(
        string Path,
        long Length,
        int SegmentNumber,
        int GenerationId,
        bool IsTrusted);

    private sealed class RetryableCaptureLaunchException(
        string message,
        Exception innerException)
        : Exception(message, innerException);

    internal readonly record struct WindowsSessionCleanupCandidate(
        string Path,
        int SessionId,
        DateTime LatestWriteTimeUtc);

    internal readonly record struct WindowsSessionCleanupScanResult(
        IReadOnlyList<WindowsSessionCleanupCandidate> Candidates,
        string NextSuffix,
        int EnumeratedCount,
        int InspectedCount);
}

internal readonly struct ReplayThreadPoolHopAwaitable
{
    public Awaiter GetAwaiter() => default;

    internal readonly struct Awaiter : ICriticalNotifyCompletion
    {
        public bool IsCompleted => false;

        public void GetResult()
        {
        }

        public void OnCompleted(Action continuation) =>
            UnsafeOnCompleted(continuation);

        public void UnsafeOnCompleted(Action continuation)
        {
            ArgumentNullException.ThrowIfNull(continuation);
            ThreadPool.UnsafeQueueUserWorkItem(
                static callback => callback(),
                continuation,
                preferLocal: false);
        }
    }
}

internal readonly record struct ReplayCaptureGenerationSnapshot(
    int GenerationId,
    int QuarantinedHeadSegmentNumber);

/// <summary>
/// Owns one scheduled capture-maintenance operation on the thread pool. The
/// service supplies the PID-checked refresh callback; this coordinator only
/// provides non-overlap, dispatcher independence, and bounded shutdown.
/// </summary>
internal sealed class ScheduledCaptureRefreshCoordinator : IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly Func<int, string, CancellationToken, Task> _refreshAsync;
    private readonly Action<int, string, Exception>? _failureHandler;
    private readonly CancellationTokenSource _shutdown = new();
    private Task _worker = Task.CompletedTask;
    private bool _sealed;

    public ScheduledCaptureRefreshCoordinator(
        Func<int, string, CancellationToken, Task> refreshAsync,
        Action<int, string, Exception>? failureHandler = null)
    {
        _refreshAsync = refreshAsync ?? throw new ArgumentNullException(nameof(refreshAsync));
        _failureHandler = failureHandler;
    }

    internal bool IsActive
    {
        get
        {
            lock (_sync)
            {
                return !_worker.IsCompleted;
            }
        }
    }

    public bool TrySchedule(int expectedProcessId, string diagnostic)
    {
        if (expectedProcessId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedProcessId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(diagnostic);
        lock (_sync)
        {
            if (_sealed || !_worker.IsCompleted)
            {
                return false;
            }

            var cancellationToken = _shutdown.Token;
            _worker = Task.Run(
                () => RunWorkerAsync(
                    expectedProcessId,
                    diagnostic,
                    cancellationToken),
                CancellationToken.None);
            return true;
        }
    }

    internal Task WaitForIdleAsync()
    {
        lock (_sync)
        {
            return _worker;
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task worker;
        var cancel = false;
        lock (_sync)
        {
            if (!_sealed)
            {
                _sealed = true;
                cancel = true;
            }

            worker = _worker;
        }

        if (!cancel)
        {
            await worker.ConfigureAwait(false);
            return;
        }

        _shutdown.Cancel();
        await worker.ConfigureAwait(false);
        _shutdown.Dispose();
    }

    private async Task RunWorkerAsync(
        int expectedProcessId,
        string diagnostic,
        CancellationToken cancellationToken)
    {
        try
        {
            await _refreshAsync(expectedProcessId, diagnostic, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Disposal cancels a queued save-gate wait or an in-flight refresh.
        }
        catch (Exception exception)
        {
            try
            {
                _failureHandler?.Invoke(expectedProcessId, diagnostic, exception);
            }
            catch
            {
                // Diagnostics must not fault the maintenance coordinator.
            }
        }
    }
}
