using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Security;
using System.Text;
using System.Text.Json;
using ClipForge.Capture;
using ClipForge.Models;
using Microsoft.Win32.SafeHandles;

namespace ClipForge.Services;

/// <summary>
/// Owns FFmpeg's continuous capture process, prunes the disk-backed replay ring,
/// and maintains Recorder's directly publishable MP4 plus recovery segments.
/// </summary>
public sealed class ReplayBufferService : IAsyncDisposable
{
    private const int MaximumDiagnosticLines = 60;
    private const int MaximumDiagnosticLineCharacters = 1000;
    private const uint FileReadAttributes = 0x00000080;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint FileShareDelete = 0x00000004;
    private const uint OpenExisting = 3;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileNameNormalized = 0;
    private const int MaximumFinalPathCharacters = 32_768;
    private const string RecordingRecoveryStateFileName =
        ".clipforge-recording-recovery.json";
    private const string RecordingRecoveryConcatFileName =
        ".clipforge-recording-recovery.ffconcat";
    private const string LiveRecordingFileName =
        ".clipforge-live-recording.mp4";
    private const long MaximumRecordingRecoveryStateBytes = 32L * 1024 * 1024;
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
    private const string ObjectiveGdiReprobeDiagnosticPrefix =
        "[objective-gdi-starvation] ";
    // Windows Graphics Capture frame pools can lose delivery cadence after a
    // long, uninterrupted desktop session while FFmpeg itself remains alive.
    // Instant Replay renews at this bounded age. Recorder keeps its healthy
    // process so one direct MP4 remains publishable, while the cadence/hang
    // watchdog still performs an evidence-driven recovery when needed.
    internal static readonly TimeSpan CaptureProcessMaximumAge = TimeSpan.FromMinutes(30);

    private readonly FfmpegSetupService _ffmpegSetupService;
    private readonly FfmpegCapabilityProbe _capabilityProbe = new();
    private readonly VideoEncodingStrategy? _captureStrategyOverride;
    private readonly string _bufferRoot;
    private readonly string _recordingRecoveryRoot;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly object _fileGate = new();
    private readonly object _stateGate = new();
    private readonly object _statePublicationGate = new();
    private readonly object _diagnosticGate = new();
    private readonly object _discontinuousRefreshGate = new();
    private readonly object _captureRecoveryRetryGate = new();
    private readonly object _recordingCleanupGate = new();
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
    private string? _activeBufferRoot;
    private string? _liveRecordingPath;
    private TimeSpan _retention = TimeSpan.FromMinutes(2);
    private ReplayStateSnapshot _state = new(
        ReplayState.Stopped,
        TimeSpan.Zero,
        TimeSpan.FromMinutes(2),
        0);
    private string? _lastSavedPath;
    private string? _activeEncoderDescription;
    private CaptureConfiguration? _activeConfiguration;
    private CaptureSessionMode _sessionMode = CaptureSessionMode.InstantReplay;
    private DetachedRecordingBuffer? _pendingDetachedRecordingBuffer;
    private RecordingRecoveryJournal? _recordingRecoveryJournal;
    private VideoEncodingStrategy? _activeCaptureStrategy;
    private CapturePerformanceProfile _activeCapturePerformanceProfile =
        CapturePerformanceProfile.LowImpact;
    private string? _activeFfmpegPath;
    private CaptureSessionPlan? _lastCapturePlan;
    private CaptureProgressSample? _latestCaptureProgress;
    private CaptureProgressSample? _lastStoppedCaptureProgress;
    private CaptureStarvationWatchdog? _captureStarvationWatchdog;
    private long _bufferBytes;
    private long _reportedDroppedAudioBlocks;
    private int _nextSegmentNumber;
    private int _activeCaptureGeneration;
    private int _activeCaptureGenerationStartSegmentNumber = -1;
    private int _completedTrustedRecordingSegments;
    private int _recordingTailInvalidatedGeneration = -1;
    private int _recordingTailInvalidatedThroughSegmentNumber = -1;
    private int _quarantinedGenerationHeadSegmentNumber = -1;
    private int _exportBlockedCaptureGeneration = -1;
    private CancellationTokenSource? _activeSaveInvalidation;
    private int _activeReplaySaveStorageSafetyCancellation;
    private string? _activeReplaySaveDirectory;
    private long _activeReplaySaveIdentity;
    private long _replaySaveIdentitySequence;
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
    private int _liveRecordingFastPathEligible;
    private int _lastCaptureStopWasGraceful;
    private long _discontinuousRefreshRequestedEpoch;
    private long _discontinuousRefreshCompletedEpoch;
    private int _discontinuousRefreshSessionIdentity;
    private string _discontinuousRefreshDiagnostic = string.Empty;
    private int _discontinuousRefreshContinuationScheduled;
    private Task _discontinuousRefreshContinuationTask = Task.CompletedTask;
    private Task _recordingCleanupTask = Task.CompletedTask;

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
        _recordingRecoveryRoot = RecordingRecoveryJournal.GetRecoveryRoot(_bufferRoot);
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
                var objectiveGdiStarvation =
                    IsObjectiveGdiReprobeDiagnostic(diagnostic);
                var cleanDiagnostic =
                    StripObjectiveGdiReprobeDiagnosticPrefix(diagnostic);
                EnqueueDiagnostic(
                    $"Background degraded-capture reprobe for process {processId} failed: " +
                    $"{cleanDiagnostic} {exception.GetBaseException().Message}");
                if (objectiveGdiStarvation)
                {
                    RequestCaptureRecovery(
                        CaptureRecoveryReason.SourceStarvation,
                        $"The focused WGC recovery check failed while GDI cadence was objectively starved. " +
                        exception.GetBaseException().Message);
                }
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

    public CaptureSessionMode ActiveSessionMode => _sessionMode;

    internal int ActiveSessionIdentity =>
        Volatile.Read(ref _captureSessionIdentity);

    internal string ActiveBufferRoot =>
        Volatile.Read(ref _activeBufferRoot) ?? _bufferRoot;

    internal string? ActiveReplaySaveDirectory =>
        Volatile.Read(ref _activeReplaySaveDirectory);

    internal long ActiveReplaySaveIdentity =>
        Volatile.Read(ref _activeReplaySaveIdentity);

    internal string ResolveReplayBufferRoot(string saveDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(saveDirectory);
        try
        {
            var savePath = Path.GetFullPath(saveDirectory);
            if (!savePath.StartsWith(@"\\", StringComparison.Ordinal))
            {
                var root = Path.GetPathRoot(savePath);
                if (!string.IsNullOrWhiteSpace(root) &&
                    new DriveInfo(root).DriveType == DriveType.Fixed)
                {
                    using var process = Process.GetCurrentProcess();
                    var preferredRoot = Path.Combine(
                        savePath,
                        ".clipforge-replay-buffer",
                        $"WindowsSession-{process.SessionId}");
                    if (IsSafeBufferRootPath(preferredRoot))
                    {
                        return preferredRoot;
                    }
                }
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                ArgumentException or NotSupportedException or SecurityException)
        {
            // Network/removable/unavailable destinations keep the isolated
            // LocalAppData fallback. The caller still checks that volume.
        }

        return _bufferRoot;
    }

    public bool HasPendingRecording =>
        Volatile.Read(ref _pendingDetachedRecordingBuffer) is not null;

    public bool PendingRecordingHasSafeSegments =>
        HasSafeRecordingSource(
            Volatile.Read(ref _pendingDetachedRecordingBuffer));

    public bool CanDiscardIncompleteRecording =>
        Volatile.Read(ref _pendingDetachedRecordingBuffer) is
        { SourceAvailable: true } pending &&
        !HasSafeRecordingSource(pending);

    private static bool HasSafeRecordingSource(
        DetachedRecordingBuffer? pending) =>
        pending is not null &&
        HasUsableRecordingSourceMetadata(
            pending.SegmentPaths.Count,
            pending.LiveRecordingPath,
            pending.LiveRecordingExpectedDuration,
            pending.LiveRecordingFastPathEligible,
            pending.LiveRecordingRecoveryPath);

    internal static bool HasUsableRecordingSourceMetadata(
        int segmentCount,
        string? liveRecordingPath,
        TimeSpan? liveRecordingExpectedDuration,
        bool liveRecordingFastPathEligible,
        string? liveRecordingRecoveryPath) =>
        segmentCount > 0 ||
        liveRecordingFastPathEligible &&
        liveRecordingExpectedDuration is { } expectedDuration &&
        expectedDuration > TimeSpan.Zero &&
        !string.IsNullOrWhiteSpace(liveRecordingPath) ||
        !string.IsNullOrWhiteSpace(liveRecordingRecoveryPath);

    public bool HasUnfinishedRecording =>
        HasPendingRecording ||
        !string.IsNullOrWhiteSpace(Volatile.Read(ref _segmentDirectory));

    public string? PendingRecordingDirectory =>
        Volatile.Read(ref _pendingDetachedRecordingBuffer)?.SessionDirectory;

    internal long RecordingRecoverySegmentBytes
    {
        get
        {
            lock (_fileGate)
            {
                return Math.Max(0, _bufferBytes);
            }
        }
    }

    internal async Task<bool> UpdateActiveDisplayForRefreshAsync(
        DisplayOption display,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(display);
        ThrowIfDisposed();
        await _saveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (!IsRunning || _activeConfiguration is not { } configuration ||
                    !string.Equals(
                        configuration.Display.DeviceName,
                        display.DeviceName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                _activeConfiguration = configuration with { Display = display };
                if (_lastCapturePlan is { } plan)
                {
                    Volatile.Write(
                        ref _lastCapturePlan,
                        plan with { Display = display });
                }

                return true;
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
        TimeSpan processUptime,
        CaptureSessionMode sessionMode = CaptureSessionMode.InstantReplay,
        bool recordingFastPathEligible = true) =>
        captureBackend == DesktopCaptureBackend.WindowsGraphicsCapture &&
        processUptime >= CaptureProcessMaximumAge &&
        (sessionMode == CaptureSessionMode.InstantReplay ||
         sessionMode == CaptureSessionMode.Recording &&
         !recordingFastPathEligible);

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

                if (_pendingDetachedRecordingBuffer is not null)
                {
                    throw new InvalidOperationException(
                        "A stopped Recorder session is waiting to be saved. Retry Stop & save before starting another capture.");
                }

                if (_sessionMode == CaptureSessionMode.Recording &&
                    !string.IsNullOrWhiteSpace(_segmentDirectory))
                {
                    throw new InvalidOperationException(
                        "Recorder still owns an unfinished session. Stop and save or preserve it before starting another capture.");
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
                _sessionMode = configuration.SessionMode;
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

                var sessionBufferRoot = configuration.SessionMode ==
                    CaptureSessionMode.Recording
                    ? RecordingStoragePolicy.GetWorkingRoot(
                        configuration.SaveDirectory)
                    : ResolveReplayBufferRoot(configuration.SaveDirectory);
                EnsureSafeBufferRoot(sessionBufferRoot);
                Directory.CreateDirectory(sessionBufferRoot);
                EnsureSafeBufferRoot(sessionBufferRoot);
                if (configuration.SessionMode == CaptureSessionMode.InstantReplay &&
                    !sessionBufferRoot.Equals(
                        _bufferRoot,
                        StringComparison.OrdinalIgnoreCase))
                {
                    // Single-instance ownership is already established. Any
                    // session-* directories left on the selected clip drive are
                    // crash residue from an earlier process.
                    CleanupStaleBuffers(sessionBufferRoot, cancellationToken);
                }
                _activeBufferRoot = sessionBufferRoot;
                _segmentDirectory = Path.Combine(
                    sessionBufferRoot,
                    $"session-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}");
                EnsureSafeBufferRoot(sessionBufferRoot);
                Directory.CreateDirectory(_segmentDirectory);
                var sessionInfo = new DirectoryInfo(_segmentDirectory);
                if (!sessionInfo.Exists ||
                    !IsSafeBufferDirectoryPath(
                        sessionBufferRoot,
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

                var directRecordingBasePath = configuration.SessionMode ==
                    CaptureSessionMode.Recording
                    ? Path.Combine(_segmentDirectory, LiveRecordingFileName)
                    : null;
                _liveRecordingPath = directRecordingBasePath;
                Volatile.Write(
                    ref _liveRecordingFastPathEligible,
                    _liveRecordingPath is null ? 0 : 1);

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
                    !SupportsGraphicsCaptureRecovery(
                        capabilitySelection.Strategy.CaptureBackend))
                {
                    EnqueueDiagnostic(capabilitySelection.Diagnostics);
                    throw new InvalidOperationException(
                        "ClipForge could not safely verify GPU desktop capture at the display's " +
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

                if (configuration.SessionMode == CaptureSessionMode.Recording)
                {
                    _recordingRecoveryJournal =
                        await RecordingRecoveryJournal.CreateAsync(
                                _recordingRecoveryRoot,
                                _segmentDirectory,
                                sessionBufferRoot,
                                configuration.FramesPerSecond,
                                configuration.CaptureSystemAudio ||
                                configuration.CaptureMicrophone,
                                segmentDurationSeconds:
                                    FfmpegArgumentBuilder.RecordingRecoverySegmentSeconds,
                                // Permanent fMP4 remains demuxable up to its
                                // last completed fragment after a hard kill. The
                                // journal may therefore offer it for bounded
                                // repair, while completed MKVs remain the
                                // authoritative fallback if validation fails.
                                liveRecordingPath: _liveRecordingPath,
                                cancellationToken)
                            .ConfigureAwait(false);
                }

                CreateAudioPipes(configuration);
                var arguments = FfmpegArgumentBuilder.BuildCaptureArguments(
                    configuration,
                    _audioPipes.Select(pipe => pipe.Specification).ToArray(),
                    capabilitySelection.Strategy,
                    _segmentDirectory,
                    performanceProfile: selectedPerformanceProfile,
                    directRecordingPath: directRecordingBasePath);

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
                        CaptureGeometry.ResolveOutputSize(configuration).RequiresScaling,
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
                        CaptureGeometry.ResolveOutputSize(configuration).RequiresScaling,
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
                // Replay is disposable by design; an all-session recording is
                // not. Generic lifecycle paths (shutdown, display loss, fault
                // cleanup) must preserve Recorder segments unless a validated
                // final MP4 has already been committed.
                var preserveRecording =
                    _sessionMode == CaptureSessionMode.Recording &&
                    !string.IsNullOrWhiteSpace(_segmentDirectory);
                var detachedRecording = await StopCoreAsync(
                        deleteBuffer: !preserveRecording,
                        publishStopped: true,
                        detachRecordingBuffer: preserveRecording)
                    .ConfigureAwait(false);
                if (detachedRecording is not null)
                {
                    Volatile.Write(
                        ref _pendingDetachedRecordingBuffer,
                        detachedRecording);
                    _ = await PersistRecordingRecoveryStateAsync(
                            detachedRecording,
                            CancellationToken.None)
                        .ConfigureAwait(false);
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

    public async Task<bool> TryLoadPendingRecordingAsync(
        string saveDirectory,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(saveDirectory);
        await SwitchToThreadPool();
        await _saveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_pendingDetachedRecordingBuffer is not null)
                {
                    return true;
                }

                if (IsRunning)
                {
                    return false;
                }

                foreach (var journalPath in
                         RecordingRecoveryJournal.EnumerateCandidatePathPages(
                                 _recordingRecoveryRoot)
                             .SelectMany(static page => page))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var recovery = await RecordingRecoveryJournal.TryReadAsync(
                            _recordingRecoveryRoot,
                            journalPath,
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (recovery is null)
                    {
                        continue;
                    }

                    recovery = await ResolveRecoveryLocatorFamilyAsync(
                            recovery,
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (recovery is null)
                    {
                        continue;
                    }

                    if (recovery.Superseded)
                    {
                        // Supersession is the durable authority. Physical
                        // removal is only garbage collection and may be retried
                        // after a sharing violation without reviving a capture.
                        RecordingRecoveryJournal.TryDeleteJournal(
                            recovery.JournalPath);
                        continue;
                    }

                    if (await TrySupersedeStaleRecoveryLocatorAsync(recovery)
                            .ConfigureAwait(false))
                    {
                        continue;
                    }

                    if (recovery.Discarded)
                    {
                        if (recovery.SourceAvailable &&
                            TryDeleteBufferDirectory(
                                recovery.SessionDirectory,
                                recovery.BufferRoot))
                        {
                            RecordingRecoveryJournal.TryDeleteJournal(
                                recovery.JournalPath);
                        }
                        else if (!recovery.SourceAvailable &&
                                 RecordingRecoveryJournal.CanRetireMissingSession(
                                     recovery.BufferRoot,
                                     recovery.SessionDirectory))
                        {
                            RecordingRecoveryJournal.TryDeleteJournal(
                                recovery.JournalPath);
                        }

                        // A durable user discard is terminal. An unavailable or
                        // temporarily locked source remains indexed for a later
                        // cleanup retry, but must never resurrect a pending capture
                        // gate after the user explicitly discarded it.
                        continue;
                    }

                    if (!string.IsNullOrWhiteSpace(recovery.CommittedOutputPath))
                    {
                        if (recovery.SourceAvailable &&
                            TryDeleteBufferDirectory(
                                recovery.SessionDirectory,
                                recovery.BufferRoot))
                        {
                            RecordingRecoveryJournal.TryDeleteJournal(
                                recovery.JournalPath);
                        }
                        else if (!recovery.SourceAvailable &&
                                 RecordingRecoveryJournal.CanRetireMissingSession(
                                     recovery.BufferRoot,
                                     recovery.SessionDirectory))
                        {
                            // The committed output has a durable identity and
                            // the regular source root is online, so a missing
                            // session is already-cleaned state rather than an
                            // offline drive. Retire the tombstone so it cannot
                            // crowd older real recoveries out of the scan.
                            RecordingRecoveryJournal.TryDeleteJournal(
                                recovery.JournalPath);
                        }

                        continue;
                    }

                    if (recovery.CommitTerminalRecorded &&
                        !recovery.SourceAvailable &&
                        RecordingRecoveryJournal.CanRetireMissingSession(
                            recovery.BufferRoot,
                            recovery.SessionDirectory))
                    {
                        // The output may have been moved or deleted after an
                        // already-committed session was cleaned. The terminal
                        // record still proves that the missing source must not
                        // be resurrected as an offline pending recording.
                        RecordingRecoveryJournal.TryDeleteJournal(
                            recovery.JournalPath);
                        continue;
                    }

                    if (!recovery.SourceAvailable &&
                        RecordingRecoveryJournal.CanRetireMissingSession(
                            recovery.BufferRoot,
                            recovery.SessionDirectory))
                    {
                        // The regular source root is online and the exact
                        // session directory is gone. This is stale cleanup
                        // residue, not an offline drive that the user can
                        // reconnect, so it must not become an undiscardable
                        // pending Recorder gate.
                        RecordingRecoveryJournal.TryDeleteJournal(
                            recovery.JournalPath);
                        continue;
                    }

                    if (!recovery.SourceAvailable &&
                        RecordingRecoveryJournal.TryGetSessionMarkerId(
                            recovery.SessionDirectory,
                            out var currentMarkerSessionId) &&
                        !string.Equals(
                            currentMarkerSessionId,
                            recovery.SessionId,
                            StringComparison.Ordinal))
                    {
                        // The deterministic locator name is derived from the
                        // session path. A later, independently authenticated
                        // session can legitimately reuse that path after an old
                        // source was removed. Do not let the stale locator mask
                        // the current folder's exact recovery state.
                        EnqueueDiagnostic(
                            "Recorder skipped a stale central locator because the live session directory has a different recovery identity.");
                        continue;
                    }

                    if (recovery.SourceAvailable &&
                        await TryLoadExactRecordingRecoveryAsync(
                                recovery,
                                cancellationToken)
                            .ConfigureAwait(false) is { } exactRecovery)
                    {
                        PublishRecoveredRecording(exactRecovery);
                        return true;
                    }

                    var detached = new DetachedRecordingBuffer(
                        recovery.SessionDirectory,
                        recovery.BufferRoot,
                        recovery.SegmentPaths,
                        recovery.SegmentBytes,
                        recovery.FramesPerSecond,
                        recovery.HasAudio,
                        recovery.JournalPath,
                        recovery.SessionId,
                        recovery.SourceAvailable,
                        recovery.MissingSegmentCount,
                        recovery.Detached,
                        LiveRecordingRecoveryPath:
                            recovery.LiveRecordingRecoveryPath,
                        SegmentTimelineDuration:
                            recovery.SegmentTimelineDuration,
                        SegmentDurationSeconds:
                            recovery.SegmentDurationSeconds);
                    PublishRecoveredRecording(detached);
                    return true;
                }

                if (!IsSafeRecordingRootCandidate(saveDirectory, out var root))
                {
                    return false;
                }

                var candidates = EnumerateRecordingRecoveryDirectories(root);
                foreach (var directory in candidates)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var statePath = Path.Combine(
                        directory.FullName,
                        RecordingRecoveryStateFileName);
                    if (!File.Exists(statePath))
                    {
                        continue;
                    }

                    try
                    {
                        var stateInfo = new FileInfo(statePath);
                        if (stateInfo.Length <= 0 ||
                            stateInfo.Length > MaximumRecordingRecoveryStateBytes ||
                            (stateInfo.Attributes &
                             (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                        {
                            continue;
                        }

                        var json = await File.ReadAllTextAsync(
                                statePath,
                                cancellationToken)
                            .ConfigureAwait(false);
                        var recovery = JsonSerializer.Deserialize<RecordingRecoveryState>(json);
                        if (!TryCreateDetachedRecordingBuffer(
                                root,
                                directory.FullName,
                                recovery,
                                out var detached))
                        {
                            continue;
                        }

                        var recovered = detached;
                        var legacyCentralJournalPath =
                            RecordingRecoveryJournal.GetJournalPath(
                                _recordingRecoveryRoot,
                                detached.SessionDirectory);
                        RecordingRecoveryJournalSnapshot? existingCentralRecovery;
                        if (RecordingRecoveryJournal.TryGetSessionMarkerId(
                                detached.SessionDirectory,
                                out var exactMarkerSessionId) &&
                            exactMarkerSessionId is not null)
                        {
                            var family = await RecordingRecoveryJournal
                                .TryReadJournalFamilyAsync(
                                    _recordingRecoveryRoot,
                                    detached.SessionDirectory,
                                    exactMarkerSessionId,
                                    cancellationToken)
                                .ConfigureAwait(false);
                            existingCentralRecovery = family.Count == 0
                                ? null
                                : await ResolveRecoveryLocatorFamilyAsync(
                                        family[0],
                                        cancellationToken)
                                    .ConfigureAwait(false);
                        }
                        else
                        {
                            existingCentralRecovery =
                                await RecordingRecoveryJournal.TryReadAsync(
                                        _recordingRecoveryRoot,
                                        legacyCentralJournalPath,
                                        cancellationToken)
                                    .ConfigureAwait(false);
                        }
                        var existingCentralPathMatchesDetached =
                            existingCentralRecovery is not null &&
                            HasMatchingRecoveryPathIdentity(
                                existingCentralRecovery,
                                detached);
                        var existingCentralMatchesDetached =
                            existingCentralPathMatchesDetached &&
                            RecordingRecoveryJournal.TryGetSessionMarkerId(
                                detached.SessionDirectory,
                                out var detachedMarkerSessionId) &&
                            string.Equals(
                                detachedMarkerSessionId,
                                existingCentralRecovery?.SessionId,
                                StringComparison.Ordinal);
                        if (existingCentralRecovery is not null &&
                            !existingCentralMatchesDetached)
                        {
                            _ = await TrySupersedeStaleRecoveryLocatorAsync(
                                    existingCentralRecovery)
                                .ConfigureAwait(false);
                            EnqueueDiagnostic(
                                "Recorder found a central-locator path or session-marker collision and refused to attach or replace the other session's journal.");
                        }
                        var missingCommittedSourceCanBeRetired =
                            existingCentralMatchesDetached &&
                            existingCentralRecovery is
                            {
                                CommitTerminalRecorded: true,
                                SourceAvailable: false
                            } &&
                            RecordingRecoveryJournal.CanRetireMissingSession(
                                existingCentralRecovery.BufferRoot,
                                existingCentralRecovery.SessionDirectory);
                        if (existingCentralMatchesDetached &&
                            (existingCentralRecovery is { Discarded: true } ||
                             !string.IsNullOrWhiteSpace(
                                 existingCentralRecovery?.CommittedOutputPath) ||
                             missingCommittedSourceCanBeRetired))
                        {
                            var terminalRecovery = existingCentralRecovery!;
                            // A valid terminal locator always outranks a stale
                            // exact-state file, including when the locator was
                            // outside the bounded newest-candidate scan.
                            if (terminalRecovery.SourceAvailable &&
                                TryDeleteBufferDirectory(
                                    terminalRecovery.SessionDirectory,
                                    terminalRecovery.BufferRoot))
                            {
                                RecordingRecoveryJournal.TryDeleteJournal(
                                    terminalRecovery.JournalPath);
                            }
                            else if (missingCommittedSourceCanBeRetired)
                            {
                                RecordingRecoveryJournal.TryDeleteJournal(
                                    terminalRecovery.JournalPath);
                            }

                            continue;
                        }

                        try
                        {
                            recovered = existingCentralMatchesDetached
                                ? detached with
                                {
                                    RecoveryJournalPath =
                                        existingCentralRecovery!.JournalPath,
                                    RecoverySessionId =
                                        existingCentralRecovery.SessionId,
                                    RecoveryWasDetached = true
                                }
                                : await CreateCentralRecoveryLocatorAsync(
                                        detached,
                                        cancellationToken)
                                    .ConfigureAwait(false);
                        }
                        catch (Exception exception) when (
                            exception is IOException or UnauthorizedAccessException or
                                ArgumentException or NotSupportedException or SecurityException or
                                InvalidOperationException)
                        {
                            // The exact recovery state remains authoritative even
                            // if the optional central-locator migration cannot be
                            // completed. Publishing it also prevents a new capture
                            // from overwriting or hiding the preserved session.
                            EnqueueDiagnostic(
                                $"Recovered Recorder source, but its central locator could not be migrated: {exception.GetBaseException().Message}");
                        }

                        PublishRecoveredRecording(recovered);
                        return true;
                    }
                    catch (Exception exception) when (
                        exception is IOException or UnauthorizedAccessException or
                            JsonException or ArgumentException or NotSupportedException or
                            SecurityException)
                    {
                        EnqueueDiagnostic(
                            $"Skipped an invalid Recorder recovery state: {exception.GetBaseException().Message}");
                    }
                }

                return false;
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

    private void PublishRecoveredRecording(DetachedRecordingBuffer detached)
    {
        Volatile.Write(ref _pendingDetachedRecordingBuffer, detached);
        _sessionMode = CaptureSessionMode.Recording;
        _retention = RecordingStoragePolicy.NoReplayRetention;
        var liveRecoveryBytes = string.IsNullOrWhiteSpace(
                detached.LiveRecordingRecoveryPath)
            ? 0
            : GetFileLengthSafely(detached.LiveRecordingRecoveryPath);
        var recoveredBytes = detached.SegmentBytes >
            long.MaxValue - liveRecoveryBytes
                ? long.MaxValue
                : detached.SegmentBytes + liveRecoveryBytes;
        Publish(new ReplayStateSnapshot(
            ReplayState.Faulted,
            TimeSpan.Zero,
            RecordingStoragePolicy.NoReplayRetention,
            recoveredBytes,
            detached.SourceAvailable
                ? detached.SegmentPaths.Count == 0 &&
                  !string.IsNullOrWhiteSpace(
                      detached.LiveRecordingRecoveryPath)
                    ? "A Recorder session was recovered from its interrupted live output. Select Retry save to repair and preserve it."
                    : detached.MissingSegmentCount > 0
                    ? $"A stopped Recorder session was recovered without {detached.MissingSegmentCount} missing segment(s). Select Retry save to preserve the remaining video."
                    : "A stopped Recorder session is waiting to be saved. Select Retry save."
                : "A Recorder session is preserved, but its source drive is unavailable. Reconnect the drive and select Retry save."));
    }

    private async Task<RecordingRecoveryJournalSnapshot?>
        ResolveRecoveryLocatorFamilyAsync(
            RecordingRecoveryJournalSnapshot recovery,
            CancellationToken cancellationToken)
    {
        var family = await RecordingRecoveryJournal.TryReadJournalFamilyAsync(
                _recordingRecoveryRoot,
                recovery.SessionDirectory,
                recovery.SessionId,
                cancellationToken)
            .ConfigureAwait(false);
        if (family.Count == 0)
        {
            return recovery;
        }

        var liveFamily = family
            .Where(candidate => !candidate.Superseded)
            .ToArray();
        if (liveFamily.Length == 0)
        {
            foreach (var superseded in family)
            {
                RecordingRecoveryJournal.TryDeleteJournal(
                    superseded.JournalPath);
            }

            return null;
        }

        // All repair suffixes belong to the authenticated marker identity. A
        // durable user/commit terminal therefore outranks an older or newer
        // actionable duplicate; otherwise retain the newest live member.
        var authoritative = liveFamily.FirstOrDefault(candidate =>
                                !string.IsNullOrWhiteSpace(
                                    candidate.CommittedOutputPath)) ??
                            liveFamily.FirstOrDefault(candidate =>
                                candidate.Discarded) ??
                            liveFamily[0];
        foreach (var duplicate in family)
        {
            if (string.Equals(
                    duplicate.JournalPath,
                    authoritative.JournalPath,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (duplicate.Superseded)
            {
                RecordingRecoveryJournal.TryDeleteJournal(
                    duplicate.JournalPath);
                continue;
            }

            try
            {
                await RecordingRecoveryJournal.MarkSupersededAsync(
                        _recordingRecoveryRoot,
                        duplicate.JournalPath,
                        duplicate.SessionId,
                        cancellationToken)
                    .ConfigureAwait(false);
                RecordingRecoveryJournal.TryDeleteJournal(
                    duplicate.JournalPath);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or
                    InvalidDataException or InvalidOperationException or
                    ArgumentException or NotSupportedException or SecurityException)
            {
                // The family is still resolved as a unit for this scan. A later
                // launch retries the durable supersession before any duplicate
                // can be surfaced on its own.
                EnqueueDiagnostic(
                    "Recorder could not retire a duplicate recovery locator: " +
                    exception.GetBaseException().Message);
            }
        }

        return authoritative;
    }

    private static async Task<DetachedRecordingBuffer?> TryLoadExactRecordingRecoveryAsync(
        RecordingRecoveryJournalSnapshot recovery,
        CancellationToken cancellationToken)
    {
        var statePath = Path.Combine(
            recovery.SessionDirectory,
            RecordingRecoveryStateFileName);
        if (!File.Exists(statePath))
        {
            return null;
        }

        try
        {
            var stateInfo = new FileInfo(statePath);
            if (stateInfo.Length <= 0 ||
                stateInfo.Length > MaximumRecordingRecoveryStateBytes ||
                (stateInfo.Attributes &
                 (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            {
                return null;
            }

            var json = await File.ReadAllTextAsync(statePath, cancellationToken)
                .ConfigureAwait(false);
            var state = JsonSerializer.Deserialize<RecordingRecoveryState>(json);
            if (recovery.LiveOutputInvalidated && state is not null)
            {
                state = state with
                {
                    LiveRecordingPrefixPath = null,
                    LiveRecordingPath = null,
                    LiveRecordingExpectedDurationSeconds = null,
                    LiveRecordingFastPathEligible = false,
                    LiveRecordingNeedsCfrNormalization = false,
                    LiveRecordingRecoveryPath = null
                };
            }
            return TryCreateDetachedRecordingBuffer(
                recovery.BufferRoot,
                recovery.SessionDirectory,
                state,
                out var detached)
                && detached.SegmentDurationSeconds ==
                    recovery.SegmentDurationSeconds
                ? detached with
                {
                    RecoveryJournalPath = recovery.JournalPath,
                    RecoverySessionId = recovery.SessionId,
                    SourceAvailable = true,
                    RecoveryWasDetached = true,
                    LiveRecordingRecoveryPath =
                        detached.LiveRecordingRecoveryPath ??
                        recovery.LiveRecordingRecoveryPath
                }
                : null;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException or
                ArgumentException or NotSupportedException or SecurityException)
        {
            return null;
        }
    }

    private async Task<DetachedRecordingBuffer> CreateCentralRecoveryLocatorAsync(
        DetachedRecordingBuffer detached,
        CancellationToken cancellationToken)
    {
        var hasExistingMarker = RecordingRecoveryJournal.TryGetSessionMarkerId(
            detached.SessionDirectory,
            out _);
        var journal = hasExistingMarker
            ? await RecordingRecoveryJournal.RepairAsync(
                    _recordingRecoveryRoot,
                    detached.SessionDirectory,
                    detached.BufferRoot,
                    detached.FramesPerSecond,
                    detached.HasAudio,
                    detached.SegmentDurationSeconds,
                    detached.LiveRecordingPath ??
                    detached.LiveRecordingRecoveryPath,
                    cancellationToken)
                .ConfigureAwait(false)
            : await RecordingRecoveryJournal.CreateAsync(
                    _recordingRecoveryRoot,
                    detached.SessionDirectory,
                    detached.BufferRoot,
                    detached.FramesPerSecond,
                    detached.HasAudio,
                    detached.SegmentDurationSeconds,
                    detached.LiveRecordingPath ??
                    detached.LiveRecordingRecoveryPath,
                    cancellationToken)
                .ConfigureAwait(false);
        var locatorCompleted = false;
        try
        {
            foreach (var segmentPath in detached.SegmentPaths)
            {
                var segmentLength = GetFileLengthSafely(segmentPath);
                if (!TryParseSegmentNumber(
                        Path.GetFileName(segmentPath),
                        out var segmentNumber) ||
                    segmentLength <= 0 ||
                    !journal.RecordCompleted(
                        segmentPath,
                        segmentLength,
                        segmentNumber))
                {
                    throw new IOException(
                        "ClipForge could not index the complete migrated Recorder session.");
                }
            }

            var locatorClosed = await journal.CloseAsync(
                    detached: true,
                    segmentTimelineDuration: detached.SegmentTimelineDuration)
                .ConfigureAwait(false);
            if (!locatorClosed || journal.WriterFailure is not null)
            {
                throw new IOException(
                    "ClipForge could not create the central Recorder recovery locator.",
                    journal.WriterFailure);
            }

            if (journal.RequiresAtomicPublish)
            {
                await journal.PublishRepairAsync(
                        _recordingRecoveryRoot,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            locatorCompleted = true;
            return detached with
            {
                RecoveryJournalPath = journal.JournalPath,
                RecoverySessionId = journal.SessionId,
                RecoveryWasDetached = true
            };
        }
        finally
        {
            try
            {
                await journal.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or
                    InvalidOperationException or ObjectDisposedException)
            {
                if (locatorCompleted)
                {
                    EnqueueDiagnostic(
                        $"Recorder created its recovery locator, but handle cleanup reported: " +
                        exception.GetBaseException().Message);
                }
            }

            if (!locatorCompleted)
            {
                journal.AbandonUnpublishedJournal();
                if (journal.OwnsSessionMarker)
                {
                    RecordingRecoveryJournal.TryDeleteOwnedSessionMarker(
                        journal.SessionDirectory,
                        journal.SessionId);
                }
            }
        }
    }

    private static bool HasMatchingRecoveryPathIdentity(
        RecordingRecoveryJournalSnapshot recovery,
        DetachedRecordingBuffer detached)
    {
        try
        {
            return string.Equals(
                       Path.TrimEndingDirectorySeparator(
                           Path.GetFullPath(recovery.BufferRoot)),
                       Path.TrimEndingDirectorySeparator(
                           Path.GetFullPath(detached.BufferRoot)),
                       StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(
                       Path.TrimEndingDirectorySeparator(
                           Path.GetFullPath(recovery.SessionDirectory)),
                       Path.TrimEndingDirectorySeparator(
                           Path.GetFullPath(detached.SessionDirectory)),
                       StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                ArgumentException or NotSupportedException or SecurityException)
        {
            return false;
        }
    }

    private async Task<bool> TrySupersedeStaleRecoveryLocatorAsync(
        RecordingRecoveryJournalSnapshot recovery)
    {
        if (recovery.SourceAvailable ||
            !RecordingRecoveryJournal.TryGetSessionMarkerId(
                recovery.SessionDirectory,
                out var currentMarkerSessionId) ||
            string.Equals(
                currentMarkerSessionId,
                recovery.SessionId,
                StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            await RecordingRecoveryJournal.MarkSupersededAsync(
                    _recordingRecoveryRoot,
                    recovery.JournalPath,
                    recovery.SessionId,
                    CancellationToken.None)
                .ConfigureAwait(false);
            RecordingRecoveryJournal.TryDeleteJournal(recovery.JournalPath);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                InvalidDataException or InvalidOperationException or
                ArgumentException or NotSupportedException or SecurityException)
        {
            // The current marker still proves that this locator cannot own the
            // path. Keep ignoring it now and retry the durable tombstone on a
            // later scan rather than attaching it to the replacement session.
            EnqueueDiagnostic(
                $"Recorder could not durably retire a stale recovery locator: " +
                exception.GetBaseException().Message);
        }

        return true;
    }

    private static IReadOnlyList<DirectoryInfo> EnumerateRecordingRecoveryDirectories(
        string root)
    {
        try
        {
            return Directory.EnumerateDirectories(
                    root,
                    "session-*",
                    SearchOption.TopDirectoryOnly)
                .Select(path => new DirectoryInfo(path))
                .Where(directory =>
                    directory.Exists &&
                    IsSafeBufferDirectoryPath(
                        root,
                        directory.FullName,
                        directory.Attributes))
                .OrderByDescending(directory => directory.LastWriteTimeUtc)
                .Take(256)
                .ToArray();
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                ArgumentException or NotSupportedException or SecurityException)
        {
            return [];
        }
    }

    private static bool IsSafeRecordingRootCandidate(
        string saveDirectory,
        out string root)
    {
        root = string.Empty;
        try
        {
            root = RecordingStoragePolicy.GetWorkingRoot(saveDirectory);
            if (!Directory.Exists(root))
            {
                return false;
            }

            var info = new DirectoryInfo(root);
            return info.Exists &&
                   (info.Attributes &
                    (FileAttributes.ReparsePoint | FileAttributes.Directory)) ==
                       FileAttributes.Directory;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                ArgumentException or NotSupportedException or SecurityException)
        {
            return false;
        }
    }

    private static bool TryCreateDetachedRecordingBuffer(
        string expectedRoot,
        string expectedSessionDirectory,
        RecordingRecoveryState? recovery,
        out DetachedRecordingBuffer detached)
    {
        detached = null!;
        if (recovery is null ||
            recovery.Version is not (1 or 2 or 3) ||
            recovery.SegmentBytes < 0 ||
            recovery.FramesPerSecond is < 1 or > 240 ||
            !TryResolveRecoveryStateSegmentDuration(
                recovery,
                out var segmentDurationSeconds))
        {
            return false;
        }

        var root = Path.GetFullPath(expectedRoot);
        var sessionDirectory = Path.GetFullPath(expectedSessionDirectory);
        if (!string.Equals(
                sessionDirectory,
                Path.GetFullPath(recovery.SessionDirectory),
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (recovery.Version == 3 &&
            (!Guid.TryParseExact(recovery.RecoverySessionId, "N", out _) ||
             !RecordingRecoveryJournal.TryGetSessionMarkerId(
                 sessionDirectory,
                 out var markerSessionId) ||
             !string.Equals(
                 markerSessionId,
                 recovery.RecoverySessionId,
                 StringComparison.Ordinal)))
        {
            return false;
        }

        var sessionPrefix = sessionDirectory.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!TryBuildRecoverySegmentCandidates(
                recovery,
                sessionDirectory,
                out var candidates))
        {
            return false;
        }

        if (recovery.Version == 3 &&
            (recovery.SegmentLengths is not { } exactSegmentLengths ||
             exactSegmentLengths.Count != candidates.Count ||
             exactSegmentLengths.Any(length => length <= 0)))
        {
            return false;
        }

        var paths = new List<string>(candidates.Count);
        long bytes = 0;
        var missingSegmentCount = 0;
        for (var candidateIndex = 0; candidateIndex < candidates.Count; candidateIndex++)
        {
            var candidate = candidates[candidateIndex];
            if (string.IsNullOrWhiteSpace(candidate))
            {
                return false;
            }

            var path = Path.GetFullPath(candidate);
            if (!path.StartsWith(sessionPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (!File.Exists(path))
            {
                if (recovery.Version == 3)
                {
                    return false;
                }

                missingSegmentCount++;
                continue;
            }

            var info = new FileInfo(path);
            if ((info.Attributes &
                  (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0 ||
                info.Length <= 0)
            {
                if (recovery.Version == 3)
                {
                    return false;
                }

                missingSegmentCount++;
                continue;
            }

            if (recovery.Version == 3 &&
                info.Length != recovery.SegmentLengths![candidateIndex])
            {
                return false;
            }

            if (bytes > long.MaxValue - info.Length)
            {
                return false;
            }

            bytes += info.Length;
            paths.Add(path);
        }

        if (recovery.Version is 2 or 3 &&
            (missingSegmentCount > 0 || bytes != recovery.SegmentBytes))
        {
            // Version 2 is written atomically after Recorder stops. If its
            // compact segment set no longer has the exact persisted aggregate,
            // prefer the per-segment lengths in the central journal instead of
            // reintroducing a truncated/replaced file through exact-state
            // precedence. Schema-v1 files remain best-effort compatible.
            return false;
        }


        TimeSpan? segmentTimelineDuration = null;
        if (recovery.SegmentTimelineDurationSeconds is { } timelineSeconds)
        {
            var minimumTimelineSeconds = paths.Count == 0
                ? 0d
                : (long)(paths.Count - 1) * segmentDurationSeconds +
                  1d / recovery.FramesPerSecond;
            var maximumTimelineSeconds =
                (long)paths.Count * segmentDurationSeconds;
            if (!double.IsFinite(timelineSeconds) ||
                paths.Count == 0 ||
                timelineSeconds < minimumTimelineSeconds ||
                timelineSeconds > maximumTimelineSeconds)
            {
                return false;
            }

            segmentTimelineDuration = TimeSpan.FromSeconds(timelineSeconds);
        }
        else if (recovery.Version == 3 && paths.Count > 0)
        {
            return false;
        }

        var liveOutputInvalidated =
            RecordingRecoveryJournal.HasLiveOutputInvalidationMarker(
                sessionDirectory);
        string? liveRecordingPrefixPath = null;
        string? liveRecordingPath = null;
        TimeSpan? liveRecordingExpectedDuration = null;
        if (!liveOutputInvalidated)
        {
            _ = TryResolveLiveRecordingPrefixOutput(
                sessionDirectory,
                recovery.LiveRecordingPrefixPath,
                out liveRecordingPrefixPath);
            _ = TryResolveLiveRecordingOutput(
                sessionDirectory,
                recovery.LiveRecordingPath,
                recovery.LiveRecordingExpectedDurationSeconds,
                recovery.LiveRecordingFastPathEligible,
                out liveRecordingPath,
                out liveRecordingExpectedDuration);
        }
        var liveRecordingRecoveryPath =
            !liveOutputInvalidated &&
            TryResolveInterruptedLiveRecordingOutput(
                sessionDirectory,
                recovery.LiveRecordingRecoveryPath,
                out var resolvedRecoveryPath)
                ? resolvedRecoveryPath
                : null;
        if (paths.Count == 0 &&
            liveRecordingPath is null &&
            liveRecordingRecoveryPath is null)
        {
            return false;
        }

        detached = new DetachedRecordingBuffer(
            sessionDirectory,
            root,
            paths,
            bytes,
            recovery.FramesPerSecond,
            recovery.HasAudio,
            MissingSegmentCount: missingSegmentCount,
            RecoveryWasDetached: true,
            SegmentTimelineDuration: segmentTimelineDuration,
            LiveRecordingPrefixPath: liveRecordingPrefixPath,
            LiveRecordingPath: liveRecordingPath,
            LiveRecordingExpectedDuration: liveRecordingExpectedDuration,
            LiveRecordingFastPathEligible: liveRecordingPath is not null,
            LiveRecordingNeedsCfrNormalization:
                recovery.LiveRecordingNeedsCfrNormalization &&
                liveRecordingPath is not null,
            LiveRecordingRecoveryPath: liveRecordingRecoveryPath,
            SegmentDurationSeconds: segmentDurationSeconds);
        return true;
    }

    private static bool TryResolveRecoveryStateSegmentDuration(
        RecordingRecoveryState recovery,
        out int segmentDurationSeconds)
    {
        segmentDurationSeconds = recovery.Version == 1
            ? recovery.SegmentDurationSeconds ?? FfmpegArgumentBuilder.SegmentSeconds
            : recovery.SegmentDurationSeconds.GetValueOrDefault();
        return segmentDurationSeconds is >= 1 and <= 300 &&
               (recovery.Version == 1 ||
                recovery.SegmentDurationSeconds is not null);
    }

    private static bool TryBuildRecoverySegmentCandidates(
        RecordingRecoveryState recovery,
        string sessionDirectory,
        out IReadOnlyList<string> candidates)
    {
        const int maximumSegments = 1_000_000;
        candidates = [];
        if (recovery.Version == 1)
        {
            if (recovery.SegmentPaths is not { Count: <= maximumSegments } paths ||
                recovery.SegmentRanges is { Count: > 0 })
            {
                return false;
            }

            candidates = paths;
            return true;
        }

        if (recovery.SegmentPaths is { Count: > 0 } ||
            recovery.SegmentRanges is not { Count: <= maximumSegments } ranges)
        {
            return false;
        }

        var expanded = new List<string>();
        var previousLast = -1;
        foreach (var range in ranges)
        {
            if (range.FirstSegmentNumber < 0 ||
                range.LastSegmentNumber < range.FirstSegmentNumber ||
                range.LastSegmentNumber > 999_999_999 ||
                range.FirstSegmentNumber <= previousLast)
            {
                return false;
            }

            var rangeCount = (long)range.LastSegmentNumber -
                range.FirstSegmentNumber + 1;
            if (rangeCount > maximumSegments - expanded.Count)
            {
                return false;
            }

            for (var segmentNumber = range.FirstSegmentNumber;
                 segmentNumber <= range.LastSegmentNumber;
                 segmentNumber++)
            {
                expanded.Add(Path.Combine(
                    sessionDirectory,
                    $"segment-{segmentNumber:D9}.mkv"));
            }

            previousLast = range.LastSegmentNumber;
        }

        candidates = expanded;
        return true;
    }

    private static bool TryResolveLiveRecordingOutput(
        string sessionDirectory,
        string? candidatePath,
        double? expectedDurationSeconds,
        bool eligible,
        out string? liveRecordingPath,
        out TimeSpan? expectedDuration)
    {
        liveRecordingPath = null;
        expectedDuration = null;
        if (!eligible ||
            string.IsNullOrWhiteSpace(candidatePath) ||
            expectedDurationSeconds is not { } seconds ||
            !double.IsFinite(seconds) ||
            seconds <= 0)
        {
            return false;
        }

        try
        {
            var normalizedSession = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(sessionDirectory));
            var sessionParent = Path.GetDirectoryName(normalizedSession);
            var sessionInfo = new DirectoryInfo(normalizedSession);
            if (string.IsNullOrWhiteSpace(sessionParent) ||
                !sessionInfo.Exists ||
                !IsSafeBufferDirectoryPath(
                    sessionParent,
                    sessionInfo.FullName,
                    sessionInfo.Attributes))
            {
                return false;
            }

            var normalizedCandidate = Path.GetFullPath(candidatePath);
            var expectedPath = Path.Combine(
                normalizedSession,
                LiveRecordingFileName);
            var legacyPartOnePath =
                FfmpegArgumentBuilder.GetDirectRecordingPartPath(
                    expectedPath,
                    partNumber: 1);
            if (!normalizedCandidate.Equals(
                    expectedPath,
                    StringComparison.OrdinalIgnoreCase) &&
                !normalizedCandidate.Equals(
                    legacyPartOnePath,
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var info = new FileInfo(normalizedCandidate);
            if (!info.Exists ||
                info.Length <= 0 ||
                (info.Attributes &
                 (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            {
                return false;
            }

            liveRecordingPath = info.FullName;
            expectedDuration = TimeSpan.FromSeconds(seconds);
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                ArgumentException or NotSupportedException or SecurityException or
                OverflowException)
        {
            return false;
        }
    }

    private static bool TryResolveLiveRecordingPrefixOutput(
        string sessionDirectory,
        string? candidatePath,
        out string? liveRecordingPrefixPath)
    {
        liveRecordingPrefixPath = null;
        if (string.IsNullOrWhiteSpace(candidatePath))
        {
            return false;
        }

        try
        {
            var normalizedSession = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(sessionDirectory));
            var sessionParent = Path.GetDirectoryName(normalizedSession);
            var sessionInfo = new DirectoryInfo(normalizedSession);
            if (string.IsNullOrWhiteSpace(sessionParent) ||
                !sessionInfo.Exists ||
                !IsSafeBufferDirectoryPath(
                    sessionParent,
                    sessionInfo.FullName,
                    sessionInfo.Attributes))
            {
                return false;
            }

            var normalizedCandidate = Path.GetFullPath(candidatePath);
            var expectedPath = FfmpegArgumentBuilder.GetDirectRecordingPartPath(
                Path.Combine(normalizedSession, LiveRecordingFileName),
                partNumber: 0);
            if (!normalizedCandidate.Equals(
                    expectedPath,
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var info = new FileInfo(normalizedCandidate);
            if (!info.Exists ||
                info.Length <= 0 ||
                (info.Attributes &
                 (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            {
                return false;
            }

            liveRecordingPrefixPath = info.FullName;
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                ArgumentException or NotSupportedException or SecurityException)
        {
            return false;
        }
    }

    private static bool IsDefinitelyMissingOrUnsafeRecordingComponent(
        string? candidatePath)
    {
        if (string.IsNullOrWhiteSpace(candidatePath))
        {
            return true;
        }

        try
        {
            var attributes = File.GetAttributes(candidatePath);
            if ((attributes &
                 (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            {
                return true;
            }

            return new FileInfo(candidatePath).Length <= 0;
        }
        catch (Exception exception) when (
            exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                ArgumentException or NotSupportedException or SecurityException)
        {
            // Locks and temporary access failures must remain retryable.
            return false;
        }
    }

    internal static bool TryResolveGracefulShortRecordingPrefixOutput(
        string sessionDirectory,
        string liveRecordingPartOnePath,
        long stoppedOutputTimeMicroseconds,
        int framesPerSecond,
        out string? liveRecordingPrefixPath)
    {
        liveRecordingPrefixPath = null;
        if (stoppedOutputTimeMicroseconds <= 0 ||
            framesPerSecond is < 1 or > 240)
        {
            return false;
        }

        // Part 0 is the two-second WGC startup quarantine. If a user stops
        // shortly after that split, publishing part 1 alone could turn a
        // 2.1-second recording into a 0.1-second file. Keep both tiny parts so
        // finalization can stream-copy them into one exact short recording.
        var maximumMergedDurationMicroseconds = checked(
            2L * FfmpegArgumentBuilder.SegmentSeconds * 1_000_000L +
            2L * ((1_000_000L + framesPerSecond - 1) / framesPerSecond));
        if (stoppedOutputTimeMicroseconds > maximumMergedDurationMicroseconds ||
            !TryResolveLiveRecordingPrefixOutput(
                sessionDirectory,
                FfmpegArgumentBuilder.GetDirectRecordingPartPath(
                    Path.Combine(sessionDirectory, LiveRecordingFileName),
                    partNumber: 0),
                out liveRecordingPrefixPath))
        {
            liveRecordingPrefixPath = null;
            return false;
        }

        return TryResolveLiveRecordingOutput(
            sessionDirectory,
            liveRecordingPartOnePath,
            stoppedOutputTimeMicroseconds / 1_000_000d,
            eligible: true,
            out _,
            out _);
    }

    internal static bool TryPromoteGracefulShortRecordingOutput(
        string sessionDirectory,
        string liveRecordingPartOnePath,
        long stoppedOutputTimeMicroseconds,
        int framesPerSecond)
    {
        if (stoppedOutputTimeMicroseconds <= 0 ||
            framesPerSecond is < 1 or > 240)
        {
            return false;
        }

        var maximumShortDurationMicroseconds = checked(
            FfmpegArgumentBuilder.SegmentSeconds * 1_000_000L +
            2L * ((1_000_000L + framesPerSecond - 1) / framesPerSecond));
        if (stoppedOutputTimeMicroseconds > maximumShortDurationMicroseconds)
        {
            return false;
        }

        try
        {
            var normalizedSession = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(sessionDirectory));
            var sessionParent = Path.GetDirectoryName(normalizedSession);
            var sessionInfo = new DirectoryInfo(normalizedSession);
            if (string.IsNullOrWhiteSpace(sessionParent) ||
                !sessionInfo.Exists ||
                !IsSafeBufferDirectoryPath(
                    sessionParent,
                    sessionInfo.FullName,
                    sessionInfo.Attributes))
            {
                return false;
            }

            var directRecordingBasePath = Path.Combine(
                normalizedSession,
                LiveRecordingFileName);
            var expectedPartOnePath =
                FfmpegArgumentBuilder.GetDirectRecordingPartPath(
                    directRecordingBasePath,
                    partNumber: 1);
            var normalizedPartOnePath = Path.GetFullPath(
                liveRecordingPartOnePath);
            if (!string.Equals(
                    normalizedPartOnePath,
                    expectedPartOnePath,
                    StringComparison.OrdinalIgnoreCase) ||
                File.Exists(normalizedPartOnePath) ||
                Directory.Exists(normalizedPartOnePath))
            {
                return false;
            }

            var partZeroPath = FfmpegArgumentBuilder.GetDirectRecordingPartPath(
                directRecordingBasePath,
                partNumber: 0);
            var partZeroInfo = new FileInfo(partZeroPath);
            if (!partZeroInfo.Exists ||
                partZeroInfo.Length <= 0 ||
                (partZeroInfo.Attributes &
                 (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            {
                return false;
            }

            File.Move(partZeroInfo.FullName, normalizedPartOnePath);
            var promotedInfo = new FileInfo(normalizedPartOnePath);
            return promotedInfo.Exists &&
                   promotedInfo.Length == partZeroInfo.Length &&
                   (promotedInfo.Attributes &
                    (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                ArgumentException or NotSupportedException or SecurityException or
                OverflowException)
        {
            return false;
        }
    }

    private static bool TryResolveInterruptedLiveRecordingOutput(
        string sessionDirectory,
        string? candidatePath,
        out string? liveRecordingPath)
    {
        liveRecordingPath = null;
        if (string.IsNullOrWhiteSpace(candidatePath))
        {
            return false;
        }

        try
        {
            var normalizedSession = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(sessionDirectory));
            var sessionParent = Path.GetDirectoryName(normalizedSession);
            var sessionInfo = new DirectoryInfo(normalizedSession);
            if (string.IsNullOrWhiteSpace(sessionParent) ||
                !sessionInfo.Exists ||
                !IsSafeBufferDirectoryPath(
                    sessionParent,
                    sessionInfo.FullName,
                    sessionInfo.Attributes))
            {
                return false;
            }

            var normalizedCandidate = Path.GetFullPath(candidatePath);
            var expectedPath = Path.Combine(
                normalizedSession,
                RecordingRecoveryJournal.LiveRecordingRecoveryFileName);
            var legacyExpectedPath =
                FfmpegArgumentBuilder.GetDirectRecordingPartPath(
                    Path.Combine(normalizedSession, LiveRecordingFileName),
                    partNumber: 1);
            if (!normalizedCandidate.Equals(
                    expectedPath,
                    StringComparison.OrdinalIgnoreCase) &&
                !normalizedCandidate.Equals(
                    legacyExpectedPath,
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var info = new FileInfo(normalizedCandidate);
            if (!info.Exists ||
                info.Length <= 0 ||
                (info.Attributes &
                 (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            {
                return false;
            }

            liveRecordingPath = info.FullName;
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                ArgumentException or NotSupportedException or SecurityException)
        {
            return false;
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

                    if (configuration.SessionMode == CaptureSessionMode.Recording &&
                        Volatile.Read(ref _saveOperationPending) != 0)
                    {
                        // StopAndSave captured its click-time boundary before it
                        // waited for this maintenance owner. Yield the gates
                        // without renewing or extending Recorder past that
                        // request.
                        RefreshBufferState();
                        return false;
                    }

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

                    var recorderStopWonRegistration = false;
                    if (configuration.SessionMode == CaptureSessionMode.Recording)
                    {
                        lock (_fileGate)
                        {
                            recorderStopWonRegistration =
                                Volatile.Read(ref _saveOperationPending) != 0;
                            if (!recorderStopWonRegistration)
                            {
                                // This exchange and StopAndSave's intent/process
                                // registration share _fileGate. Whichever owns
                                // it first establishes the authoritative side
                                // of the race without a check-then-act window.
                                InvalidateLiveRecordingFastPath(
                                    "The capture process was renewed; trusted recovery segments remain authoritative.");
                            }
                        }
                    }

                    if (recorderStopWonRegistration)
                    {
                        // StopAndSave captured and signaled the exact process.
                        // Yield lifecycle ownership without replacing its
                        // continuous direct output.
                        RefreshBufferState();
                        return false;
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

                    if (configuration.SessionMode == CaptureSessionMode.Recording &&
                        Volatile.Read(ref _saveOperationPending) != 0)
                    {
                        // The Stop request raced the final pre-refresh check and
                        // closed the old process. Never launch a post-click
                        // replacement generation; StopCore will transfer the
                        // already-closed recovery state as soon as this gate is
                        // released.
                        Volatile.Write(ref _isRunning, 0);
                        return false;
                    }

                    if (configuration.SessionMode == CaptureSessionMode.Recording)
                    {
                        TryDeleteDirectRecordingArtifacts(_segmentDirectory);
                        _liveRecordingPath = null;
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

                    await EnsureRecordingRecoveryCheckpointAsync(cancellationToken)
                        .ConfigureAwait(false);

                    if (configuration.SessionMode == CaptureSessionMode.Recording &&
                        Volatile.Read(ref _saveOperationPending) != 0)
                    {
                        // The old process is already closed and no replacement
                        // resources exist yet. Leave the stopped segment state
                        // for StopCore instead of extending capture after click.
                        Volatile.Write(ref _isRunning, 0);
                        return false;
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
                    var recorderStopPreventedReplacement = false;
                    lock (_fileGate)
                    {
                        recorderStopPreventedReplacement =
                            configuration.SessionMode ==
                                CaptureSessionMode.Recording &&
                            Volatile.Read(ref _saveOperationPending) != 0;
                        if (!recorderStopPreventedReplacement)
                        {
                            try
                            {
                                if (!replacement.Start())
                                {
                                    throw new InvalidOperationException(
                                        "Windows could not renew the capture engine.");
                                }

                                replacementStartedProcess = true;
                                _captureProcessJob =
                                    CaptureProcessJob.Attach(replacement);
                                _captureProcess = replacement;
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
                        }
                    }

                    if (recorderStopPreventedReplacement)
                    {
                        replacement.Dispose();
                        await AbortRecorderRefreshForPendingSaveAsync(configuration)
                            .ConfigureAwait(false);
                        return false;
                    }

                    if (await AbortRecorderRefreshForPendingSaveAsync(configuration)
                            .ConfigureAwait(false))
                    {
                        return false;
                    }

                    if (!ProcessTuning.TryApplyCapturePriority(
                            replacement,
                            replacementStrategy,
                            CaptureGeometry.ResolveOutputSize(configuration).RequiresScaling,
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
                        var connectionTask = Task.WhenAll(_audioPipes
                            .Select(pipe => pipe.ConnectAndStartAsync(_sessionCancellation.Token))
                            .ToArray());
                        try
                        {
                            if (!await WaitForRefreshStageUnlessRecorderStopAsync(
                                    connectionTask,
                                    TimeSpan.FromSeconds(15),
                                    configuration,
                                    cancellationToken)
                                .ConfigureAwait(false))
                            {
                                await AbortRecorderRefreshForPendingSaveAsync(
                                        configuration)
                                    .ConfigureAwait(false);
                                return false;
                            }
                        }
                        catch when (
                            !cancellationToken.IsCancellationRequested &&
                            HasProcessExitedSafely(replacement))
                        {
                            replacementCaptureLaunchFailed = true;
                            throw;
                        }
                    }

                    if (!await WaitForRefreshStageUnlessRecorderStopAsync(
                            Task.Delay(500, cancellationToken),
                            TimeSpan.FromSeconds(1),
                            configuration,
                            cancellationToken)
                        .ConfigureAwait(false))
                    {
                        await AbortRecorderRefreshForPendingSaveAsync(configuration)
                            .ConfigureAwait(false);
                        return false;
                    }
                    if (replacement.HasExited)
                    {
                        replacementCaptureLaunchFailed = true;
                        throw new InvalidOperationException(BuildCaptureFailureMessage());
                    }

                    if (!ProcessTuning.TryApplyCapturePriority(
                            replacement,
                            replacementStrategy,
                            CaptureGeometry.ResolveOutputSize(configuration).RequiresScaling,
                            replacementPerformanceProfile))
                    {
                        EnqueueDiagnostic(
                            "Windows did not allow ClipForge to finalize the renewed capture priority policy.");
                    }

                    var recorderStopPreventedReplacementCommit = false;
                    lock (_fileGate)
                    {
                        recorderStopPreventedReplacementCommit =
                            configuration.SessionMode ==
                                CaptureSessionMode.Recording &&
                            Volatile.Read(ref _saveOperationPending) != 0;
                        if (!recorderStopPreventedReplacementCommit)
                        {
                            // Commit the replacement's running state under the
                            // same lock used by StopAndSave's boundary/process
                            // registration. Stop can therefore never capture a
                            // half-registered replacement generation.
                            InvalidateCaptureRecoveryRetry();
                            _activeCaptureStrategy = replacementStrategy;
                            _activeCapturePerformanceProfile =
                                replacementPerformanceProfile;
                            _activeEncoderDescription =
                                replacementStrategy.Description;
                            Volatile.Write(ref _isStopping, 0);
                            Volatile.Write(ref _isRunning, 1);
                        }
                    }

                    if (recorderStopPreventedReplacementCommit)
                    {
                        await AbortRecorderRefreshForPendingSaveAsync(configuration)
                            .ConfigureAwait(false);
                        return false;
                    }

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
        if (_sessionMode == CaptureSessionMode.Recording)
        {
            throw new InvalidOperationException(
                "Recorder does not use the Instant Replay length.");
        }

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
            if (_sessionMode == CaptureSessionMode.Recording)
            {
                RefreshSegmentIndexLocked();
                newlyBlocked = InvalidateRecordingTailOnceLocked(
                    GetRecordingInvalidationSegmentCount());
            }
            else
            {
                newlyBlocked =
                    _exportBlockedCaptureGeneration != blockedGeneration;
                _exportBlockedCaptureGeneration = blockedGeneration;
                if (_activeSaveCaptureGeneration == blockedGeneration)
                {
                    invalidatedSave = _activeSaveInvalidation;
                }
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

        if (_sessionMode == CaptureSessionMode.Recording)
        {
            InvalidateLiveRecordingFastPath(
                $"A display/graphics transition invalidated the current recording tail: {diagnostic}");
        }

        RefreshBufferState();
        if (newlyBlocked)
        {
            EnqueueDiagnostic(
                _sessionMode == CaptureSessionMode.Recording
                    ? $"Quarantined the recent recording tail immediately after a display/graphics transition: {diagnostic}"
                    : $"Blocked capture generation {blockedGeneration} immediately after a display/graphics transition: {diagnostic}");
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

    internal bool CancelActiveReplaySaveForStorageSafety(
        int expectedSessionIdentity,
        long expectedReplaySaveIdentity,
        string? expectedSaveDirectory = null)
    {
        CancellationTokenSource? activeSave = null;
        lock (_fileGate)
        {
            if (_sessionMode != CaptureSessionMode.InstantReplay ||
                Volatile.Read(ref _captureSessionIdentity) !=
                    expectedSessionIdentity ||
                expectedReplaySaveIdentity <= 0 ||
                Volatile.Read(ref _activeReplaySaveIdentity) !=
                    expectedReplaySaveIdentity ||
                Volatile.Read(ref _isSaving) == 0 ||
                _activeSaveInvalidation is null ||
                expectedSaveDirectory is not null &&
                !string.Equals(
                    expectedSaveDirectory,
                    _activeReplaySaveDirectory,
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            Volatile.Write(
                ref _activeReplaySaveStorageSafetyCancellation,
                1);
            activeSave = _activeSaveInvalidation;
        }

        try
        {
            // Save owns _saveGate. Cancel it before a safety Stop waits on that
            // gate, otherwise a low-space export could run for its full timeout
            // while both its partial file and protected ring continue growing.
            activeSave.Cancel();
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    public async Task<string> SaveClipAsync(
        TimeSpan requestedDuration,
        string saveDirectory,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(saveDirectory);
        if (_sessionMode == CaptureSessionMode.Recording)
        {
            throw new InvalidOperationException(
                "Stop and save the Recorder session instead of saving an Instant Replay clip.");
        }
        if (requestedDuration <= TimeSpan.Zero ||
            requestedDuration > TimeSpan.FromHours(1) ||
            requestedDuration.Ticks %
                TimeSpan.FromSeconds(FfmpegArgumentBuilder.SegmentSeconds).Ticks != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(requestedDuration),
                "Clip length must be a whole two-second segment between two seconds and one hour.");
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
        long selectedSegmentBytes = 0;
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
                    foreach (var entry in selectedEntries)
                    {
                        selectedSegmentBytes = selectedSegmentBytes >
                            long.MaxValue - entry.Length
                                ? long.MaxValue
                                : selectedSegmentBytes + entry.Length;
                    }
                }

                foreach (var path in selectedSegments)
                {
                    _protectedSegments.Add(path);
                }

                _activeSaveCaptureGeneration = selectedGeneration;
                _activeSaveInvalidation = saveCancellation;
                Volatile.Write(
                    ref _activeReplaySaveIdentity,
                    Interlocked.Increment(ref _replaySaveIdentitySequence));
                Volatile.Write(
                    ref _activeReplaySaveDirectory,
                    Path.GetFullPath(saveDirectory));
                Volatile.Write(
                    ref _activeReplaySaveStorageSafetyCancellation,
                    0);
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
            var replayBufferRoot = ActiveBufferRoot;
            var sharesBufferVolume = ArePathsOnSameVolume(
                saveDirectory,
                replayBufferRoot);
            var estimatedRollingOverlapBytes = _activeConfiguration is
            { } activeConfiguration
                    ? StorageEstimator.EstimateBufferBytes(
                        activeConfiguration.Display,
                        activeConfiguration.Resolution,
                        activeConfiguration.FramesPerSecond,
                        actualDuration,
                        activeConfiguration.CaptureSystemAudio ||
                        activeConfiguration.CaptureMicrophone)
                    : 0;
            var rollingOverlapBytes = Math.Max(
                selectedSegmentBytes,
                estimatedRollingOverlapBytes);
            var availableSaveBytes = TryGetAvailableFreeSpace(saveDirectory);
            var availableBufferBytes = sharesBufferVolume
                ? availableSaveBytes
                : TryGetAvailableFreeSpace(replayBufferRoot);
            var requiredSaveBytes = RecordingStoragePolicy
                .GetRequiredReplaySaveFreeBytes(
                    selectedSegmentBytes,
                    rollingOverlapBytes,
                    sharesBufferVolume);
            var requiredBufferBytes = RecordingStoragePolicy
                .GetRequiredReplaySaveBufferFreeBytes(
                    rollingOverlapBytes);
            if (availableSaveBytes is null ||
                availableSaveBytes.Value <= requiredSaveBytes ||
                availableBufferBytes is null ||
                availableBufferBytes.Value <= requiredBufferBytes)
            {
                throw new IOException(
                    $"Saving this replay needs about " +
                    (sharesBufferVolume
                        ? $"{StorageEstimator.FormatBytes(requiredSaveBytes)} free on the shared replay/save drive. "
                        : $"{StorageEstimator.FormatBytes(requiredSaveBytes)} free on the selected drive and " +
                          $"{StorageEstimator.FormatBytes(requiredBufferBytes)} free on the replay-buffer drive. ") +
                    "The rolling buffer is still running, so you can free space and try again.");
            }

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
            var canceledForStorageSafety =
                exception is OperationCanceledException &&
                !cancellationToken.IsCancellationRequested &&
                Volatile.Read(
                    ref _activeReplaySaveStorageSafetyCancellation) != 0;
            var reportedException = canceledForStorageSafety
                ? new IOException(
                    "ClipForge canceled this replay export before its drive ran out of safe space. " +
                    "No incomplete clip was kept.",
                    exception)
                : exception is OperationCanceledException &&
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
                    Volatile.Write(ref _activeReplaySaveIdentity, 0);
                    Volatile.Write(ref _activeReplaySaveDirectory, null);
                    Volatile.Write(
                        ref _activeReplaySaveStorageSafetyCancellation,
                        0);
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

    public async Task<string> StopAndSaveRecordingAsync(
        string saveDirectory,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(saveDirectory);
        RecordingStopBoundary? recordingStopBoundary;
        RecordingStopRequest? recordingStopRequest;
        lock (_fileGate)
        {
            if (Interlocked.CompareExchange(ref _saveOperationPending, 1, 0) != 0)
            {
                throw new InvalidOperationException(
                    "A capture export is already running. ClipForge ignored the duplicate request.");
            }

            // This lock is the Recorder stop/refresh registration handshake.
            // A refresh either commits to invalidating/replacing the current
            // process first, or this Stop intent captures and signals the exact
            // registered process before refresh can proceed.
            recordingStopBoundary = CaptureRecordingStopBoundary();
            recordingStopRequest = BeginRecordingStopRequest(
                recordingStopBoundary);
        }

        var gateCancellationToken = recordingStopBoundary is null
            ? cancellationToken
            : CancellationToken.None;
        var enteredSaveGate = false;
        var enteredLifecycleGate = false;
        DetachedRecordingBuffer? detachedBuffer = null;
        string? manifestPath = null;
        string? shortRecordingManifestPath = null;
        string? partialPath = null;
        var finalizationAccepted = false;
        var recordingCommitted = false;
        var finalizationStartedAt = Stopwatch.GetTimestamp();
        try
        {
            await SwitchToThreadPool();
            await _saveGate.WaitAsync(gateCancellationToken).ConfigureAwait(false);
            enteredSaveGate = true;
            await _lifecycleGate.WaitAsync(gateCancellationToken).ConfigureAwait(false);
            enteredLifecycleGate = true;
            var hasActiveRecording =
                _sessionMode == CaptureSessionMode.Recording &&
                !string.IsNullOrWhiteSpace(_segmentDirectory);
            var exactProcessExitedForRequestedStop =
                recordingStopRequest is not null &&
                recordingStopRequest.SessionIdentity ==
                    Volatile.Read(ref _captureSessionIdentity) &&
                ReferenceEquals(
                    recordingStopRequest.CaptureProcess,
                    _captureProcess);
            if (hasActiveRecording &&
                (_captureProcess is null || HasProcessExitedSafely(_captureProcess)) &&
                !exactProcessExitedForRequestedStop)
            {
                InvalidateLiveRecordingFastPath(
                    "The capture process was no longer running when Recorder finalization began.");
            }
            detachedBuffer = _pendingDetachedRecordingBuffer;
            if (!hasActiveRecording &&
                detachedBuffer is { SourceAvailable: false })
            {
                detachedBuffer = await RehydrateDetachedRecordingAsync(
                        detachedBuffer,
                        cancellationToken)
                    .ConfigureAwait(false);
                Volatile.Write(
                    ref _pendingDetachedRecordingBuffer,
                    detachedBuffer);
            }

            if (!hasActiveRecording && detachedBuffer is null)
            {
                throw new InvalidOperationException(
                    "Recorder is not running and there is no stopped recording waiting to be saved.");
            }

            finalizationAccepted = true;
            Volatile.Write(ref _isSaving, 1);
            Publish(_state with
            {
                State = ReplayState.Saving,
                Message = "Stopping capture and finalizing the recording…"
            });
            RecordCaptureRuntimeEvent(
                "recording_finalize_started",
                _captureProcess,
                detail: "Closing Recorder at the requested Stop moment before using its live MP4 or completed recovery segments.");

            if (hasActiveRecording)
            {
                detachedBuffer = await StopCoreAsync(
                        deleteBuffer: false,
                        publishStopped: false,
                        detachRecordingBuffer: true,
                        recordingStopBoundary: recordingStopBoundary,
                        recordingStopRequest: recordingStopRequest)
                    .ConfigureAwait(false);
                if (detachedBuffer is not null)
                {
                    Volatile.Write(
                        ref _pendingDetachedRecordingBuffer,
                        detachedBuffer);
                }
            }

            _lifecycleGate.Release();
            enteredLifecycleGate = false;

            if (detachedBuffer is null ||
                !HasSafeRecordingSource(detachedBuffer))
            {
                if (detachedBuffer is not null)
                {
                    Publish(new ReplayStateSnapshot(
                        ReplayState.Faulted,
                        TimeSpan.Zero,
                        RecordingStoragePolicy.NoReplayRetention,
                        detachedBuffer.SegmentBytes,
                        "Recorder stopped before a complete safe segment was available. " +
                        "The incomplete source was preserved; use Discard incomplete only if you no longer need it."));
                    finalizationAccepted = false;
                }

                throw new InvalidOperationException(
                    "Recorder stopped before it had enough video to save safely. " +
                    "The incomplete source was preserved and was not deleted.");
            }

            // Persist the exact trusted ordering before any operation that can
            // fail (capacity check, engine lookup, remux, validation). It is a
            // directly reusable concat manifest and survives failed retries.
            if (!IsSafeRecordingSaveDestination(
                    saveDirectory,
                    detachedBuffer.BufferRoot,
                    detachedBuffer.SessionDirectory))
            {
                throw new InvalidOperationException(
                    "Choose a regular folder outside ClipForge's Recorder working directory. " +
                    "The recoverable session was preserved and was not used as its own save destination.");
            }

            manifestPath = await PersistRecordingRecoveryStateAsync(
                    detachedBuffer,
                    cancellationToken)
                .ConfigureAwait(false);

            Directory.CreateDirectory(saveDirectory);
            var finalPath = GetUniqueClipPath(saveDirectory);
            var segmentDuration = detachedBuffer.SegmentTimelineDuration ??
                TimeSpan.FromSeconds(
                    (long)detachedBuffer.SegmentPaths.Count *
                    detachedBuffer.SegmentDurationSeconds);
            var ffprobePath = _ffmpegSetupService.FindProbeExecutable()
                ?? throw new InvalidOperationException(
                    "The clip validator is no longer available. The recording session was preserved.");
            string? liveRecordingPath = null;
            string? liveRecordingPrefixPath = null;
            TimeSpan? liveRecordingDuration = null;
            string? liveRecordingFingerprint = null;
            string? copiedLiveRecordingFingerprint = null;
            string? liveRecordingPrefixFingerprint = null;
            TimeSpan? recoveredLiveRecordingDuration = null;
            var expectsShortRecordingPair = !string.IsNullOrWhiteSpace(
                detachedBuffer.LiveRecordingPrefixPath);
            var expectsShortRecordingNormalization =
                detachedBuffer.LiveRecordingNeedsCfrNormalization;
            var resolvedShortRecordingPrefix =
                expectsShortRecordingPair &&
                TryResolveLiveRecordingPrefixOutput(
                    detachedBuffer.SessionDirectory,
                    detachedBuffer.LiveRecordingPrefixPath,
                    out var resolvedLiveRecordingPrefixPath)
                    ? resolvedLiveRecordingPrefixPath
                    : null;
            if (TryResolveLiveRecordingOutput(
                    detachedBuffer.SessionDirectory,
                    detachedBuffer.LiveRecordingPath,
                    detachedBuffer.LiveRecordingExpectedDuration?.TotalSeconds,
                    detachedBuffer.LiveRecordingFastPathEligible,
                    out var resolvedLiveRecordingPath,
                    out var resolvedLiveRecordingDuration) &&
                resolvedLiveRecordingPath is not null &&
                resolvedLiveRecordingDuration is not null)
            {
                if (expectsShortRecordingNormalization &&
                    (!expectsShortRecordingPair ||
                     resolvedShortRecordingPrefix is not null))
                {
                    liveRecordingPrefixPath = resolvedShortRecordingPrefix;
                    liveRecordingPath = resolvedLiveRecordingPath;
                    liveRecordingDuration = resolvedLiveRecordingDuration;
                    liveRecordingPrefixFingerprint =
                        resolvedShortRecordingPrefix is null
                            ? null
                            :
                        RecordingRecoveryJournal.ComputeOutputFingerprint(
                            resolvedShortRecordingPrefix);
                    liveRecordingFingerprint =
                        RecordingRecoveryJournal.ComputeOutputFingerprint(
                            resolvedLiveRecordingPath);
                }
                else if (!expectsShortRecordingNormalization &&
                         !expectsShortRecordingPair)
                {
                    try
                    {
                        RecordCaptureRuntimeEvent(
                            "recording_live_output_validation_started",
                            detail: string.Create(
                                CultureInfo.InvariantCulture,
                                $"bytes={GetFileLengthSafely(resolvedLiveRecordingPath)}; " +
                                $"expectedSeconds={resolvedLiveRecordingDuration.Value.TotalSeconds:0.###}."),
                            includeResourceSnapshots: false);
                        await ValidateExportAsync(
                                ffprobePath,
                                resolvedLiveRecordingPath,
                                resolvedLiveRecordingDuration.Value,
                                detachedBuffer.FramesPerSecond,
                                detachedBuffer.HasAudio,
                                cancellationToken,
                                TimeSpan.FromMinutes(2),
                                durationToleranceOverride:
                                    TimeSpan.FromSeconds(
                                        FfmpegArgumentBuilder.SegmentSeconds / 2d +
                                        0.25))
                            .ConfigureAwait(false);
                        liveRecordingPath = resolvedLiveRecordingPath;
                        liveRecordingDuration = resolvedLiveRecordingDuration;
                        liveRecordingFingerprint =
                            RecordingRecoveryJournal.ComputeOutputFingerprint(
                                resolvedLiveRecordingPath);
                    }
                    catch (MediaTimelineValidationException exception)
                    {
                        EnqueueDiagnostic(
                            $"Recorder live output was rejected; using the recoverable segments instead: " +
                            exception.GetBaseException().Message);
                        RecordCaptureRuntimeEvent(
                            "recording_live_output_rejected",
                            detail: exception.GetBaseException().Message,
                            includeResourceSnapshots: false);
                        if (detachedBuffer.SegmentPaths.Count == 0 &&
                            string.IsNullOrWhiteSpace(
                                detachedBuffer.LiveRecordingRecoveryPath))
                        {
                            var hasRecoveryJournal =
                                !string.IsNullOrWhiteSpace(
                                    detachedBuffer.RecoveryJournalPath);
                            var hasRecoveryIdentity =
                                !string.IsNullOrWhiteSpace(
                                    detachedBuffer.RecoverySessionId);
                            if (hasRecoveryJournal != hasRecoveryIdentity ||
                                hasRecoveryJournal &&
                                !RecordingRecoveryJournal
                                    .TryPersistLiveOutputInvalidationMarker(
                                        detachedBuffer.SessionDirectory,
                                        detachedBuffer.RecoverySessionId!))
                            {
                                throw new IOException(
                                    "Recorder could not durably quarantine the rejected live output. " +
                                    "The source was preserved for another retry.",
                                    exception);
                            }

                            detachedBuffer = detachedBuffer with
                            {
                                LiveRecordingExpectedDuration = null,
                                LiveRecordingFastPathEligible = false,
                                LiveRecordingNeedsCfrNormalization = false
                            };
                            Volatile.Write(
                                ref _pendingDetachedRecordingBuffer,
                                detachedBuffer);
                            manifestPath = await PersistRecordingRecoveryStateAsync(
                                    detachedBuffer,
                                    cancellationToken)
                                .ConfigureAwait(false);
                            throw new InvalidDataException(
                                "Recorder's only live output failed media validation. " +
                                "It was preserved, but contains no verified safe video; " +
                                "use Discard incomplete if you no longer need it.",
                                exception);
                        }
                    }
                    catch (Exception exception) when (
                        exception is IOException or TimeoutException &&
                        detachedBuffer.SegmentPaths.Count > 0)
                    {
                        // A non-zero/timeout ffprobe result can be transient (for
                        // example an antivirus lock), so it must remain retryable
                        // when the live MP4 is the only source. With verified MKV
                        // checkpoints available, however, do not let an optional
                        // direct-output probe strand an otherwise saveable session.
                        EnqueueDiagnostic(
                            $"Recorder could not read its optional live output; using recovery segments instead: " +
                            exception.GetBaseException().Message);
                        RecordCaptureRuntimeEvent(
                            "recording_live_output_probe_failed",
                            detail: exception.GetBaseException().Message,
                            includeResourceSnapshots: false);
                    }
                }
            }

            if (expectsShortRecordingNormalization &&
                liveRecordingPath is null &&
                (IsDefinitelyMissingOrUnsafeRecordingComponent(
                     detachedBuffer.LiveRecordingPath) ||
                 expectsShortRecordingPair &&
                 IsDefinitelyMissingOrUnsafeRecordingComponent(
                     detachedBuffer.LiveRecordingPrefixPath)))
            {
                // A legacy short recording needs every declared MP4 component.
                // A component that is definitely gone cannot become valid on a
                // retry; durably invalidate that direct path so same-process and
                // startup recovery agree, and let verified MKVs take over.
                (detachedBuffer, manifestPath) =
                    await QuarantineDetachedLiveRecordingOutputAsync(
                            detachedBuffer,
                            cancellationToken)
                        .ConfigureAwait(false);
                expectsShortRecordingPair = false;
                expectsShortRecordingNormalization = false;
                if (detachedBuffer.SegmentPaths.Count == 0)
                {
                    throw new InvalidDataException(
                        "Recorder's incomplete short MP4 is missing a required component. " +
                        "No verified recovery segment is available; the remaining source was " +
                        "preserved and can now be discarded if it is no longer needed.");
                }
            }

            var usedShortRecordingOutput =
                expectsShortRecordingNormalization &&
                liveRecordingPath is not null &&
                (!expectsShortRecordingPair ||
                 liveRecordingPrefixPath is not null);
            var usedLiveRecordingOutput =
                liveRecordingPath is not null &&
                !usedShortRecordingOutput;
            var usedLiveRecordingAtomicMove =
                usedLiveRecordingOutput &&
                ArePathsOnSameVolume(liveRecordingPath!, finalPath);
            var shortRecordingOutputCreated = false;
            var recoveredInterruptedLiveOutput = false;
            if (!usedLiveRecordingAtomicMove)
            {
                var interruptedLiveBytes =
                    TryResolveInterruptedLiveRecordingOutput(
                        detachedBuffer.SessionDirectory,
                        detachedBuffer.LiveRecordingRecoveryPath,
                        out var interruptedLiveRecordingPath) &&
                    interruptedLiveRecordingPath is not null
                        ? GetFileLengthSafely(interruptedLiveRecordingPath)
                        : 0;
                var directRecordingBytes = liveRecordingPath is null
                    ? 0
                    : GetFileLengthSafely(liveRecordingPath);
                if (liveRecordingPrefixPath is not null)
                {
                    var prefixBytes = GetFileLengthSafely(
                        liveRecordingPrefixPath);
                    directRecordingBytes = directRecordingBytes >
                        long.MaxValue - prefixBytes
                            ? long.MaxValue
                            : directRecordingBytes + prefixBytes;
                }
                var finalizationSourceBytes = Math.Max(
                    Math.Max(
                        detachedBuffer.SegmentBytes,
                        interruptedLiveBytes),
                    directRecordingBytes);
                var availableFreeBytes = TryGetAvailableFreeSpace(saveDirectory);
                var requiredFreeBytes = RecordingStoragePolicy
                    .GetRequiredFinalizationFreeBytes(finalizationSourceBytes);
                if (availableFreeBytes is null || availableFreeBytes < requiredFreeBytes)
                {
                    throw new IOException(
                        $"Recorder stopped safely, but the drive needs about " +
                        $"{StorageEstimator.FormatBytes(requiredFreeBytes)} free to create the final MP4. " +
                        $"The recoverable session is preserved at {detachedBuffer.SessionDirectory}.");
                }

                partialPath = Path.Combine(
                    saveDirectory,
                    $".{Path.GetFileNameWithoutExtension(finalPath)}-{Guid.NewGuid():N}.recording.partial.mp4");
                if (usedShortRecordingOutput)
                {
                    try
                    {
                        var ffmpegPath = _ffmpegSetupService.FindExecutable()
                            ?? throw new InvalidOperationException(
                                "The capture engine is no longer available. The recording session was preserved.");
                        if (liveRecordingPrefixPath is not null)
                        {
                            await ValidateExportAsync(
                                    ffprobePath,
                                    liveRecordingPrefixPath,
                                    TimeSpan.FromSeconds(
                                        FfmpegArgumentBuilder.SegmentSeconds),
                                    detachedBuffer.FramesPerSecond,
                                    detachedBuffer.HasAudio,
                                    cancellationToken,
                                    TimeSpan.FromMinutes(2),
                                    durationToleranceOverride:
                                        TimeSpan.FromSeconds(0.35))
                                .ConfigureAwait(false);
                        }

                        var minimumFrameDuration = TimeSpan.FromSeconds(
                            1d / detachedBuffer.FramesPerSecond);
                        var expectedRemainderDuration =
                            liveRecordingPrefixPath is null
                                ? liveRecordingDuration!.Value
                                : liveRecordingDuration!.Value -
                                  TimeSpan.FromSeconds(
                                      FfmpegArgumentBuilder.SegmentSeconds);
                        if (expectedRemainderDuration < minimumFrameDuration)
                        {
                            expectedRemainderDuration = minimumFrameDuration;
                        }
                        await ValidateExportAsync(
                                ffprobePath,
                                liveRecordingPath!,
                                expectedRemainderDuration,
                                detachedBuffer.FramesPerSecond,
                                detachedBuffer.HasAudio,
                                cancellationToken,
                                TimeSpan.FromMinutes(2),
                                durationToleranceOverride:
                                    TimeSpan.FromSeconds(0.35))
                            .ConfigureAwait(false);
                        shortRecordingManifestPath = Path.Combine(
                            detachedBuffer.SessionDirectory,
                            $".clipforge-short-recording-{Guid.NewGuid():N}.ffconcat");
                        await File.WriteAllLinesAsync(
                                shortRecordingManifestPath,
                                liveRecordingPrefixPath is null
                                    ? BuildSingleRecordingConcatManifestLines(
                                        liveRecordingPath!)
                                    : BuildShortRecordingConcatManifestLines(
                                        liveRecordingPrefixPath,
                                        liveRecordingPath!),
                                new UTF8Encoding(
                                    encoderShouldEmitUTF8Identifier: false),
                                cancellationToken)
                            .ConfigureAwait(false);
                        RecordCaptureRuntimeEvent(
                            "recording_short_parts_merge_started",
                            detail: string.Create(
                                CultureInfo.InvariantCulture,
                                $"expectedSeconds={liveRecordingDuration!.Value.TotalSeconds:0.###}."),
                            includeResourceSnapshots: false);
                        var arguments =
                            FfmpegArgumentBuilder.BuildShortRecordingConcatArguments(
                                shortRecordingManifestPath,
                                partialPath,
                                detachedBuffer.FramesPerSecond,
                                detachedBuffer.HasAudio);
                        await RunRecordingExportProcessAsync(
                                ffmpegPath,
                                arguments,
                                partialPath,
                                cancellationToken)
                            .ConfigureAwait(false);
                        await ValidateExportAsync(
                                ffprobePath,
                                partialPath,
                                liveRecordingDuration.Value,
                                detachedBuffer.FramesPerSecond,
                                detachedBuffer.HasAudio,
                                cancellationToken,
                                TimeSpan.FromMinutes(2),
                                durationToleranceOverride: TimeSpan.FromSeconds(0.5))
                            .ConfigureAwait(false);
                        shortRecordingOutputCreated = true;
                    }
                    catch (MediaTimelineValidationException exception)
                    {
                        EnqueueDiagnostic(
                            $"Recorder's short direct output was rejected: " +
                            exception.GetBaseException().Message);
                        RecordCaptureRuntimeEvent(
                            "recording_short_parts_merge_rejected",
                            detail: exception.GetBaseException().Message,
                            includeResourceSnapshots: false);
                        TryDeleteFile(partialPath);
                        if (detachedBuffer.SegmentPaths.Count == 0 &&
                            string.IsNullOrWhiteSpace(
                                detachedBuffer.LiveRecordingRecoveryPath))
                        {
                            var hasRecoveryJournal =
                                !string.IsNullOrWhiteSpace(
                                    detachedBuffer.RecoveryJournalPath);
                            var hasRecoveryIdentity =
                                !string.IsNullOrWhiteSpace(
                                    detachedBuffer.RecoverySessionId);
                            if (hasRecoveryJournal != hasRecoveryIdentity ||
                                hasRecoveryJournal &&
                                !RecordingRecoveryJournal
                                    .TryPersistLiveOutputInvalidationMarker(
                                        detachedBuffer.SessionDirectory,
                                        detachedBuffer.RecoverySessionId!))
                            {
                                throw new IOException(
                                    "Recorder could not durably quarantine the rejected short output. " +
                                    "Both source parts were preserved for another retry.",
                                    exception);
                            }

                            detachedBuffer = detachedBuffer with
                            {
                                LiveRecordingPrefixPath = null,
                                LiveRecordingExpectedDuration = null,
                                LiveRecordingFastPathEligible = false,
                                LiveRecordingNeedsCfrNormalization = false
                            };
                            Volatile.Write(
                                ref _pendingDetachedRecordingBuffer,
                                detachedBuffer);
                            manifestPath = await PersistRecordingRecoveryStateAsync(
                                    detachedBuffer,
                                    cancellationToken)
                                .ConfigureAwait(false);
                            throw new InvalidDataException(
                                "Recorder's short direct output failed media validation. " +
                                "Both source parts were preserved; use Discard incomplete " +
                                "if you no longer need them.",
                                exception);
                        }

                        usedShortRecordingOutput = false;
                        liveRecordingPrefixPath = null;
                        liveRecordingPath = null;
                        liveRecordingDuration = null;
                    }
                    catch (RecordingAssemblyException exception) when (
                        detachedBuffer.SegmentPaths.Count > 0)
                    {
                        // Both source MP4s passed independent media validation,
                        // so a non-zero concat exit may be destination pressure,
                        // antivirus interference, or another transient. Fall
                        // back to durable MKVs when available, but never
                        // quarantine valid only-copy source media.
                        EnqueueDiagnostic(
                            $"Recorder could not assemble its validated short direct output; " +
                            $"using recovery segments instead: " +
                            exception.GetBaseException().Message);
                        TryDeleteFile(partialPath);
                        usedShortRecordingOutput = false;
                        liveRecordingPrefixPath = null;
                        liveRecordingPath = null;
                        liveRecordingDuration = null;
                    }
                }

                if (!shortRecordingOutputCreated && usedLiveRecordingOutput)
                {
                    copiedLiveRecordingFingerprint = await CopyRecordingOutputAsync(
                            liveRecordingPath!,
                            partialPath,
                            cancellationToken,
                            expectedSourceFingerprint: liveRecordingFingerprint)
                        .ConfigureAwait(false);
                }
                else if (!shortRecordingOutputCreated)
                {
                    var ffmpegPath = _ffmpegSetupService.FindExecutable()
                        ?? throw new InvalidOperationException(
                            "The capture engine is no longer available. The recording session was preserved.");

                    if (interruptedLiveRecordingPath is not null)
                    {
                        try
                        {
                            if (!TryResolveInterruptedLiveRecordingOutput(
                                    detachedBuffer.SessionDirectory,
                                    interruptedLiveRecordingPath,
                                    out var revalidatedInterruptedPath) ||
                                revalidatedInterruptedPath is null)
                            {
                                throw new SecurityException(
                                    "Recorder's interrupted live output changed before repair.");
                            }

                            RecordCaptureRuntimeEvent(
                                "recording_live_output_repair_started",
                                detail: string.Create(
                                    CultureInfo.InvariantCulture,
                                    $"bytes={GetFileLengthSafely(revalidatedInterruptedPath)}."),
                                includeResourceSnapshots: false);
                            var recoveryArguments =
                                FfmpegArgumentBuilder.BuildRecordingRecoveryArguments(
                                    revalidatedInterruptedPath,
                                    partialPath);
                            await RunRecordingExportProcessAsync(
                                    ffmpegPath,
                                    recoveryArguments,
                                    partialPath,
                                    cancellationToken)
                                .ConfigureAwait(false);
                            recoveredLiveRecordingDuration =
                                await ValidateRecoveredExportAsync(
                                        ffprobePath,
                                        partialPath,
                                        segmentDuration,
                                        detachedBuffer.FramesPerSecond,
                                        detachedBuffer.HasAudio,
                                        cancellationToken,
                                        TimeSpan.FromMinutes(2))
                                    .ConfigureAwait(false);
                            recoveredInterruptedLiveOutput = true;
                        }
                        catch (Exception exception) when (
                            exception is not OperationCanceledException &&
                            detachedBuffer.SegmentPaths.Count > 0)
                        {
                            EnqueueDiagnostic(
                                $"Recorder's interrupted live output could not be repaired; using recovery segments instead: " +
                                exception.GetBaseException().Message);
                            RecordCaptureRuntimeEvent(
                                "recording_live_output_repair_failed",
                                detail: exception.GetBaseException().Message,
                                includeResourceSnapshots: false);
                            TryDeleteFile(partialPath);
                        }
                    }

                    if (!recoveredInterruptedLiveOutput)
                    {
                        if (detachedBuffer.SegmentPaths.Count == 0)
                        {
                            throw new InvalidDataException(
                                "Recorder's interrupted live output could not be repaired. " +
                                "The source file was preserved for another retry.");
                        }

                        var arguments = FfmpegArgumentBuilder.BuildRecordingConcatArguments(
                            manifestPath,
                            partialPath,
                            segmentDuration);
                        await RunRecordingExportProcessAsync(
                                ffmpegPath,
                                arguments,
                                partialPath,
                                cancellationToken)
                            .ConfigureAwait(false);
                    }
                }

                if (!File.Exists(partialPath) || new FileInfo(partialPath).Length == 0)
                {
                    throw new InvalidDataException(
                        "The capture engine produced an empty recording. The source session was preserved.");
                }

                if (!usedLiveRecordingOutput &&
                    !usedShortRecordingOutput &&
                    !recoveredInterruptedLiveOutput)
                {
                    await ValidateExportAsync(
                            ffprobePath,
                            partialPath,
                            segmentDuration,
                            detachedBuffer.FramesPerSecond,
                            detachedBuffer.HasAudio,
                            cancellationToken,
                            TimeSpan.FromMinutes(2))
                        .ConfigureAwait(false);
                }
            }

            if (usedShortRecordingOutput &&
                (liveRecordingPrefixPath is not null &&
                 (!TryResolveLiveRecordingPrefixOutput(
                      detachedBuffer.SessionDirectory,
                      liveRecordingPrefixPath,
                      out var revalidatedLiveRecordingPrefixPath) ||
                  !string.Equals(
                      liveRecordingPrefixPath,
                      revalidatedLiveRecordingPrefixPath,
                      StringComparison.OrdinalIgnoreCase) ||
                  !string.Equals(
                      liveRecordingPrefixFingerprint,
                      RecordingRecoveryJournal.ComputeOutputFingerprint(
                          liveRecordingPrefixPath),
                      StringComparison.Ordinal)) ||
                 !TryResolveLiveRecordingOutput(
                     detachedBuffer.SessionDirectory,
                     liveRecordingPath,
                     liveRecordingDuration?.TotalSeconds,
                     eligible: true,
                     out var revalidatedShortLiveRecordingPath,
                     out _) ||
                 !string.Equals(
                     liveRecordingPath,
                     revalidatedShortLiveRecordingPath,
                     StringComparison.OrdinalIgnoreCase) ||
                 !string.Equals(
                     liveRecordingFingerprint,
                     RecordingRecoveryJournal.ComputeOutputFingerprint(
                         liveRecordingPath!),
                     StringComparison.Ordinal)))
            {
                throw new IOException(
                    "Recorder's short recording parts changed during finalization. " +
                    "The recovery session was preserved and no changed source was committed.");
            }

            if (usedLiveRecordingOutput &&
                (!TryResolveLiveRecordingOutput(
                     detachedBuffer.SessionDirectory,
                     liveRecordingPath,
                     liveRecordingDuration?.TotalSeconds,
                     eligible: true,
                     out var revalidatedLiveRecordingPath,
                     out _) ||
                 !string.Equals(
                     liveRecordingPath,
                     revalidatedLiveRecordingPath,
                     StringComparison.OrdinalIgnoreCase)))
            {
                throw new SecurityException(
                    "Recorder's live MP4 path changed after validation. The recovery segments were preserved.");
            }

            var commitSourcePath = usedLiveRecordingAtomicMove
                ? liveRecordingPath!
                : partialPath!;
            var commitSourceInfo = new FileInfo(commitSourcePath);
            var finalOutputLength = commitSourceInfo.Length;
            if (finalOutputLength <= 0 ||
                (commitSourceInfo.Attributes &
                 (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            {
                throw new InvalidDataException(
                    "Recorder's validated output changed before commit. The recovery segments were preserved.");
            }

            var finalOutputFingerprint =
                RecordingRecoveryJournal.ComputeOutputFingerprint(
                    commitSourcePath);
            if (usedLiveRecordingOutput &&
                (!string.Equals(
                     RecordingRecoveryJournal.ComputeOutputFingerprint(
                         liveRecordingPath!),
                     liveRecordingFingerprint,
                     StringComparison.Ordinal) ||
                 !string.Equals(
                     finalOutputFingerprint,
                     usedLiveRecordingAtomicMove
                         ? liveRecordingFingerprint
                         : copiedLiveRecordingFingerprint,
                     StringComparison.Ordinal)))
            {
                throw new IOException(
                    "Recorder's live output changed after media validation. " +
                    "The recovery session was preserved and the changed file was not committed.");
            }

            if (!string.IsNullOrWhiteSpace(detachedBuffer.RecoveryJournalPath) &&
                !string.IsNullOrWhiteSpace(detachedBuffer.RecoverySessionId))
            {
                await RecordingRecoveryJournal.MarkCommittingAsync(
                        _recordingRecoveryRoot,
                        detachedBuffer.RecoveryJournalPath,
                        detachedBuffer.RecoverySessionId,
                        finalPath,
                        finalOutputLength,
                        finalOutputFingerprint,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            if (usedLiveRecordingAtomicMove)
            {
                File.Move(liveRecordingPath!, finalPath);
            }
            else
            {
                File.Move(partialPath!, finalPath);
                partialPath = null;
            }

            try
            {
                var committedOutputInfo = new FileInfo(finalPath);
                if (!committedOutputInfo.Exists ||
                    committedOutputInfo.Length != finalOutputLength ||
                    (committedOutputInfo.Attributes &
                     (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0 ||
                    !string.Equals(
                        RecordingRecoveryJournal.ComputeOutputFingerprint(finalPath),
                        finalOutputFingerprint,
                        StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        "Recorder's final output changed during its atomic commit.");
                }
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or
                    InvalidDataException or ArgumentException or
                    NotSupportedException or SecurityException)
            {
                if (usedLiveRecordingAtomicMove &&
                    liveRecordingPath is not null &&
                    File.Exists(finalPath) &&
                    !File.Exists(liveRecordingPath))
                {
                    try
                    {
                        // Restore the source name when possible so a commit-time
                        // mutation cannot leave an unverified file in the library
                        // or silently discard the user's only live recording.
                        File.Move(finalPath, liveRecordingPath);
                    }
                    catch (Exception rollbackException) when (
                        rollbackException is IOException or UnauthorizedAccessException or
                            ArgumentException or NotSupportedException or SecurityException)
                    {
                        EnqueueDiagnostic(
                            $"Recorder could not restore its changed live output after a failed commit check: " +
                            rollbackException.GetBaseException().Message);
                    }
                }
                else
                {
                    TryDeleteFile(finalPath);
                }

                throw new InvalidDataException(
                    "Recorder's final output could not be proven identical after its atomic commit. " +
                    "The recovery session was preserved.",
                    exception);
            }

            if (usedLiveRecordingAtomicMove)
            {
                liveRecordingPath = null;
            }

            if (!string.IsNullOrWhiteSpace(detachedBuffer.RecoveryJournalPath) &&
                !string.IsNullOrWhiteSpace(detachedBuffer.RecoverySessionId))
            {
                try
                {
                    // The output move is the irreversible commit point. User
                    // cancellation after that point must not turn a successful
                    // save into a false failure or leave an ambiguous journal.
                    await RecordingRecoveryJournal.MarkCommittedAsync(
                            _recordingRecoveryRoot,
                            detachedBuffer.RecoveryJournalPath,
                            detachedBuffer.RecoverySessionId,
                            finalPath,
                            finalOutputLength,
                            finalOutputFingerprint,
                            CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException or
                        ArgumentException or NotSupportedException or SecurityException or
                        InvalidOperationException)
                {
                    // The durable committing record contains the exact length
                    // and sampled fingerprint. Startup can therefore recognize
                    // this already-moved output without creating a duplicate.
                    EnqueueDiagnostic(
                        $"Recorder saved successfully, but its final recovery marker could not be appended: " +
                        exception.GetBaseException().Message);
                }
            }

            _lastSavedPath = finalPath;
            recordingCommitted = true;
            Volatile.Write(ref _pendingDetachedRecordingBuffer, null);
            manifestPath = null;
            QueueCommittedRecordingCleanup(
                detachedBuffer,
                finalPath,
                finalOutputLength,
                finalOutputFingerprint);
            var savedDuration = liveRecordingDuration ??
                recoveredLiveRecordingDuration ??
                segmentDuration;
            RecordCaptureRuntimeEvent(
                "recording_finalize_completed",
                detail: string.Create(
                    CultureInfo.InvariantCulture,
                    $"elapsedMs={Stopwatch.GetElapsedTime(finalizationStartedAt).TotalMilliseconds:0}; " +
                     $"segments={detachedBuffer.SegmentPaths.Count}; " +
                     $"durationSeconds={savedDuration.TotalSeconds:0}; " +
                     $"liveOutputUsed={usedLiveRecordingOutput}; " +
                     $"shortOutputNormalized={usedShortRecordingOutput}; " +
                     $"liveOutputAtomicMove={usedLiveRecordingAtomicMove}; " +
                     $"recoveredLiveOutput={recoveredInterruptedLiveOutput}."),
                includeResourceSnapshots: false);
            Publish(new ReplayStateSnapshot(
                ReplayState.Stopped,
                TimeSpan.Zero,
                RecordingStoragePolicy.NoReplayRetention,
                0,
                "Recording saved.",
                finalPath));
            return finalPath;
        }
        catch (Exception exception)
        {
            if (!finalizationAccepted)
            {
                throw;
            }

            var recoveryDetail = detachedBuffer is null
                ? string.Empty
                : $" Recoverable source segments were preserved at {detachedBuffer.SessionDirectory}.";
            RecordCaptureRuntimeEvent(
                "recording_finalize_failed",
                detail: string.Create(
                    CultureInfo.InvariantCulture,
                    $"elapsedMs={Stopwatch.GetElapsedTime(finalizationStartedAt).TotalMilliseconds:0}; " +
                    $"error={exception.GetType().Name}."),
                includeResourceSnapshots: false);
            Publish(new ReplayStateSnapshot(
                ReplayState.Faulted,
                TimeSpan.Zero,
                RecordingStoragePolicy.NoReplayRetention,
                detachedBuffer?.SegmentBytes ?? 0,
                $"Recording finalization did not finish.{recoveryDetail}"));
            throw;
        }
        finally
        {
            if (enteredLifecycleGate)
            {
                _lifecycleGate.Release();
            }

            if (recordingCommitted)
            {
                TryDeleteFile(manifestPath);
            }

            TryDeleteFile(shortRecordingManifestPath);
            TryDeleteFile(partialPath);
            Volatile.Write(ref _isSaving, 0);
            Volatile.Write(ref _saveOperationPending, 0);
            if (enteredSaveGate)
            {
                _saveGate.Release();
            }
        }
    }

    private async Task<(DetachedRecordingBuffer Buffer, string ManifestPath)>
        QuarantineDetachedLiveRecordingOutputAsync(
            DetachedRecordingBuffer detachedBuffer,
            CancellationToken cancellationToken)
    {
        var hasRecoveryJournal = !string.IsNullOrWhiteSpace(
            detachedBuffer.RecoveryJournalPath);
        var hasRecoveryIdentity = !string.IsNullOrWhiteSpace(
            detachedBuffer.RecoverySessionId);
        if (hasRecoveryJournal != hasRecoveryIdentity ||
            hasRecoveryJournal &&
            !RecordingRecoveryJournal.TryPersistLiveOutputInvalidationMarker(
                detachedBuffer.SessionDirectory,
                detachedBuffer.RecoverySessionId!))
        {
            throw new IOException(
                "Recorder could not durably quarantine its rejected live output. " +
                "The source was preserved for another retry.");
        }

        var quarantined = detachedBuffer with
        {
            LiveRecordingPrefixPath = null,
            LiveRecordingPath = null,
            LiveRecordingExpectedDuration = null,
            LiveRecordingFastPathEligible = false,
            LiveRecordingNeedsCfrNormalization = false,
            LiveRecordingRecoveryPath = null
        };
        Volatile.Write(ref _pendingDetachedRecordingBuffer, quarantined);
        var manifestPath = await PersistRecordingRecoveryStateAsync(
                quarantined,
                cancellationToken)
            .ConfigureAwait(false);
        return (quarantined, manifestPath);
    }

    public async Task DiscardIncompleteRecordingAsync(
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await SwitchToThreadPool();
        await _saveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (IsRunning)
                {
                    throw new InvalidOperationException(
                        "Stop Recorder before discarding an incomplete session.");
                }

                var pending = Volatile.Read(ref _pendingDetachedRecordingBuffer)
                    ?? throw new InvalidOperationException(
                        "There is no incomplete Recorder session waiting to be discarded.");
                if (!pending.SourceAvailable)
                {
                    throw new IOException(
                        "Reconnect the original recording drive before discarding this session.");
                }

                if (HasSafeRecordingSource(pending))
                {
                    throw new InvalidOperationException(
                        "This Recorder session contains safe video and cannot be discarded as incomplete.");
                }

                var hasRecoveryJournal =
                    !string.IsNullOrWhiteSpace(pending.RecoveryJournalPath);
                var hasRecoveryIdentity =
                    !string.IsNullOrWhiteSpace(pending.RecoverySessionId);
                if (hasRecoveryJournal != hasRecoveryIdentity)
                {
                    throw new IOException(
                        "ClipForge could not verify the incomplete Recorder recovery identity. " +
                        "The source was preserved instead of risking a recovery ghost.");
                }

                if (hasRecoveryJournal)
                {
                    await RecordingRecoveryJournal.MarkDiscardedAsync(
                            _recordingRecoveryRoot,
                            pending.RecoveryJournalPath!,
                            pending.RecoverySessionId!,
                            cancellationToken)
                        .ConfigureAwait(false);
                }

                if (!TryDeleteBufferDirectory(
                        pending.SessionDirectory,
                        pending.BufferRoot))
                {
                    throw new IOException(
                        "ClipForge could not delete the incomplete Recorder source. " +
                        "It remains preserved and can be discarded after the folder is available.");
                }

                RecordingRecoveryJournal.TryDeleteJournal(
                    pending.RecoveryJournalPath);
                Volatile.Write(ref _pendingDetachedRecordingBuffer, null);
                Publish(new ReplayStateSnapshot(
                    ReplayState.Stopped,
                    TimeSpan.Zero,
                    RecordingStoragePolicy.NoReplayRetention,
                    0,
                    "Incomplete recording discarded.",
                    _lastSavedPath));
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

    private async Task<DetachedRecordingBuffer> RehydrateDetachedRecordingAsync(
        DetachedRecordingBuffer detachedBuffer,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(detachedBuffer.RecoveryJournalPath))
        {
            throw new IOException(
                "The preserved Recorder source is unavailable. Reconnect its drive and try again.");
        }

        var recovery = await RecordingRecoveryJournal.TryReadAsync(
                _recordingRecoveryRoot,
                detachedBuffer.RecoveryJournalPath,
                cancellationToken)
            .ConfigureAwait(false);
        if (recovery is null ||
            !string.Equals(
                recovery.SessionId,
                detachedBuffer.RecoverySessionId,
                StringComparison.Ordinal) ||
            !recovery.SourceAvailable)
        {
            throw new IOException(
                "The preserved Recorder source drive is still unavailable. Reconnect the original drive and select Retry save.");
        }

        var exactRecovery = await TryLoadExactRecordingRecoveryAsync(
                recovery,
                cancellationToken)
            .ConfigureAwait(false);
        if (exactRecovery is not null)
        {
            return exactRecovery;
        }

        return detachedBuffer with
        {
            SegmentPaths = recovery.SegmentPaths,
            SegmentBytes = recovery.SegmentBytes,
            FramesPerSecond = recovery.FramesPerSecond,
            HasAudio = recovery.HasAudio,
            SourceAvailable = true,
            MissingSegmentCount = recovery.MissingSegmentCount,
            RecoveryWasDetached = recovery.Detached,
            SegmentTimelineDuration = recovery.SegmentTimelineDuration,
            LiveRecordingRecoveryPath =
                recovery.LiveRecordingRecoveryPath,
            SegmentDurationSeconds = recovery.SegmentDurationSeconds
        };
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
                    var preserveRecording =
                        _sessionMode == CaptureSessionMode.Recording &&
                        !string.IsNullOrWhiteSpace(_segmentDirectory);
                    var detachedRecording = await StopCoreAsync(
                            deleteBuffer: !preserveRecording,
                            publishStopped: false,
                            detachRecordingBuffer: preserveRecording)
                        .ConfigureAwait(false);
                    if (detachedRecording is not null)
                    {
                        Volatile.Write(
                            ref _pendingDetachedRecordingBuffer,
                            detachedRecording);
                    }

                    var recoveryToPersist = detachedRecording ??
                        Volatile.Read(ref _pendingDetachedRecordingBuffer);
                    if (recoveryToPersist is not null)
                    {
                        _ = await PersistRecordingRecoveryStateAsync(
                                recoveryToPersist,
                                CancellationToken.None)
                            .ConfigureAwait(false);
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
        TimeSpan? gracefulStopTimeout = null,
        RecordingStopRequest? recordingStopRequest = null)
    {
        Volatile.Write(ref _lastCaptureCleanupFailure, null);
        Volatile.Write(ref _lastCaptureStopWasGraceful, 0);
        Volatile.Write(ref _lastStoppedCaptureProgress, null);
        var process = _captureProcess;
        var processJob = _captureProcessJob;
        var earlyStopSignal =
            recordingStopRequest is not null &&
            ReferenceEquals(recordingStopRequest.CaptureProcess, process)
                ? recordingStopRequest.SignalTask
                : null;
        var processExited = process is null;
        var processClosedGracefully = process is null;
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
                    processClosedGracefully = await StopProcessGracefullyAsync(
                            process,
                            CaptureRefreshGracefulStopTimeout,
                            earlyStopSignal)
                        .ConfigureAwait(false);
                }
                else
                {
                    processClosedGracefully = await StopProcessGracefullyAsync(
                            process,
                            gracefulStopTimeout ?? CaptureRefreshGracefulStopTimeout,
                            earlyStopSignal)
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

        Volatile.Write(
            ref _lastCaptureStopWasGraceful,
            processClosedGracefully ? 1 : 0);

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
        Volatile.Write(
            ref _lastStoppedCaptureProgress,
            Volatile.Read(ref _latestCaptureProgress));
        ResetCaptureProgress();
        _captureStarvationWatchdog = null;
        return true;
    }

    private async Task<bool> AbortRecorderRefreshForPendingSaveAsync(
        CaptureConfiguration configuration)
    {
        if (configuration.SessionMode != CaptureSessionMode.Recording ||
            Volatile.Read(ref _saveOperationPending) == 0)
        {
            return false;
        }

        // Refresh owns the lifecycle/save gates here. Finish any process or
        // pipe resources it registered after the Stop intent, then hand the
        // unchanged session directory to StopCore for durable detachment.
        Volatile.Write(ref _isStopping, 1);
        _ = await StopCaptureResourcesForRefreshAsync().ConfigureAwait(false);
        Volatile.Write(ref _isRunning, 0);
        return true;
    }

    private async Task<bool> WaitForRefreshStageUnlessRecorderStopAsync(
        Task stage,
        TimeSpan timeout,
        CaptureConfiguration configuration,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stage);
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        if (configuration.SessionMode != CaptureSessionMode.Recording)
        {
            await stage.WaitAsync(timeout, cancellationToken)
                .ConfigureAwait(false);
            return true;
        }

        var started = Stopwatch.GetTimestamp();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Volatile.Read(ref _saveOperationPending) != 0)
            {
                return false;
            }

            if (stage.IsCompleted)
            {
                await stage.ConfigureAwait(false);
                return true;
            }

            var elapsed = Stopwatch.GetElapsedTime(started);
            if (elapsed >= timeout)
            {
                throw new TimeoutException(
                    "The capture refresh stage exceeded its bounded timeout.");
            }

            var remaining = timeout - elapsed;
            var pollDelay = remaining < TimeSpan.FromMilliseconds(50)
                ? remaining
                : TimeSpan.FromMilliseconds(50);
            await Task.WhenAny(
                    stage,
                    Task.Delay(pollDelay, cancellationToken))
                .ConfigureAwait(false);
        }
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

                    MarkRecordingSegmentUntrustedLocked(index);
                    segment = _segments[index];
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

            await EnsureRecordingRecoveryCheckpointAsync(cancellationToken)
                .ConfigureAwait(false);

            if (configuration.SessionMode == CaptureSessionMode.Recording &&
                Volatile.Read(ref _saveOperationPending) != 0)
            {
                // StopAndSave already owns the stopped generation. Do not
                // rebuild the degraded process after the user clicked Stop.
                Volatile.Write(ref _isRunning, 0);
                return true;
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
            var recorderStopPreventedRestore = false;
            lock (_fileGate)
            {
                recorderStopPreventedRestore =
                    configuration.SessionMode == CaptureSessionMode.Recording &&
                    Volatile.Read(ref _saveOperationPending) != 0;
                if (!recorderStopPreventedRestore)
                {
                    try
                    {
                        if (!restoredProcess.Start())
                        {
                            throw new InvalidOperationException(
                                "Windows could not restore the previous GDI capture path.");
                        }

                        _captureProcessJob = CaptureProcessJob.Attach(restoredProcess);
                        _captureProcess = restoredProcess;
                    }
                    catch
                    {
                        TryKill(restoredProcess);
                        _captureProcessJob?.Dispose();
                        _captureProcessJob = null;
                        throw;
                    }
                }
            }

            if (recorderStopPreventedRestore)
            {
                restoredProcess.Dispose();
                restoredProcess = null;
                await AbortRecorderRefreshForPendingSaveAsync(configuration)
                    .ConfigureAwait(false);
                return true;
            }

            if (await AbortRecorderRefreshForPendingSaveAsync(configuration)
                    .ConfigureAwait(false))
            {
                return true;
            }

            _ = ProcessTuning.TryApplyCapturePriority(
                restoredProcess,
                degradedStrategy,
                CaptureGeometry.ResolveOutputSize(configuration).RequiresScaling,
                performanceProfile);
            restoredProcess.StandardInput.AutoFlush = true;
            _diagnosticTask = PumpDiagnosticsAsync(restoredProcess);
            _captureProgressTask = PumpCaptureProgressAsync(
                restoredProcess,
                configuration.Display);
            if (_audioPipes.Count > 0)
            {
                var connectionTask = Task.WhenAll(_audioPipes
                    .Select(pipe => pipe.ConnectAndStartAsync(_sessionCancellation.Token))
                    .ToArray());
                if (!await WaitForRefreshStageUnlessRecorderStopAsync(
                        connectionTask,
                        TimeSpan.FromSeconds(15),
                        configuration,
                        cancellationToken)
                    .ConfigureAwait(false))
                {
                    await AbortRecorderRefreshForPendingSaveAsync(configuration)
                        .ConfigureAwait(false);
                    return true;
                }
            }

            if (!await WaitForRefreshStageUnlessRecorderStopAsync(
                    Task.Delay(500, cancellationToken),
                    TimeSpan.FromSeconds(1),
                    configuration,
                    cancellationToken)
                .ConfigureAwait(false))
            {
                await AbortRecorderRefreshForPendingSaveAsync(configuration)
                    .ConfigureAwait(false);
                return true;
            }

            if (restoredProcess.HasExited)
            {
                throw new InvalidOperationException(BuildCaptureFailureMessage());
            }

            _ = ProcessTuning.TryApplyCapturePriority(
                restoredProcess,
                degradedStrategy,
                CaptureGeometry.ResolveOutputSize(configuration).RequiresScaling,
                performanceProfile);
            var recorderStopPreventedRestoreCommit = false;
            lock (_fileGate)
            {
                recorderStopPreventedRestoreCommit =
                    configuration.SessionMode == CaptureSessionMode.Recording &&
                    Volatile.Read(ref _saveOperationPending) != 0;
                if (!recorderStopPreventedRestoreCommit)
                {
                    _activeCaptureStrategy = degradedStrategy;
                    _activeCapturePerformanceProfile = performanceProfile;
                    _activeEncoderDescription = degradedStrategy.Description;
                    Volatile.Write(ref _isStopping, 0);
                    Volatile.Write(ref _isRunning, 1);
                }
            }

            if (recorderStopPreventedRestoreCommit)
            {
                await AbortRecorderRefreshForPendingSaveAsync(configuration)
                    .ConfigureAwait(false);
                return true;
            }

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

    private RecordingStopBoundary? CaptureRecordingStopBoundary()
    {
        if (_sessionMode != CaptureSessionMode.Recording ||
            !IsRunning ||
            string.IsNullOrWhiteSpace(_segmentDirectory))
        {
            return null;
        }

        lock (_fileGate)
        {
            if (_sessionMode != CaptureSessionMode.Recording ||
                !IsRunning ||
                string.IsNullOrWhiteSpace(_segmentDirectory))
            {
                return null;
            }

            RefreshSegmentIndexLocked();
            var completedCount = Math.Max(0, _segments.Count - 1);
            var closedSegments = _segments
                .Take(completedCount)
                .Where(segment =>
                    segment.IsTrusted &&
                    segment.IsCountedForRecording &&
                    segment.GenerationId != _exportBlockedCaptureGeneration &&
                    segment.Length > 0)
                .ToDictionary(
                    segment => segment.Path,
                    segment => segment.Length,
                    StringComparer.OrdinalIgnoreCase);
            BufferedSegment? openSegment = _segments.Count == 0
                ? null
                : _segments[^1];
            return new RecordingStopBoundary(
                Volatile.Read(ref _captureSessionIdentity),
                Path.GetFullPath(_segmentDirectory),
                closedSegments,
                openSegment?.Path,
                openSegment?.Length ?? 0,
                openSegment?.SegmentNumber ?? -1,
                openSegment?.GenerationId ?? -1,
                _activeCaptureGenerationStartSegmentNumber,
                Volatile.Read(ref _latestCaptureProgress),
                _captureProcess);
        }
    }

    private RecordingStopRequest? BeginRecordingStopRequest(
        RecordingStopBoundary? boundary)
    {
        if (boundary?.CaptureProcess is not { } process ||
            boundary.SessionIdentity != Volatile.Read(ref _captureSessionIdentity) ||
            !ReferenceEquals(process, _captureProcess) ||
            HasProcessExitedSafely(process))
        {
            return null;
        }

        // Stop is an irreversible user action. Signal the exact process before
        // waiting for maintenance/lifecycle ownership so a busy refresh cannot
        // extend a manual recording by seconds or minutes after the click.
        Volatile.Write(ref _isStopping, 1);
        return new RecordingStopRequest(
            boundary.SessionIdentity,
            process,
            SignalProcessGracefulStopAsync(process));
    }

    private static async Task<bool> SignalProcessGracefulStopAsync(
        Process process)
    {
        try
        {
            if (process.HasExited)
            {
                return process.ExitCode == 0;
            }

            using var signalTimeout =
                new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
            await process.StandardInput.WriteLineAsync("q")
                .WaitAsync(signalTimeout.Token)
                .ConfigureAwait(false);
            await process.StandardInput.FlushAsync(signalTimeout.Token)
                .ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or InvalidOperationException or
                ObjectDisposedException or OperationCanceledException or
                System.ComponentModel.Win32Exception)
        {
            // StopCore retries the signal while it owns the lifecycle gate.
            return false;
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
        timeout.CancelAfter(GetCaptureBoundaryWaitTimeout(_sessionMode));
        try
        {
            while (true)
            {
                if (_sessionMode == CaptureSessionMode.Recording &&
                    Volatile.Read(ref _saveOperationPending) != 0)
                {
                    return false;
                }

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
            // The Recorder recovery stream rotates every ten seconds while
            // Instant Replay rotates every two. A mode-aware bound gives a
            // healthy graph one full interval to reach a safe boundary without
            // waiting forever on an already stalled source.
            return false;
        }
    }

    internal static TimeSpan GetCaptureBoundaryWaitTimeout(
        CaptureSessionMode sessionMode) =>
        sessionMode == CaptureSessionMode.Recording
            ? TimeSpan.FromSeconds(
                FfmpegArgumentBuilder.RecordingRecoverySegmentSeconds + 1)
            : CaptureBoundaryWaitTimeout;

    private void DiscardNewestCaptureTailLocked()
    {
        if (_segments.Count == 0)
        {
            return;
        }

        var tailIndex = _segments.Count - 1;
        MarkRecordingSegmentUntrustedLocked(tailIndex);
        var tail = _segments[tailIndex];
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

    private async Task<DetachedRecordingBuffer?> StopCoreAsync(
        bool deleteBuffer,
        bool publishStopped,
        bool detachRecordingBuffer = false,
        RecordingStopBoundary? recordingStopBoundary = null,
        RecordingStopRequest? recordingStopRequest = null)
    {
        var stoppedConfiguration = _activeConfiguration;
        var stoppedCaptureProgress = Volatile.Read(ref _latestCaptureProgress);
        var stoppedLiveRecordingPath = _liveRecordingPath;
        var usesRequestedEarlyStop =
            recordingStopRequest is not null &&
            recordingStopRequest.SessionIdentity ==
                Volatile.Read(ref _captureSessionIdentity) &&
            ReferenceEquals(recordingStopRequest.CaptureProcess, _captureProcess);
        var ownedRunningCaptureProcess =
            _captureProcess is { } processAtStop &&
            !HasProcessExitedSafely(processAtStop);
        if (Volatile.Read(ref _liveRecordingFastPathEligible) != 0 &&
            (_captureProcess is null || HasProcessExitedSafely(_captureProcess)) &&
            !usesRequestedEarlyStop)
        {
            InvalidateLiveRecordingFastPath(
                "The capture process exited before Recorder could close the live MP4 cleanly.");
        }

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
                Message = _sessionMode == CaptureSessionMode.Recording
                    ? "Stopping Recorder…"
                    : "Stopping Instant Replay…"
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
                gracefulStopTimeout: TimeSpan.FromSeconds(5),
                recordingStopRequest: usesRequestedEarlyStop
                    ? recordingStopRequest
                    : null)
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

        stoppedCaptureProgress =
            Volatile.Read(ref _lastStoppedCaptureProgress) ??
            stoppedCaptureProgress;

        var stoppedLiveRecordingFastPathEligible =
            Volatile.Read(ref _liveRecordingFastPathEligible) != 0;
        if (stoppedLiveRecordingFastPathEligible &&
            Volatile.Read(ref _lastCaptureStopWasGraceful) == 0)
        {
            InvalidateLiveRecordingFastPath(
                "Recorder had to terminate the capture process before its live MP4 trailer was confirmed.");
            stoppedLiveRecordingFastPathEligible = false;
        }

        var oldSegmentDirectory = _segmentDirectory;
        var oldBufferRoot = _activeBufferRoot ?? _bufferRoot;
        var recordingRecoveryJournal = _recordingRecoveryJournal;
        var matchesRequestedStopBoundary =
            recordingStopBoundary is not null &&
            !string.IsNullOrWhiteSpace(oldSegmentDirectory) &&
            recordingStopBoundary.SessionIdentity ==
                Volatile.Read(ref _captureSessionIdentity) &&
            string.Equals(
                recordingStopBoundary.SessionDirectory,
                Path.GetFullPath(oldSegmentDirectory),
                StringComparison.OrdinalIgnoreCase);
        var requestedStopProgress = matchesRequestedStopBoundary
            ? recordingStopBoundary!.CaptureProgress ?? stoppedCaptureProgress
            : stoppedCaptureProgress;
        var promotedGracefulShortRecording =
            stoppedLiveRecordingFastPathEligible &&
            Volatile.Read(ref _lastCaptureStopWasGraceful) != 0 &&
            requestedStopProgress is not null &&
            !string.IsNullOrWhiteSpace(oldSegmentDirectory) &&
            !string.IsNullOrWhiteSpace(stoppedLiveRecordingPath) &&
            TryPromoteGracefulShortRecordingOutput(
                oldSegmentDirectory,
                stoppedLiveRecordingPath,
                requestedStopProgress.OutputTimeMicroseconds,
                stoppedConfiguration?.FramesPerSecond ?? 0);
        string? gracefulShortRecordingPrefixPath = null;
        var mergesGracefulShortRecordingParts =
            !promotedGracefulShortRecording &&
            stoppedLiveRecordingFastPathEligible &&
            Volatile.Read(ref _lastCaptureStopWasGraceful) != 0 &&
            requestedStopProgress is not null &&
            !string.IsNullOrWhiteSpace(oldSegmentDirectory) &&
            !string.IsNullOrWhiteSpace(stoppedLiveRecordingPath) &&
            TryResolveGracefulShortRecordingPrefixOutput(
                oldSegmentDirectory,
                stoppedLiveRecordingPath,
                requestedStopProgress.OutputTimeMicroseconds,
                stoppedConfiguration?.FramesPerSecond ?? 0,
                out gracefulShortRecordingPrefixPath);
        DetachedRecordingBuffer? detachedBuffer = null;
        if (detachRecordingBuffer &&
            _sessionMode == CaptureSessionMode.Recording &&
            stoppedConfiguration is not null &&
            !string.IsNullOrWhiteSpace(oldSegmentDirectory))
        {
            lock (_fileGate)
            {
                RefreshSegmentIndexLocked();
                // Files followed by another numbered segment are known-complete
                // ten-second checkpoints. The exact file that was open when a
                // confirmed graceful Stop began is also closed safely, but is a
                // variable-length tail, sealed in the journal and exact state
                // with its measured duration before either can be committed.
                var completedSegmentEntries = _segments
                    .Where(segment =>
                        segment.IsTrusted &&
                        segment.IsCountedForRecording &&
                        segment.GenerationId != _exportBlockedCaptureGeneration &&
                        segment.Length > 0)
                    .ToArray();
                var selectedSegmentEntries = (recordingStopBoundary is null
                        ? completedSegmentEntries
                        : matchesRequestedStopBoundary
                            ? completedSegmentEntries
                                .Where(segment =>
                                    recordingStopBoundary.ClosedSegments.TryGetValue(
                                        segment.Path,
                                        out var stoppedLength) &&
                                    stoppedLength == segment.Length)
                                .ToArray()
                            : [])
                    .ToList();

                BufferedSegment? gracefulTail = null;
                var gracefulStopOwnsTail =
                    Volatile.Read(ref _lastCaptureStopWasGraceful) != 0 &&
                    (usesRequestedEarlyStop || ownedRunningCaptureProcess);
                var tailGenerationStartNumber = -1;
                if (gracefulStopOwnsTail && matchesRequestedStopBoundary &&
                    recordingStopBoundary is
                    {
                        OpenSegmentPath: { Length: > 0 } openPath,
                        OpenSegmentNumber: >= 0,
                        OpenSegmentGenerationId: >= 0
                    })
                {
                    gracefulTail = _segments.FirstOrDefault(segment =>
                        segment.SegmentNumber ==
                            recordingStopBoundary.OpenSegmentNumber &&
                        segment.GenerationId ==
                            recordingStopBoundary.OpenSegmentGenerationId &&
                        string.Equals(
                            segment.Path,
                            openPath,
                            StringComparison.OrdinalIgnoreCase) &&
                        segment.Length >= recordingStopBoundary.OpenSegmentLength);
                    tailGenerationStartNumber =
                        recordingStopBoundary.OpenSegmentGenerationStartNumber;
                }
                else if (gracefulStopOwnsTail && recordingStopBoundary is null &&
                         _segments.Count > 0)
                {
                    gracefulTail = _segments[^1];
                    tailGenerationStartNumber =
                        _activeCaptureGenerationStartSegmentNumber;
                }

                TimeSpan? gracefulTailDuration = null;
                if (gracefulTail is { } tail &&
                    tail.IsTrusted &&
                    tail.GenerationId != _exportBlockedCaptureGeneration &&
                    tail.Length > 0 &&
                    tailGenerationStartNumber >= 0 &&
                    tail.SegmentNumber >= tailGenerationStartNumber)
                {
                    gracefulTailDuration =
                        ResolveGracefulRecordingTailDuration(
                            (recordingStopBoundary is not null
                                ? requestedStopProgress
                                : stoppedCaptureProgress)
                            ?.OutputTimeMicroseconds ?? 0,
                            tail.SegmentNumber,
                            tailGenerationStartNumber,
                            FfmpegArgumentBuilder.RecordingRecoverySegmentSeconds,
                            stoppedConfiguration.FramesPerSecond);
                    if (gracefulTailDuration is not null &&
                        selectedSegmentEntries.All(segment =>
                            !string.Equals(
                                segment.Path,
                                tail.Path,
                                StringComparison.OrdinalIgnoreCase)))
                    {
                        selectedSegmentEntries.Add(tail);
                        selectedSegmentEntries.Sort(static (left, right) =>
                            left.SegmentNumber.CompareTo(right.SegmentNumber));
                        // The segment monitor checkpoints only files followed
                        // by another numbered segment. A confirmed graceful
                        // Stop makes the still-open tail equally trustworthy,
                        // so publish that final add before the detached record.
                        // Otherwise short recordings (and every partial final
                        // ten-second interval) exist only in the exact state
                        // file and the central journal rejects its duration.
                        if (!tail.IsCountedForRecording &&
                            recordingRecoveryJournal is { } journal &&
                            !journal.RecordCompleted(
                                tail.Path,
                                tail.Length,
                                tail.SegmentNumber))
                        {
                            EnqueueDiagnostic(
                                "Recorder recovery could not queue its gracefully closed final segment checkpoint.");
                        }
                    }
                }

                if (recordingStopBoundary is not null)
                {
                    var selectedPaths = selectedSegmentEntries
                        .Select(segment => segment.Path)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                    foreach (var excluded in completedSegmentEntries.Where(
                                 segment => !selectedPaths.Contains(segment.Path)))
                    {
                        if (recordingRecoveryJournal is not null &&
                            !recordingRecoveryJournal.RecordUntrusted(
                                excluded.Path,
                                excluded.SegmentNumber))
                        {
                            EnqueueDiagnostic(
                                "Recorder could not durably exclude a recovery segment completed after Stop was requested.");
                        }
                    }
                }
                var selectedSegments = selectedSegmentEntries
                    .Select(segment => segment.Path)
                    .ToArray();
                var selectedBytes = selectedSegmentEntries
                    .Sum(segment => segment.Length);
                TimeSpan? segmentTimelineDuration = selectedSegmentEntries.Count == 0
                    ? null
                    : gracefulTailDuration is { } partialTailDuration &&
                      gracefulTail is { } selectedTail &&
                      string.Equals(
                          selectedSegmentEntries[^1].Path,
                          selectedTail.Path,
                          StringComparison.OrdinalIgnoreCase)
                        ? TimeSpan.FromSeconds(
                            checked((long)selectedSegmentEntries.Count - 1) *
                            FfmpegArgumentBuilder.RecordingRecoverySegmentSeconds) +
                          partialTailDuration
                        : TimeSpan.FromSeconds(
                            checked((long)selectedSegmentEntries.Count) *
                            FfmpegArgumentBuilder.RecordingRecoverySegmentSeconds);
                var liveRecordingIncludesSessionStart =
                    IsCanonicalContinuousLiveRecordingPath(
                        oldSegmentDirectory,
                        stoppedLiveRecordingPath);
                var recordedAfterWarmupMicroseconds =
                    requestedStopProgress is null
                        ? 0
                        : liveRecordingIncludesSessionStart ||
                          promotedGracefulShortRecording ||
                          mergesGracefulShortRecordingParts
                            ? requestedStopProgress.OutputTimeMicroseconds
                            : Math.Max(
                                0,
                                requestedStopProgress.OutputTimeMicroseconds -
                                FfmpegArgumentBuilder.SegmentSeconds * 1_000_000L);
                TimeSpan? liveRecordingExpectedDuration =
                    stoppedLiveRecordingFastPathEligible &&
                    !string.IsNullOrWhiteSpace(stoppedLiveRecordingPath) &&
                    recordedAfterWarmupMicroseconds > 0
                        ? TimeSpan.FromSeconds(
                            recordedAfterWarmupMicroseconds / 1_000_000d)
                        : selectedSegmentEntries.Count > 0 &&
                          stoppedLiveRecordingFastPathEligible &&
                          !string.IsNullOrWhiteSpace(stoppedLiveRecordingPath)
                            ? TimeSpan.FromSeconds(
                                checked((long)selectedSegmentEntries.Count) *
                                FfmpegArgumentBuilder.RecordingRecoverySegmentSeconds)
                            : null;
                string? resolvedStoppedLiveRecordingPath = null;
                TimeSpan? resolvedStoppedLiveRecordingDuration = null;
                if (liveRecordingExpectedDuration is { } expectedDuration)
                {
                    _ = TryResolveLiveRecordingOutput(
                        oldSegmentDirectory,
                        stoppedLiveRecordingPath,
                        expectedDuration.TotalSeconds,
                        stoppedLiveRecordingFastPathEligible,
                        out resolvedStoppedLiveRecordingPath,
                        out resolvedStoppedLiveRecordingDuration);
                }

                detachedBuffer = new DetachedRecordingBuffer(
                    oldSegmentDirectory,
                    oldBufferRoot,
                    selectedSegments,
                    selectedBytes,
                    stoppedConfiguration.FramesPerSecond,
                    stoppedConfiguration.CaptureSystemAudio ||
                    stoppedConfiguration.CaptureMicrophone,
                    recordingRecoveryJournal?.JournalPath,
                    recordingRecoveryJournal?.SessionId,
                    SourceAvailable: true,
                    SegmentTimelineDuration: segmentTimelineDuration,
                    LiveRecordingPrefixPath:
                        gracefulShortRecordingPrefixPath,
                    LiveRecordingPath: resolvedStoppedLiveRecordingPath,
                    LiveRecordingExpectedDuration:
                        resolvedStoppedLiveRecordingDuration,
                    LiveRecordingFastPathEligible:
                        resolvedStoppedLiveRecordingPath is not null,
                    LiveRecordingNeedsCfrNormalization:
                        promotedGracefulShortRecording ||
                        mergesGracefulShortRecordingParts,
                    SegmentDurationSeconds:
                        FfmpegArgumentBuilder.RecordingRecoverySegmentSeconds);
            }
        }

        if (detachRecordingBuffer && detachedBuffer is not null)
        {
            try
            {
                // Publish the exact direct-output identity before StopCore
                // returns and before the journal writes its detached terminal
                // record. If the process dies between those two durable writes,
                // startup can still recover the continuous output (and both
                // parts of a legacy short recording) with its exact metadata.
                _ = await PersistRecordingRecoveryStateAsync(
                        detachedBuffer,
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or
                    ArgumentException or NotSupportedException or SecurityException)
            {
                // Stop must still release capture resources. StopAndSave writes
                // the state again before finalization; a generic Stop retains
                // the central journal and reports this durability degradation.
                EnqueueDiagnostic(
                    $"Recorder could not persist its exact stopped state before returning: " +
                    exception.GetBaseException().Message);
            }
        }

        if (recordingRecoveryJournal is not null)
        {
            var journalClosed = await recordingRecoveryJournal.CloseAsync(
                    detached: detachRecordingBuffer && detachedBuffer is not null,
                    segmentTimelineDuration:
                        detachedBuffer?.SegmentTimelineDuration)
                .ConfigureAwait(false);
            _recordingRecoveryJournal = null;
            var journalFailure = recordingRecoveryJournal.WriterFailure;
            if (!journalClosed || journalFailure is not null)
            {
                EnqueueDiagnostic(
                    journalFailure is null
                        ? "Recorder recovery journal did not confirm its final checkpoint before the shutdown bound."
                        : $"Recorder recovery journal stopped early: {journalFailure.GetBaseException().Message}");
            }

            if (deleteBuffer && _sessionMode == CaptureSessionMode.Recording)
            {
                if (!journalClosed || journalFailure is not null)
                {
                    // Never remove the only source after a locator writer
                    // failed: the next launch must still be able to recover it.
                    deleteBuffer = false;
                    EnqueueDiagnostic(
                        "Recorder preserved its working directory because recovery metadata did not close durably.");
                }
                else
                {
                    try
                    {
                        await RecordingRecoveryJournal.MarkDiscardedAsync(
                                _recordingRecoveryRoot,
                                recordingRecoveryJournal.JournalPath,
                                recordingRecoveryJournal.SessionId,
                                CancellationToken.None)
                            .ConfigureAwait(false);
                    }
                    catch (Exception exception) when (
                        exception is IOException or UnauthorizedAccessException or
                            InvalidDataException or InvalidOperationException or
                            ArgumentException or NotSupportedException or SecurityException)
                    {
                        deleteBuffer = false;
                        EnqueueDiagnostic(
                            "Recorder preserved its working directory because a durable cleanup terminal could not be written: " +
                            exception.GetBaseException().Message);
                    }
                }
            }

            if (deleteBuffer ||
                !detachRecordingBuffer &&
                _sessionMode != CaptureSessionMode.Recording)
            {
                RecordingRecoveryJournal.TryDeleteJournal(
                    recordingRecoveryJournal.JournalPath);
                TryDeleteFile(Path.Combine(
                    recordingRecoveryJournal.SessionDirectory,
                    RecordingRecoveryJournal.SessionMarkerFileName));
            }
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

        _segmentDirectory = null;
        _activeBufferRoot = null;
        _liveRecordingPath = null;
        Volatile.Write(ref _liveRecordingFastPathEligible, 0);
        if (deleteBuffer)
        {
            TryDeleteBufferDirectory(oldSegmentDirectory, oldBufferRoot);
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

        return detachedBuffer;
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
        var outputRequiresScaling =
            CaptureGeometry.ResolveOutputSize(configuration).RequiresScaling;

        try
        {
            while (await healthTimer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                if (process.HasExited)
                {
                    if (strategy.CaptureBackend == DesktopCaptureBackend.DesktopDuplication &&
                        Volatile.Read(ref _isStopping) == 0 &&
                        CaptureRecoveryRequested is not null)
                    {
                        // DXGI duplication can lose access when the desktop mode,
                        // lock state, or graphics device changes. Keep the logical
                        // session owned until the existing bounded recovery handler
                        // replaces this exact exited process or stops the session.
                        RequestCaptureRecovery(
                            CaptureRecoveryReason.CaptureHang,
                            $"Desktop Duplication stopped unexpectedly. {BuildCaptureFailureMessage()}");
                        if (_captureRecoveryRequestGate.IsPending)
                        {
                            return;
                        }
                    }

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
                    if (SupportsGraphicsCaptureRecovery(strategy.CaptureBackend))
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
                if (_sessionMode == CaptureSessionMode.Recording &&
                    _recordingRecoveryJournal is { IsHealthy: false } journal)
                {
                    throw new IOException(
                        "Recorder stopped because its crash-recovery journal could no longer be written. " +
                        (journal.WriterFailure?.GetBaseException().Message ??
                         "The journal writer ended unexpectedly."));
                }

                var degradedReprobeDeadline = Volatile.Read(
                    ref _degradedCaptureReprobeNotBeforeUtcTicks);
                if (_sessionMode != CaptureSessionMode.Recording &&
                    ShouldRunDegradedCaptureReprobe(
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
                            CaptureGeometry.ResolveOutputSize(configuration).RequiresScaling,
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
                        captureProcessUptime,
                        configuration.SessionMode,
                        Volatile.Read(ref _liveRecordingFastPathEligible) != 0))
                {
                    _ = QueueScheduledCaptureRefresh(
                        process.Id,
                        string.Create(
                            CultureInfo.InvariantCulture,
                            $"The WGC capture process reached its bounded {CaptureProcessMaximumAge.TotalMinutes:0}-minute lifetime; renewing it prevents long-session frame-pool degradation."));
                }

                if (strategy.CaptureBackend is
                        DesktopCaptureBackend.WindowsGraphicsCapture or
                        DesktopCaptureBackend.DesktopDuplication or
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
                        var isGdiCapture =
                            strategy.CaptureBackend ==
                            DesktopCaptureBackend.Gdi;
                        // ddagrab repeats the last acquired texture internally on
                        // a quiet desktop. FFmpeg's CFR duplicate counter cannot
                        // distinguish those samples from new desktop content.
                        var supportsSourceCadence =
                            SupportsSourceCadenceDiagnostics(strategy.CaptureBackend);
                        var cadenceForegroundContext =
                            ResolveCaptureCadenceForegroundContext(
                                strategy.CaptureBackend,
                                envelope.ForegroundContext);
                        cadenceForegroundContext =
                            ResolveAgedRecorderCadenceForegroundContext(
                                configuration.SessionMode,
                                strategy.CaptureBackend,
                                captureProcessUptime,
                                cadenceForegroundContext);
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
                                cadenceForegroundContext,
                                captureProcessUptime,
                                allowSchedulingPressure:
                                    allowInitialProfileCadence,
                                allowOutputThroughput: true,
                                allowChronicLowCadence:
                                    supportsSourceCadence &&
                                    (isGdiCapture || allowInitialProfileCadence),
                                allowSourceCadence:
                                    supportsSourceCadence &&
                                    (isGdiCapture ||
                                     isWindowsGraphicsCapture &&
                                     !suppressNonObjectiveSourceCadence));
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
                                    $"{(SupportsSourceCadenceDiagnostics(strategy.CaptureBackend) ? "uniqueFps" : "outputFps")}={uniqueFps:0.0}; duplicateRatio={duplicateRatio:0.000}; " +
                                    $"sourceCadenceObservable={SupportsSourceCadenceDiagnostics(strategy.CaptureBackend)}; " +
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
                        var objectiveGdiCadence =
                            strategy.CaptureBackend ==
                                DesktopCaptureBackend.Gdi &&
                            IsContentCadenceAssessment(assessment.Kind);
                        var usedCustomFullscreenFallback =
                            !objectiveGdiCadence &&
                            (assessment.UsedCustomFullscreenFallback ||
                             faultContext.UsedCustomFullscreenFallback);
                        var recoveryReason = SelectSafeCadenceRecoveryReason(
                            outputRequiresScaling,
                            _activeCapturePerformanceProfile,
                            assessment.Kind,
                            usedCustomFullscreenFallback);
                        var diagnostic = string.Create(
                            CultureInfo.InvariantCulture,
                            $"Desktop capture cadence fault backend={strategy.CaptureBackend}; kind={assessment.Kind}; " +
                            $"{(SupportsSourceCadenceDiagnostics(strategy.CaptureBackend) ? "uniqueFps" : "outputFps")}={assessment.UniqueFramesPerSecond:0.0}; " +
                            $"windowSeconds={assessment.Window.TotalSeconds:0.0}; " +
                            $"duplicateRatio={assessment.DuplicateRatio:0.000}; " +
                            $"outputSpeed={assessment.OutputSpeedRatio:0.000}; " +
                            $"coverage={faultContext.CapturedDisplayCoverage:0.000}; " +
                            $"customFullscreen={usedCustomFullscreenFallback}; " +
                            $"profile={_activeCapturePerformanceProfile}.");
                        if (objectiveGdiCadence &&
                            QueueDegradedCaptureReprobe(
                                process.Id,
                                diagnostic,
                                objectiveGdiStarvation: true))
                        {
                            RecordCaptureRuntimeEvent(
                                "capture_gdi_starvation_wgc_reprobe_queued",
                                process,
                                configuration,
                                strategy,
                                diagnostic);
                            continue;
                        }

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
                    if (SupportsGraphicsCaptureRecovery(strategy.CaptureBackend))
                    {
                        RequestCaptureRecovery(
                            CaptureRecoveryReason.CaptureHang,
                            hangDiagnostic);
                    }
                    else
                    {
                        // Automatic transport recovery uses GPU capture. Leaving
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
                    Message = _sessionMode == CaptureSessionMode.Recording
                        ? $"Recorder stopped safely. Select Stop & save to preserve the completed video. {exception.Message}"
                        : $"The replay buffer stopped unexpectedly. {exception.Message}"
                });
            }
        }
    }

    private async Task EnsureRecordingRecoveryCheckpointAsync(
        CancellationToken cancellationToken)
    {
        if (_sessionMode != CaptureSessionMode.Recording ||
            _recordingRecoveryJournal is not { } journal)
        {
            return;
        }

        if (!await journal.CheckpointAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new IOException(
                "Recorder stopped before renewing capture because its recovery exclusions could not be secured.");
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

        if (_sessionMode == CaptureSessionMode.Recording)
        {
            RefreshRecordingBufferState(lastSavedPath);
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

    private void RefreshRecordingBufferState(string? lastSavedPath)
    {
        TimeSpan available;
        long bytes;
        lock (_fileGate)
        {
            RefreshSegmentIndexLocked();
            available = TimeSpan.FromSeconds(
                (long)_completedTrustedRecordingSegments *
                FfmpegArgumentBuilder.RecordingRecoverySegmentSeconds);
            bytes = _bufferBytes;
        }

        // Recorder keeps both recovery MKVs and a directly publishable MP4.
        // Include both direct parts so long sessions report their real on-disk
        // footprint instead of appearing to use roughly half of it.
        var directRecordingBytes = GetDirectRecordingArtifactsLengthSafely(
            _segmentDirectory);
        bytes = bytes > long.MaxValue - directRecordingBytes
            ? long.MaxValue
            : bytes + directRecordingBytes;

        var state = Volatile.Read(ref _isSaving) != 0
            ? ReplayState.Saving
            : available > TimeSpan.Zero
                ? ReplayState.Ready
                : ReplayState.Buffering;
        Publish(new ReplayStateSnapshot(
            state,
            available,
            RecordingStoragePolicy.NoReplayRetention,
            bytes,
            state == ReplayState.Saving
                ? "Finalizing the recording…"
                : available > TimeSpan.Zero
                    ? $"Recorder is running using {_activeEncoderDescription}."
                    : $"Recorder is starting using {_activeEncoderDescription}.",
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

    private void InvalidateLiveRecordingFastPath(string diagnostic)
    {
        if (Interlocked.Exchange(ref _liveRecordingFastPathEligible, 0) == 0)
        {
            return;
        }

        if (_recordingRecoveryJournal is { } journal &&
            !journal.RecordLiveOutputInvalidated())
        {
            EnqueueDiagnostic(
                "Recorder recovery could not journal the live-output invalidation; recovery segments remain authoritative.");
        }

        EnqueueDiagnostic($"Recorder live-output fast path was disabled: {diagnostic}");
        RecordCaptureRuntimeEvent(
            "recording_live_output_invalidated",
            _captureProcess,
            detail: diagnostic,
            includeResourceSnapshots: false);
    }

    internal void InvalidateLiveRecordingFastPathForTesting() =>
        InvalidateLiveRecordingFastPath(
            "Integration smoke forced recovery-segment finalization.");

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

        // A custom/stretched fullscreen candidate is heuristic because Win32
        // can expose the pre-stretch client rectangle. Keep ordinary cadence
        // ambiguity non-destructive, but 8 seconds below 10% of the requested
        // unique frame rate (or a sustained post-healthy collapse) is strong
        // evidence of the exact WGC failure seen in real Apex/Minecraft files.
        // The session-wide two-attempt budget still prevents restart loops.
        if (!outputRequiresScaling &&
            performanceProfile == CapturePerformanceProfile.LowImpact)
        {
            return CaptureRecoveryReason.SourceProfilePromotion;
        }

        return starvationKind is
            CaptureStarvationKind.Severe or
            CaptureStarvationKind.ModerateDegradation
                ? CaptureRecoveryReason.SourceStarvation
                : null;
    }

    internal static bool IsContentCadenceAssessment(
        CaptureStarvationKind starvationKind) =>
        starvationKind is
            CaptureStarvationKind.Severe or
            CaptureStarvationKind.ModerateDegradation or
            CaptureStarvationKind.SchedulingPressure or
            CaptureStarvationKind.ChronicLowCadence;

    internal static CaptureForegroundContext
        ResolveCaptureCadenceForegroundContext(
            DesktopCaptureBackend captureBackend,
            CaptureForegroundContext foregroundContext) =>
        captureBackend == DesktopCaptureBackend.Gdi
            ? foregroundContext with
            {
                // gdigrab is timer-driven rather than content/change-driven.
                // Its CFR duplicate counter therefore measures failed desktop
                // acquisitions even on an idle or windowed desktop. Treat that
                // source telemetry as objective without depending on Win32's
                // necessarily heuristic fullscreen/input classification.
                IsFullscreenOnCapturedDisplay = true,
                HasRecentInput = true,
                UsedCustomFullscreenFallback = false
            }
            : foregroundContext;

    internal static CaptureForegroundContext
        ResolveAgedRecorderCadenceForegroundContext(
            CaptureSessionMode sessionMode,
            DesktopCaptureBackend captureBackend,
            TimeSpan processUptime,
            CaptureForegroundContext foregroundContext)
    {
        _ = sessionMode;
        _ = captureBackend;
        _ = processUptime;

        // Process age and recent input are not proof that a windowed WGC
        // source should be changing every frame. Promoting that heuristic to
        // "fullscreen" made a healthy long, mostly-static desktop recording
        // look like a stalled game and could invalidate its direct MP4. Keep
        // cadence recovery destructive only when the foreground probe has
        // objective fullscreen/custom-resolution evidence of its own.
        return foregroundContext;
    }

    internal static bool ShouldSuppressNonObjectiveCaptureCadence(
        bool usedCustomFullscreenFallback,
        bool outputRequiresScaling,
        CapturePerformanceProfile performanceProfile,
        bool deferredRecoveryIsPending)
    {
        _ = usedCustomFullscreenFallback;
        _ = outputRequiresScaling;
        _ = performanceProfile;
        // Continue collecting custom/stretched cadence so the high-confidence
        // severe and post-healthy lanes above can act. Only a pending recovery
        // suppresses new duplicate-driven assessments.
        return deferredRecoveryIsPending;
    }

    internal static bool CanRefreshCaptureBackend(
        DesktopCaptureBackend captureBackend) =>
        captureBackend is DesktopCaptureBackend.WindowsGraphicsCapture or
            DesktopCaptureBackend.DesktopDuplication or
            DesktopCaptureBackend.Gdi;

    internal static bool SupportsGraphicsCaptureRecovery(
        DesktopCaptureBackend captureBackend) =>
        captureBackend is DesktopCaptureBackend.WindowsGraphicsCapture or
            DesktopCaptureBackend.DesktopDuplication;

    internal static bool SupportsSourceCadenceDiagnostics(
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
            MarkNewestRecordingSegmentCompletedLocked();
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

    private void MarkNewestRecordingSegmentCompletedLocked()
    {
        if (_sessionMode != CaptureSessionMode.Recording || _segments.Count == 0)
        {
            return;
        }

        var newestIndex = _segments.Count - 1;
        var newest = _segments[newestIndex];
        if (newest.IsCountedForRecording ||
            !newest.IsTrusted ||
            newest.Length <= 0 ||
            newest.GenerationId == _exportBlockedCaptureGeneration)
        {
            return;
        }

        _segments[newestIndex] = newest with { IsCountedForRecording = true };
        _completedTrustedRecordingSegments = checked(
            _completedTrustedRecordingSegments + 1);
        if (_recordingRecoveryJournal is { } journal &&
            !journal.RecordCompleted(
                newest.Path,
                newest.Length,
                newest.SegmentNumber))
        {
            EnqueueDiagnostic(
                "Recorder recovery could not queue a completed segment checkpoint.");
        }
    }

    private void MarkRecordingSegmentUntrustedLocked(int index)
    {
        var segment = _segments[index];
        if (segment.IsCountedForRecording)
        {
            if (_recordingRecoveryJournal is { } journal &&
                !journal.RecordUntrusted(segment.Path, segment.SegmentNumber))
            {
                EnqueueDiagnostic(
                    "Recorder recovery could not queue an invalidated segment checkpoint.");
            }

            _completedTrustedRecordingSegments = Math.Max(
                0,
                _completedTrustedRecordingSegments - 1);
        }

        _segments[index] = segment with
        {
            IsTrusted = false,
            IsCountedForRecording = false
        };
    }

    private void ResetSegmentIndexLocked()
    {
        _segments.Clear();
        _bufferBytes = 0;
        _nextSegmentNumber = 0;
        _activeCaptureGeneration = 0;
        _activeCaptureGenerationStartSegmentNumber = -1;
        _completedTrustedRecordingSegments = 0;
        _recordingTailInvalidatedGeneration = -1;
        _recordingTailInvalidatedThroughSegmentNumber = -1;
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

        var isInitialRecorderGeneration =
            _sessionMode == CaptureSessionMode.Recording &&
            _activeCaptureGeneration == 0 &&
            _segments.Count == 0 &&
            firstSegmentNumber == 0;
        _activeCaptureGenerationStartSegmentNumber = firstSegmentNumber;
        _activeCaptureGeneration = checked(_activeCaptureGeneration + 1);
        // Global audio/video setts now rebases the initial Recorder graph to
        // zero before both tee branches, so its first recovery segment is a
        // valid Start boundary. Replacement generations still quarantine one
        // head segment: a restarted WGC source may need a frame before its new
        // graph reaches steady cadence.
        _quarantinedGenerationHeadSegmentNumber = isInitialRecorderGeneration
            ? -1
            : firstSegmentNumber;
    }

    internal static bool ShouldRetainCompletedSegments(
        bool preserveCompletedSegments,
        bool reachedSegmentBoundary) =>
        preserveCompletedSegments && reachedSegmentBoundary;

    internal static bool ShouldPreserveDegradedCaptureHistory(
        bool objectiveGdiStarvation) =>
        !objectiveGdiStarvation;

    internal static bool ShouldDeferNonDestructiveCaptureRefresh(
        bool requireCompletedSegmentBoundary,
        bool reachedSegmentBoundary) =>
        requireCompletedSegmentBoundary && !reachedSegmentBoundary;

    internal static bool IsCaptureSegmentTrusted(
        int segmentNumber,
        int quarantinedGenerationHeadSegmentNumber) =>
        segmentNumber >= 0 &&
        segmentNumber != quarantinedGenerationHeadSegmentNumber;

    internal static TimeSpan? ResolveGracefulRecordingTailDuration(
        long outputTimeMicroseconds,
        int tailSegmentNumber,
        int generationStartSegmentNumber,
        int segmentDurationSeconds,
        int framesPerSecond)
    {
        if (outputTimeMicroseconds <= 0 ||
            tailSegmentNumber < generationStartSegmentNumber ||
            generationStartSegmentNumber < 0 ||
            segmentDurationSeconds is < 1 or > 300 ||
            framesPerSecond is < 1 or > 240)
        {
            return null;
        }

        var completedGenerationSegments =
            tailSegmentNumber - generationStartSegmentNumber;
        var tailSeconds = outputTimeMicroseconds / 1_000_000d -
                          (long)completedGenerationSegments *
                          segmentDurationSeconds;
        var minimumFrameSeconds = 1d / framesPerSecond;
        // FFmpeg progress can lead the video packet that closes the Matroska
        // segment by a small audio/frame interval. Accept only that bounded
        // clock skew, then clamp to the real segment boundary.
        var maximumClockSkewSeconds = Math.Max(0.25, 2d / framesPerSecond);
        if (!double.IsFinite(tailSeconds) ||
            tailSeconds < minimumFrameSeconds / 2d ||
            tailSeconds > segmentDurationSeconds + maximumClockSkewSeconds)
        {
            return null;
        }

        return TimeSpan.FromSeconds(Math.Clamp(
            tailSeconds,
            minimumFrameSeconds,
            segmentDurationSeconds));
    }

    private void InvalidateRetainedCaptureGenerationLocked()
    {
        if (_sessionMode == CaptureSessionMode.Recording)
        {
            // Replay can discard its complete rolling generation. A long
            // recording must retain the known-good history and remove only the
            // most recent safety window around the observed stall/transition.
            _ = InvalidateRecordingTailOnceLocked(
                GetRecordingInvalidationSegmentCount());
            return;
        }

        // A timed-out boundary or a health-triggered renewal means the old
        // process may have continued writing audio while video was stalled.
        // Keeping any of that generation can create a nominal 180-second MP4
        // whose video starts tens of seconds late. Mark every entry untrusted
        // even when Windows prevents immediate deletion, then rebuffer from the
        // replacement generation without reusing segment numbers.
        for (var index = _segments.Count - 1; index >= 0; index--)
        {
            MarkRecordingSegmentUntrustedLocked(index);
            var segment = _segments[index];
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

    private bool InvalidateRecordingTailOnceLocked(int maximumSegments)
    {
        if (maximumSegments <= 0)
        {
            return false;
        }

        var sameGeneration =
            _recordingTailInvalidatedGeneration == _activeCaptureGeneration;
        var previousCutoff = sameGeneration
            ? _recordingTailInvalidatedThroughSegmentNumber
            : -1;
        var observedMaximum = _segments
            .Where(segment => segment.GenerationId == _activeCaptureGeneration)
            .Select(segment => segment.SegmentNumber)
            .DefaultIfEmpty(previousCutoff)
            .Max();
        var invalidated = 0;
        for (var index = _segments.Count - 1; index >= 0; index--)
        {
            var segment = _segments[index];
            if (segment.GenerationId != _activeCaptureGeneration ||
                !segment.IsTrusted ||
                sameGeneration && segment.SegmentNumber <= previousCutoff ||
                !sameGeneration && invalidated >= maximumSegments)
            {
                continue;
            }

            MarkRecordingSegmentUntrustedLocked(index);
            segment = _segments[index];
            invalidated++;
            if (!_protectedSegments.Contains(segment.Path) &&
                TryDeleteFile(segment.Path))
            {
                _bufferBytes = Math.Max(0, _bufferBytes - segment.Length);
                _segments.RemoveAt(index);
            }
        }

        _recordingTailInvalidatedGeneration = _activeCaptureGeneration;
        _recordingTailInvalidatedThroughSegmentNumber = Math.Max(
            previousCutoff,
            observedMaximum);
        return !sameGeneration || invalidated > 0;
    }

    internal static int GetRecordingInvalidationSegmentCount() =>
        (int)Math.Ceiling(
            24d / FfmpegArgumentBuilder.RecordingRecoverySegmentSeconds);

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
        IEnumerable<string> segmentPaths) =>
        BuildConcatManifestLines(
            segmentPaths,
            FfmpegArgumentBuilder.SegmentSeconds);

    internal static IReadOnlyList<string> BuildConcatManifestLines(
        IEnumerable<string> segmentPaths,
        int segmentDurationSeconds)
    {
        ArgumentNullException.ThrowIfNull(segmentPaths);
        if (segmentDurationSeconds is < 1 or > 300)
        {
            throw new ArgumentOutOfRangeException(nameof(segmentDurationSeconds));
        }

        var duration = segmentDurationSeconds.ToString(
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

    internal static IReadOnlyList<string> BuildShortRecordingConcatManifestLines(
        string prefixPath,
        string remainderPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefixPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(remainderPath);
        var prefixDuration = FfmpegArgumentBuilder.SegmentSeconds.ToString(
            "0.000000",
            CultureInfo.InvariantCulture);
        return
        [
            $"file '{EscapeConcatPath(prefixPath)}'",
            $"duration {prefixDuration}",
            $"file '{EscapeConcatPath(remainderPath)}'"
        ];
    }

    internal static IReadOnlyList<string> BuildSingleRecordingConcatManifestLines(
        string mediaPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaPath);
        return [$"file '{EscapeConcatPath(mediaPath)}'"];
    }

    private static async Task<string> PersistRecordingRecoveryStateAsync(
        DetachedRecordingBuffer detachedBuffer,
        CancellationToken cancellationToken)
    {
        var statePath = Path.Combine(
            detachedBuffer.SessionDirectory,
            RecordingRecoveryStateFileName);
        var stateTemporaryPath = statePath + $".{Guid.NewGuid():N}.tmp";
        var concatPath = Path.Combine(
            detachedBuffer.SessionDirectory,
            RecordingRecoveryConcatFileName);
        var concatTemporaryPath = concatPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            var segmentRanges = BuildRecordingSegmentRanges(
                detachedBuffer.SessionDirectory,
                detachedBuffer.SegmentPaths);
            var segmentLengths = detachedBuffer.SegmentPaths
                .Select(path => GetFileLengthSafely(path))
                .ToArray();
            if (segmentLengths.Any(length => length <= 0) ||
                segmentLengths.Aggregate(
                    0L,
                    static (total, length) => checked(total + length)) !=
                detachedBuffer.SegmentBytes)
            {
                throw new IOException(
                    "Recorder recovery segments changed before their exact state could be persisted.");
            }

            var recoverySessionId = detachedBuffer.RecoverySessionId;
            if (!Guid.TryParseExact(recoverySessionId, "N", out _) ||
                !RecordingRecoveryJournal.TryGetSessionMarkerId(
                    detachedBuffer.SessionDirectory,
                    out var markerSessionId) ||
                !string.Equals(
                    recoverySessionId,
                    markerSessionId,
                    StringComparison.Ordinal))
            {
                recoverySessionId = null;
            }

            var stateVersion = recoverySessionId is null ? 2 : 3;
            var state = new RecordingRecoveryState(
                Version: stateVersion,
                SessionDirectory: detachedBuffer.SessionDirectory,
                SegmentPaths: null,
                SegmentBytes: detachedBuffer.SegmentBytes,
                FramesPerSecond: detachedBuffer.FramesPerSecond,
                HasAudio: detachedBuffer.HasAudio,
                LiveRecordingPrefixPath:
                    detachedBuffer.LiveRecordingPrefixPath,
                LiveRecordingPath: detachedBuffer.LiveRecordingPath,
                LiveRecordingExpectedDurationSeconds:
                    detachedBuffer.LiveRecordingExpectedDuration?.TotalSeconds,
                LiveRecordingFastPathEligible:
                    detachedBuffer.LiveRecordingFastPathEligible,
                LiveRecordingNeedsCfrNormalization:
                    detachedBuffer.LiveRecordingNeedsCfrNormalization,
                LiveRecordingRecoveryPath:
                    detachedBuffer.LiveRecordingRecoveryPath,
                SegmentRanges: segmentRanges,
                SegmentLengths: stateVersion == 3
                    ? segmentLengths
                    : null,
                RecoverySessionId: recoverySessionId,
                SegmentTimelineDurationSeconds:
                    detachedBuffer.SegmentTimelineDuration?.TotalSeconds,
                SegmentDurationSeconds:
                    detachedBuffer.SegmentDurationSeconds);
            await File.WriteAllTextAsync(
                    stateTemporaryPath,
                    JsonSerializer.Serialize(state),
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                    cancellationToken)
                .ConfigureAwait(false);
            File.Move(stateTemporaryPath, statePath, overwrite: true);

            await WriteConcatManifestAsync(
                    concatTemporaryPath,
                    detachedBuffer.SegmentPaths,
                    detachedBuffer.SegmentDurationSeconds,
                    detachedBuffer.SegmentTimelineDuration,
                    cancellationToken)
                .ConfigureAwait(false);
            File.Move(concatTemporaryPath, concatPath, overwrite: true);
            return concatPath;
        }
        finally
        {
            TryDeleteFile(stateTemporaryPath);
            TryDeleteFile(concatTemporaryPath);
        }
    }

    private static IReadOnlyList<RecordingSegmentRange>
        BuildRecordingSegmentRanges(
            string sessionDirectory,
            IReadOnlyList<string> segmentPaths)
    {
        var ranges = new List<RecordingSegmentRange>();
        var sessionPrefix = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(sessionDirectory)) + Path.DirectorySeparatorChar;
        var rangeStart = -1;
        var rangeEnd = -1;
        foreach (var candidate in segmentPaths)
        {
            var path = Path.GetFullPath(candidate);
            if (!path.StartsWith(sessionPrefix, StringComparison.OrdinalIgnoreCase) ||
                !TryParseSegmentNumber(Path.GetFileName(path), out var segmentNumber))
            {
                throw new InvalidDataException(
                    "Recorder recovery contains a non-canonical segment path.");
            }

            var expectedPath = Path.Combine(
                sessionDirectory,
                $"segment-{segmentNumber:D9}.mkv");
            if (!string.Equals(path, expectedPath, StringComparison.OrdinalIgnoreCase) ||
                segmentNumber <= rangeEnd)
            {
                throw new InvalidDataException(
                    "Recorder recovery contains duplicate, unordered, or unsafe segments.");
            }

            if (rangeStart < 0)
            {
                rangeStart = rangeEnd = segmentNumber;
            }
            else if (segmentNumber == rangeEnd + 1)
            {
                rangeEnd = segmentNumber;
            }
            else
            {
                ranges.Add(new RecordingSegmentRange(rangeStart, rangeEnd));
                rangeStart = rangeEnd = segmentNumber;
            }
        }

        if (rangeStart >= 0)
        {
            ranges.Add(new RecordingSegmentRange(rangeStart, rangeEnd));
        }

        return ranges;
    }

    private static bool TryParseSegmentNumber(
        string fileName,
        out int segmentNumber)
    {
        const string prefix = "segment-";
        const string suffix = ".mkv";
        segmentNumber = -1;
        return fileName.Length == prefix.Length + 9 + suffix.Length &&
               fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
               fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) &&
               int.TryParse(
                   fileName.AsSpan(prefix.Length, 9),
                   NumberStyles.None,
                   CultureInfo.InvariantCulture,
                   out segmentNumber);
    }

    private static async Task WriteConcatManifestAsync(
        string path,
        IReadOnlyList<string> segmentPaths,
        int segmentDurationSeconds,
        TimeSpan? segmentTimelineDuration,
        CancellationToken cancellationToken)
    {
        if (segmentDurationSeconds is < 1 or > 300)
        {
            throw new ArgumentOutOfRangeException(nameof(segmentDurationSeconds));
        }

        var finalSegmentDurationSeconds = (double)segmentDurationSeconds;
        if (segmentTimelineDuration is { } timelineDuration)
        {
            var precedingDurationSeconds = checked(
                (long)Math.Max(0, segmentPaths.Count - 1) *
                segmentDurationSeconds);
            finalSegmentDurationSeconds =
                timelineDuration.TotalSeconds - precedingDurationSeconds;
            if (segmentPaths.Count == 0 ||
                !double.IsFinite(finalSegmentDurationSeconds) ||
                finalSegmentDurationSeconds <= 0 ||
                finalSegmentDurationSeconds > segmentDurationSeconds)
            {
                throw new InvalidDataException(
                    "Recorder recovery contains an invalid segment timeline duration.");
            }
        }

        await using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var writer = new StreamWriter(
            stream,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            bufferSize: 64 * 1024,
            leaveOpen: false);
        var fullDuration = segmentDurationSeconds.ToString(
            "0.000000",
            CultureInfo.InvariantCulture);
        var finalDuration = finalSegmentDurationSeconds.ToString(
            "0.000000",
            CultureInfo.InvariantCulture);
        for (var index = 0; index < segmentPaths.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var segmentPath = segmentPaths[index];
            ArgumentException.ThrowIfNullOrWhiteSpace(segmentPath);
            await writer.WriteLineAsync(
                    $"file '{EscapeConcatPath(segmentPath)}'")
                .ConfigureAwait(false);
            if (segmentTimelineDuration is not null &&
                index == segmentPaths.Count - 1 &&
                finalSegmentDurationSeconds < segmentDurationSeconds)
            {
                await writer.WriteLineAsync($"outpoint {finalDuration}")
                    .ConfigureAwait(false);
            }
            await writer.WriteLineAsync(
                    $"duration {(index == segmentPaths.Count - 1 ? finalDuration : fullDuration)}")
                .ConfigureAwait(false);
        }

        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task PumpDiagnosticsAsync(Process process)
    {
        while (await process.StandardError.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            if (IsDirectRecordingTeeFailureDiagnostic(line))
            {
                // The direct MP4 is an optional tee branch. Recovery segments
                // remain authoritative if that branch falls behind or fails.
                // Never publish a file after its FIFO dropped encoded packets.
                InvalidateLiveRecordingFastPath(line);
            }

            EnqueueDiagnostic(line);
        }
    }

    internal static bool IsDirectRecordingTeeFailureDiagnostic(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        return line.Contains("FIFO queue full", StringComparison.OrdinalIgnoreCase) ||
               line.Contains("Slave muxer #1", StringComparison.OrdinalIgnoreCase) &&
               line.Contains("fail", StringComparison.OrdinalIgnoreCase);
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
        var invalidatedRecordingTail = false;
        if (ShouldInvalidateCaptureGeneration(reason))
        {
            lock (_fileGate)
            {
                if (_sessionMode == CaptureSessionMode.Recording)
                {
                    // Preserve hours of known-good media and quarantine only the
                    // recent window surrounding the detected pacing failure.
                    RefreshSegmentIndexLocked();
                    _ = InvalidateRecordingTailOnceLocked(
                        GetRecordingInvalidationSegmentCount());
                    invalidatedRecordingTail = true;
                }
                else
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
        }

        if (invalidatedRecordingTail)
        {
            InvalidateLiveRecordingFastPath(
                $"Capture health invalidated the recent Recorder timeline: {diagnostic}");
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
        string diagnostic,
        bool objectiveGdiStarvation = false)
    {
        var deadlineUtcTicks = Volatile.Read(
            ref _degradedCaptureReprobeNotBeforeUtcTicks);
        var strategyUsesCapabilityProbe =
            Volatile.Read(ref _activeStrategyUsesCapabilityProbe) != 0;
        var captureBackend = _activeCaptureStrategy?.CaptureBackend;
        var deadlineReached = ShouldScheduleDegradedCaptureReprobe(
            captureBackend,
            strategyUsesCapabilityProbe,
            deadlineUtcTicks,
            DateTimeOffset.UtcNow.UtcDateTime.Ticks);
        var objectiveReprobeAllowed =
            objectiveGdiStarvation &&
            ShouldScheduleObjectiveGdiReprobe(
                captureBackend,
                strategyUsesCapabilityProbe,
                Volatile.Read(ref _degradedCaptureReprobeAttempt),
                deadlineUtcTicks,
                DateTimeOffset.UtcNow.UtcDateTime.Ticks);
        if (expectedProcessId is not { } processId ||
            processId <= 0 ||
            Volatile.Read(ref _disposed) != 0 ||
            Volatile.Read(ref _isStopping) != 0 ||
            !IsRunning ||
            IsActiveCaptureGenerationExportBlocked() ||
            !deadlineReached && !objectiveReprobeAllowed ||
            CaptureProcessId != processId)
        {
            return false;
        }

        var coordinatorDiagnostic = objectiveGdiStarvation
            ? ObjectiveGdiReprobeDiagnosticPrefix + diagnostic
            : diagnostic;
        var queued = _degradedCaptureReprobeCoordinator.TrySchedule(
            processId,
            coordinatorDiagnostic);
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
        var objectiveGdiStarvation =
            IsObjectiveGdiReprobeDiagnostic(diagnostic);
        diagnostic = StripObjectiveGdiReprobeDiagnosticPrefix(diagnostic);
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
        if (!objectiveGdiStarvation &&
            ShouldDeferDegradedCaptureReprobeForGameplay(
                foregroundContext))
        {
            // The capability probe is a real three-second capture/encode graph.
            // Periodic Instant Replay maintenance can retry the already-due
            // check after an alt-tab or idle period. Recorder never enters
            // this periodic path; only objective GDI starvation can request
            // its focused WGC-only replacement check.
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
        var inputMonitorTask = objectiveGdiStarvation
            ? Task.CompletedTask
            : CancelDegradedCaptureReprobeOnInputAsync(
                configuration.Display,
                probeCancellation,
                inputMonitorCancellation.Token);
        FfmpegCapabilitySelection selection;
        try
        {
            selection = objectiveGdiStarvation
                ? await _capabilityProbe.ReprobeWindowsGraphicsCaptureAsync(
                        verifiedFfmpegPath,
                        configuration,
                        currentStrategy!,
                        probeCancellation.Token,
                        performanceProfile)
                    .ConfigureAwait(false)
                : await _capabilityProbe.SelectAsync(
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

        if (!SupportsGraphicsCaptureRecovery(selection.Strategy.CaptureBackend))
        {
            ScheduleNextDegradedCaptureReprobe(selection.CacheExpiresAtUtc);
            RecordCaptureRuntimeEvent(
                "capture_degraded_reprobe_retained",
                _captureProcess,
                configuration,
                selection.Strategy,
                "GPU desktop capture was still unavailable; the verified GDI session remains active.");
            if (objectiveGdiStarvation)
            {
                RequestCaptureRecovery(
                    CaptureRecoveryReason.SourceStarvation,
                    "GDI source cadence was objectively starved and the focused GPU capture replacement check remained unavailable. " +
                    selection.Diagnostics);
            }

            return;
        }

        var refreshed = await RefreshCaptureAsync(
                expectedProcessId,
                cancellationToken,
                // A routine idle promotion retains its healthy GDI history.
                // Objective starvation has already proven the recent cadence
                // bad: Recorder's destructive path quarantines only its bounded
                // recent tail, while Instant Replay safely re-buffers.
                preserveCompletedSegments:
                    ShouldPreserveDegradedCaptureHistory(
                        objectiveGdiStarvation),
                verifiedReplacement: selection,
                performanceProfileOverride: performanceProfile)
            .ConfigureAwait(false);
        if (!refreshed)
        {
            var degradedCaptureIsStillCurrent =
                IsCurrentDegradedCapture(
                    expectedProcessId,
                    sessionIdentity,
                    configuration,
                    ffmpegPath,
                    currentStrategy);
            if (degradedCaptureIsStillCurrent)
            {
                ScheduleNextDegradedCaptureReprobe(selection.CacheExpiresAtUtc);
            }

            EnqueueDiagnostic(
                $"Skipped stale degraded-capture promotion for process {expectedProcessId}.");
            if (objectiveGdiStarvation && degradedCaptureIsStillCurrent)
            {
                RequestCaptureRecovery(
                    CaptureRecoveryReason.SourceStarvation,
                    "GDI cadence was objectively starved and the verified WGC promotion could not complete at a safe segment boundary.");
            }
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

    internal static bool ShouldScheduleObjectiveGdiReprobe(
        DesktopCaptureBackend? captureBackend,
        bool strategyUsesCapabilityProbe,
        int reprobeAttempt,
        long deadlineUtcTicks,
        long nowUtcTicks) =>
        ShouldScheduleDegradedCaptureReprobe(
            captureBackend,
            strategyUsesCapabilityProbe,
            deadlineUtcTicks,
            nowUtcTicks) ||
        ShouldMaintainDegradedCaptureReprobe(
            captureBackend,
            strategyUsesCapabilityProbe) &&
        reprobeAttempt <= 1;

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

    internal static bool IsObjectiveGdiReprobeDiagnostic(
        string diagnostic) =>
        diagnostic.StartsWith(
            ObjectiveGdiReprobeDiagnosticPrefix,
            StringComparison.Ordinal);

    internal static string StripObjectiveGdiReprobeDiagnosticPrefix(
        string diagnostic) =>
        IsObjectiveGdiReprobeDiagnostic(diagnostic)
            ? diagnostic[ObjectiveGdiReprobeDiagnosticPrefix.Length..]
            : diagnostic;

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
               SupportsGraphicsCaptureRecovery(replacementStrategy.CaptureBackend);
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
                : CaptureGeometry.ResolveOutputSize(configuration);
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
        out string failure,
        TimeSpan? durationToleranceOverride = null)
    {
        failure = string.Empty;
        if (expectedDuration <= TimeSpan.Zero ||
            expectedFramesPerSecond is < 1 or > 240 ||
            durationToleranceOverride is { } invalidTolerance &&
            invalidTolerance <= TimeSpan.Zero)
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
            var durationTolerance = durationToleranceOverride?.TotalSeconds ??
                                    Math.Max(0.15, 3d / expectedFramesPerSecond);
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
                var frameCountTolerance = Math.Max(
                    Math.Max(3d, expectedFramesPerSecond * 0.25),
                    durationTolerance * expectedFramesPerSecond);
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
        CancellationToken cancellationToken,
        TimeSpan? timeoutOverride = null,
        TimeSpan? durationToleranceOverride = null)
    {
        var output = await ReadExportProbeAsync(
                ffprobePath,
                mediaPath,
                cancellationToken,
                timeoutOverride)
            .ConfigureAwait(false);

        if (!TryValidateExportProbe(
                output,
                expectedDuration,
                expectedFramesPerSecond,
                expectedAudio,
                out var failure,
                durationToleranceOverride))
        {
            throw new MediaTimelineValidationException(
                $"ClipForge rejected a clip with a broken media timeline. {failure}");
        }
    }

    private static async Task<TimeSpan> ValidateRecoveredExportAsync(
        string ffprobePath,
        string mediaPath,
        TimeSpan minimumTrustedDuration,
        int expectedFramesPerSecond,
        bool expectedAudio,
        CancellationToken cancellationToken,
        TimeSpan? timeoutOverride = null)
    {
        var output = await ReadExportProbeAsync(
                ffprobePath,
                mediaPath,
                cancellationToken,
                timeoutOverride)
            .ConfigureAwait(false);
        if (!TryReadPrimaryVideoDuration(output, out var recoveredDuration) ||
            recoveredDuration <= TimeSpan.Zero)
        {
            throw new InvalidDataException(
                "ClipForge could not determine the repaired recording timeline.");
        }

        if (!RecoveredOutputCoversTrustedTimeline(
                recoveredDuration,
                minimumTrustedDuration))
        {
            throw new InvalidDataException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"The repaired live output is only {recoveredDuration.TotalSeconds:0.###}s, " +
                    $"shorter than the {minimumTrustedDuration.TotalSeconds:0.###}s trusted recovery timeline."));
        }

        if (!TryValidateExportProbe(
                output,
                recoveredDuration,
                expectedFramesPerSecond,
                expectedAudio,
                out var failure,
                durationToleranceOverride: TimeSpan.FromSeconds(0.5)))
        {
            throw new InvalidDataException(
                $"ClipForge rejected a repaired recording with a broken media timeline. {failure}");
        }

        return recoveredDuration;
    }

    internal static bool RecoveredOutputCoversTrustedTimeline(
        TimeSpan recoveredDuration,
        TimeSpan minimumTrustedDuration) =>
        recoveredDuration > TimeSpan.Zero &&
        (minimumTrustedDuration <= TimeSpan.Zero ||
         recoveredDuration + TimeSpan.FromSeconds(0.5) >=
            minimumTrustedDuration);

    private static bool TryReadPrimaryVideoDuration(
        string probeJson,
        out TimeSpan duration)
    {
        duration = TimeSpan.Zero;
        try
        {
            using var document = JsonDocument.Parse(probeJson, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 16
            });
            if (!document.RootElement.TryGetProperty("streams", out var streams) ||
                streams.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            foreach (var stream in streams.EnumerateArray())
            {
                if (stream.ValueKind != JsonValueKind.Object ||
                    !stream.TryGetProperty("codec_type", out var codecType) ||
                    !string.Equals(
                        codecType.GetString(),
                        "video",
                        StringComparison.Ordinal) ||
                    !TryReadProbeSeconds(stream, "duration", out var seconds) ||
                    seconds <= 0 ||
                    seconds > TimeSpan.MaxValue.TotalSeconds)
                {
                    continue;
                }

                duration = TimeSpan.FromSeconds(seconds);
                return true;
            }

            return false;
        }
        catch (Exception exception) when (
            exception is JsonException or InvalidOperationException or
                FormatException or OverflowException)
        {
            return false;
        }
    }

    private static async Task<string> ReadExportProbeAsync(
        string ffprobePath,
        string mediaPath,
        CancellationToken cancellationToken,
        TimeSpan? timeoutOverride)
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
        using var timeout = new CancellationTokenSource(
            timeoutOverride ?? TimeSpan.FromSeconds(8));
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
            throw new TimeoutException("Clip validation timed out.");
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
            throw new IOException(
                string.IsNullOrWhiteSpace(error)
                    ? "The generated clip could not be read back by ClipForge."
                    : $"The generated clip could not be validated. {error}");
        }

        return output;
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

    private static async Task RunRecordingExportProcessAsync(
        string executable,
        IReadOnlyList<string> arguments,
        string outputPath,
        CancellationToken cancellationToken)
    {
        using var process = CreateProcess(
            executable,
            arguments,
            redirectStandardInput: false);
        if (!process.Start())
        {
            throw new InvalidOperationException(
                "Windows could not start the recording finalizer.");
        }

        using var processJob = AttachAuxiliaryProcessLifetime(process);
        _ = ProcessTuning.TryApplyLowImpactPriority(process);
        var errorTask = ReadLastDiagnosticLineAsync(process.StandardError);
        var exitTask = process.WaitForExitAsync();
        var lastProgressTimestamp = Stopwatch.GetTimestamp();
        long lastOutputBytes = -1;
        try
        {
            while (!exitTask.IsCompleted)
            {
                await Task.WhenAny(
                        exitTask,
                        Task.Delay(TimeSpan.FromSeconds(5), cancellationToken))
                    .ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (exitTask.IsCompleted)
                {
                    break;
                }

                var outputBytes = GetFileLengthSafely(outputPath);
                if (outputBytes > lastOutputBytes)
                {
                    lastOutputBytes = outputBytes;
                    lastProgressTimestamp = Stopwatch.GetTimestamp();
                    continue;
                }

                if (Stopwatch.GetElapsedTime(lastProgressTimestamp) >=
                    TimeSpan.FromMinutes(2))
                {
                    TryKill(process);
                    await WaitForExportTerminationAsync(process).ConfigureAwait(false);
                    throw new TimeoutException(
                        "The recording finalizer made no disk progress for two minutes and was stopped. The source session was preserved.");
                }
            }

            await exitTask.ConfigureAwait(false);
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
            throw new RecordingAssemblyException(
                string.IsNullOrWhiteSpace(error)
                    ? "The capture engine could not assemble the recording."
                    : $"The capture engine could not assemble the recording. {error}");
        }
    }

    internal static async Task<string> CopyRecordingOutputAsync(
        string sourcePath,
        string outputPath,
        CancellationToken cancellationToken,
        string? expectedSourceFingerprint = null)
    {
        const int copyBufferBytes = 1024 * 1024;
        await using var source = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            copyBufferBytes,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var expectedLength = source.Length;
        if (expectedLength <= 0)
        {
            throw new InvalidDataException(
                "Recorder's validated live output became empty before it could be copied.");
        }

        // Keep this read handle open until the copied payload is checked. It
        // denies both writes and replacement of the validated source for the
        // entire potentially long cross-volume copy.
        var sourceFingerprint =
            RecordingRecoveryJournal.ComputeOutputFingerprint(sourcePath);
        if (expectedSourceFingerprint is not null &&
            !string.Equals(
                sourceFingerprint,
                expectedSourceFingerprint,
                StringComparison.Ordinal))
        {
            throw new IOException(
                "Recorder's validated live output changed before it could be copied.");
        }
        var sourceContentFingerprint =
            RecordingRecoveryJournal.ComputeOutputContentSampleFingerprint(
                sourcePath);

        await using (var output = new FileStream(
                         outputPath,
                         FileMode.CreateNew,
                         FileAccess.Write,
                         FileShare.None,
                         copyBufferBytes,
                         FileOptions.Asynchronous |
                         FileOptions.SequentialScan |
                         FileOptions.WriteThrough))
        {
            await source.CopyToAsync(
                    output,
                    copyBufferBytes,
                    cancellationToken)
                .ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            output.Flush(flushToDisk: true);
            if (output.Length != expectedLength)
            {
                throw new IOException(
                    "Recorder's cross-volume copy did not preserve the complete live output.");
            }
        }

        using var copiedOutput = new FileStream(
            outputPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.RandomAccess);
        if (copiedOutput.Length != expectedLength ||
            !string.Equals(
                sourceContentFingerprint,
                RecordingRecoveryJournal.ComputeOutputContentSampleFingerprint(
                    outputPath),
                StringComparison.Ordinal))
        {
            throw new IOException(
                "Recorder's copied live output did not match the validated source. " +
                "The source session was preserved.");
        }

        // Return the new file's identity, not the source file's identity. This
        // token remains stable across the destination's final atomic rename
        // and detects an edited/replaced partial before commit or cleanup.
        return RecordingRecoveryJournal.ComputeOutputFingerprint(outputPath);
    }

    internal static long? TryGetAvailableFreeSpace(string path)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            if (OperatingSystem.IsWindows() &&
                GetDiskFreeSpaceExW(
                    fullPath,
                    out var availableBytes,
                    out _,
                    out _))
            {
                return availableBytes > long.MaxValue
                    ? long.MaxValue
                    : (long)availableBytes;
            }

            if (fullPath.StartsWith(@"\\", StringComparison.Ordinal))
            {
                return null;
            }

            var root = Path.GetPathRoot(fullPath);
            return string.IsNullOrWhiteSpace(root)
                ? null
                : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                ArgumentException or NotSupportedException or SecurityException)
        {
            return null;
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

    private static async Task<bool> StopProcessGracefullyAsync(
        Process process,
        TimeSpan? gracefulTimeout = null,
        Task<bool>? earlyStopSignal = null)
    {
        var signalAlreadySent = false;
        if (earlyStopSignal is not null)
        {
            try
            {
                signalAlreadySent = await earlyStopSignal.ConfigureAwait(false);
            }
            catch
            {
                // BeginRecordingStopRequest normally converts signal failures to
                // false. Keep this helper defensive and retry below if a future
                // signal implementation faults unexpectedly.
            }
        }

        if (process.HasExited)
        {
            return process.ExitCode == 0;
        }

        if (!signalAlreadySent)
        {
            _ = await SignalProcessGracefulStopAsync(process)
                .ConfigureAwait(false);
        }

        using var timeout = new CancellationTokenSource(
            gracefulTimeout ?? TimeSpan.FromSeconds(5));
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            return process.ExitCode == 0;
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

            return false;
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
        CleanupStaleBuffers(_bufferRoot, cancellationToken);
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

    private void CleanupStaleBuffers(
        string bufferRoot,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested ||
            !IsSafeBufferRootPath(bufferRoot))
        {
            return;
        }

        var startedAt = Stopwatch.GetTimestamp();
        var directoriesInspected = 0;
        var filesDeleted = 0;
        try
        {
            foreach (var directoryPath in Directory.EnumerateDirectories(
                         bufferRoot,
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
                    bufferRoot,
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
        string bufferRoot,
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
                        bufferRoot,
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
                    bufferRoot,
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

    internal static bool ArePathsOnSameVolume(string leftPath, string rightPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(leftPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(rightPath);
        try
        {
            if (OperatingSystem.IsWindows() &&
                TryGetVolumeIdentity(leftPath, out var leftVolume) &&
                TryGetVolumeIdentity(rightPath, out var rightVolume))
            {
                return leftVolume.Equals(
                    rightVolume,
                    StringComparison.OrdinalIgnoreCase);
            }

            var leftRoot = Path.GetPathRoot(Path.GetFullPath(leftPath));
            var rightRoot = Path.GetPathRoot(Path.GetFullPath(rightPath));
            return !string.IsNullOrWhiteSpace(leftRoot) &&
                   leftRoot.Equals(rightRoot, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                ArgumentException or NotSupportedException or SecurityException)
        {
            return false;
        }
    }

    private static bool TryGetVolumeIdentity(
        string path,
        out string volumeIdentity)
    {
        volumeIdentity = string.Empty;
        try
        {
            var existingPath = Path.GetFullPath(path);
            if (File.Exists(existingPath))
            {
                existingPath = Path.GetDirectoryName(existingPath) ?? existingPath;
            }
            while (!Directory.Exists(existingPath))
            {
                var parent = Path.GetDirectoryName(existingPath);
                if (string.IsNullOrWhiteSpace(parent) ||
                    parent.Equals(existingPath, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                existingPath = parent;
            }

            var volumePath = new StringBuilder(MaximumFinalPathCharacters);
            if (!GetVolumePathNameW(
                    existingPath,
                    volumePath,
                    volumePath.Capacity))
            {
                return false;
            }

            var volumeName = new StringBuilder(128);
            volumeIdentity = GetVolumeNameForVolumeMountPointW(
                    volumePath.ToString(),
                    volumeName,
                    volumeName.Capacity)
                ? volumeName.ToString()
                : volumePath.ToString();
            return !string.IsNullOrWhiteSpace(volumeIdentity);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                ArgumentException or NotSupportedException or SecurityException)
        {
            return false;
        }
    }

    internal static bool IsSafeRecordingSaveDestination(
        string saveDirectory,
        string bufferRoot,
        string sessionDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(saveDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(bufferRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionDirectory);
        try
        {
            var destination = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(saveDirectory));
            var normalizedBufferRoot = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(bufferRoot));
            var normalizedSession = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(sessionDirectory));
            return IsSafeBufferRootPath(destination) &&
                   TryResolvePhysicalDirectoryPath(
                       destination,
                       out var physicalDestination) &&
                   TryResolvePhysicalDirectoryPath(
                       normalizedBufferRoot,
                       out var physicalBufferRoot) &&
                   TryResolvePhysicalDirectoryPath(
                       normalizedSession,
                       out var physicalSession) &&
                   !IsSameOrDescendantPath(
                       physicalDestination,
                       physicalBufferRoot) &&
                   !IsSameOrDescendantPath(
                       physicalDestination,
                       physicalSession);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                ArgumentException or NotSupportedException or SecurityException)
        {
            return false;
        }
    }

    private static bool TryResolvePhysicalDirectoryPath(
        string path,
        out string physicalPath)
    {
        physicalPath = string.Empty;
        if (!OperatingSystem.IsWindows() ||
            string.IsNullOrWhiteSpace(path) ||
            !Path.IsPathFullyQualified(path))
        {
            return false;
        }

        try
        {
            var normalized = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(path));
            var attributes = File.GetAttributes(normalized);
            if ((attributes & FileAttributes.Directory) == 0 ||
                (attributes & FileAttributes.ReparsePoint) != 0)
            {
                return false;
            }

            using var handle = CreateFileW(
                ToExtendedLengthPath(normalized),
                FileReadAttributes,
                FileShareRead | FileShareWrite | FileShareDelete,
                IntPtr.Zero,
                OpenExisting,
                FileFlagOpenReparsePoint | FileFlagBackupSemantics,
                IntPtr.Zero);
            if (handle.IsInvalid)
            {
                return false;
            }

            var resolved = new StringBuilder(MaximumFinalPathCharacters);
            var length = GetFinalPathNameByHandleW(
                handle,
                resolved,
                resolved.Capacity,
                FileNameNormalized);
            if (length == 0 || length >= resolved.Capacity)
            {
                return false;
            }

            var value = resolved.ToString();
            if (value.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            {
                value = @"\\" + value[8..];
            }
            else if (value.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase))
            {
                value = value[4..];
            }

            physicalPath = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(value));
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                ArgumentException or NotSupportedException or SecurityException)
        {
            return false;
        }
    }

    private static string ToExtendedLengthPath(string path)
    {
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            return path;
        }

        return path.StartsWith(@"\\", StringComparison.Ordinal)
            ? @"\\?\UNC\" + path[2..]
            : @"\\?\" + path;
    }

    private static bool IsSameOrDescendantPath(
        string candidate,
        string directory)
    {
        if (candidate.Equals(directory, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var prefix = directory.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private void QueueCommittedRecordingCleanup(
        DetachedRecordingBuffer detachedBuffer,
        string committedOutputPath,
        long committedOutputLength,
        string committedOutputFingerprint)
    {
        if (!IsSafeRecordingSaveDestination(
                Path.GetDirectoryName(Path.GetFullPath(committedOutputPath))!,
                detachedBuffer.BufferRoot,
                detachedBuffer.SessionDirectory))
        {
            EnqueueDiagnostic(
                "Recorder kept its working directory because the committed output could not be proven outside it.");
            return;
        }

        lock (_recordingCleanupGate)
        {
            // A long session can contain tens of thousands of files. Serialize
            // completed-session deletions so they cannot saturate the drive in
            // parallel after their MP4s are already visible in the library.
            _recordingCleanupTask = _recordingCleanupTask.ContinueWith(
                _ =>
                {
                    if (!RecordingRecoveryJournal.HasMatchingOutputIdentity(
                            committedOutputPath,
                            committedOutputLength,
                            committedOutputFingerprint))
                    {
                        EnqueueDiagnostic(
                            "Recorder kept its recovery source because the committed output changed before background cleanup.");
                        return;
                    }

                    if (TryDeleteBufferDirectory(
                            detachedBuffer.SessionDirectory,
                            detachedBuffer.BufferRoot))
                    {
                        RecordingRecoveryJournal.TryDeleteJournal(
                            detachedBuffer.RecoveryJournalPath);
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default);
        }
    }

    private static long GetDirectRecordingArtifactsLengthSafely(
        string? sessionDirectory)
    {
        if (string.IsNullOrWhiteSpace(sessionDirectory))
        {
            return 0;
        }

        var basePath = Path.Combine(sessionDirectory, LiveRecordingFileName);
        var total = GetFileLengthSafely(basePath);
        for (var partNumber = 0; partNumber <= 1; partNumber++)
        {
            var partLength = GetFileLengthSafely(
                FfmpegArgumentBuilder.GetDirectRecordingPartPath(
                    basePath,
                    partNumber));
            total = total > long.MaxValue - partLength
                ? long.MaxValue
                : total + partLength;
        }

        return total;
    }

    private static void TryDeleteDirectRecordingArtifacts(
        string? sessionDirectory)
    {
        if (string.IsNullOrWhiteSpace(sessionDirectory))
        {
            return;
        }

        var basePath = Path.Combine(sessionDirectory, LiveRecordingFileName);
        TryDeleteFile(basePath);
        for (var partNumber = 0; partNumber <= 1; partNumber++)
        {
            TryDeleteFile(FfmpegArgumentBuilder.GetDirectRecordingPartPath(
                basePath,
                partNumber));
        }
    }

    private static bool IsCanonicalContinuousLiveRecordingPath(
        string sessionDirectory,
        string? candidatePath)
    {
        if (string.IsNullOrWhiteSpace(sessionDirectory) ||
            string.IsNullOrWhiteSpace(candidatePath))
        {
            return false;
        }

        try
        {
            return string.Equals(
                Path.GetFullPath(candidatePath),
                Path.GetFullPath(Path.Combine(
                    sessionDirectory,
                    LiveRecordingFileName)),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                ArgumentException or NotSupportedException or SecurityException)
        {
            return false;
        }
    }

    private bool TryDeleteBufferDirectory(string? path, string? bufferRoot = null)
    {
        bufferRoot ??= _activeBufferRoot ?? _bufferRoot;
        if (string.IsNullOrWhiteSpace(path) ||
            !IsSafeBufferRootPath(bufferRoot))
        {
            return false;
        }

        try
        {
            var directory = new DirectoryInfo(Path.GetFullPath(path));
            if (directory.Exists &&
                IsSafeBufferDirectoryPath(bufferRoot, directory.FullName, directory.Attributes) &&
                IsSafeBufferRootPath(bufferRoot))
            {
                Directory.Delete(directory.FullName, recursive: true);
                return true;
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or
                NotSupportedException or SecurityException)
        {
            // A later startup can retry stale session cleanup.
        }

        return false;
    }

    private bool IsSafeActiveBufferDirectory(string path)
    {
        try
        {
            var bufferRoot = _activeBufferRoot ?? _bufferRoot;
            var directory = new DirectoryInfo(Path.GetFullPath(path));
            return directory.Exists &&
                   IsSafeBufferDirectoryPath(
                       bufferRoot,
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

    private static void EnsureSafeBufferRoot(string bufferRoot)
    {
        if (!IsSafeBufferRootPath(bufferRoot))
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
        if (configuration.SessionMode == CaptureSessionMode.InstantReplay &&
            (configuration.Retention < TimeSpan.FromSeconds(FfmpegArgumentBuilder.SegmentSeconds) ||
             configuration.Retention > TimeSpan.FromHours(1)))
        {
            throw new ArgumentOutOfRangeException(
                nameof(configuration),
                "Replay length must be between two seconds and one hour.");
        }

        if (configuration.SessionMode == CaptureSessionMode.Recording &&
            configuration.Retention != RecordingStoragePolicy.NoReplayRetention)
        {
            throw new ArgumentOutOfRangeException(
                nameof(configuration),
                "Recorder must not define an Instant Replay retention window.");
        }

        if (configuration.SessionMode is not (
                CaptureSessionMode.InstantReplay or CaptureSessionMode.Recording))
        {
            throw new ArgumentOutOfRangeException(nameof(configuration));
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
                var detachedRecordingFinalization =
                    snapshot.State == ReplayState.Saving &&
                    _sessionMode == CaptureSessionMode.Recording &&
                    Volatile.Read(ref _isSaving) != 0 &&
                    Volatile.Read(ref _pendingDetachedRecordingBuffer) is not null;
                if (!IsRunning &&
                    !detachedRecordingFinalization &&
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
        bool IsTrusted,
        bool IsCountedForRecording = false);

    private sealed record RecordingStopBoundary(
        int SessionIdentity,
        string SessionDirectory,
        IReadOnlyDictionary<string, long> ClosedSegments,
        string? OpenSegmentPath,
        long OpenSegmentLength,
        int OpenSegmentNumber,
        int OpenSegmentGenerationId,
        int OpenSegmentGenerationStartNumber,
        CaptureProgressSample? CaptureProgress,
        Process? CaptureProcess);

    private sealed record RecordingStopRequest(
        int SessionIdentity,
        Process CaptureProcess,
        Task<bool> SignalTask);

    private sealed record DetachedRecordingBuffer(
        string SessionDirectory,
        string BufferRoot,
        IReadOnlyList<string> SegmentPaths,
        long SegmentBytes,
        int FramesPerSecond,
        bool HasAudio,
        string? RecoveryJournalPath = null,
        string? RecoverySessionId = null,
        bool SourceAvailable = true,
        int MissingSegmentCount = 0,
        bool RecoveryWasDetached = true,
        TimeSpan? SegmentTimelineDuration = null,
        string? LiveRecordingPrefixPath = null,
        string? LiveRecordingPath = null,
        TimeSpan? LiveRecordingExpectedDuration = null,
        bool LiveRecordingFastPathEligible = false,
        bool LiveRecordingNeedsCfrNormalization = false,
        string? LiveRecordingRecoveryPath = null,
        int SegmentDurationSeconds = FfmpegArgumentBuilder.SegmentSeconds);

    private sealed record RecordingRecoveryState(
        int Version,
        string SessionDirectory,
        IReadOnlyList<string>? SegmentPaths,
        long SegmentBytes,
        int FramesPerSecond,
        bool HasAudio,
        string? LiveRecordingPrefixPath = null,
        string? LiveRecordingPath = null,
        double? LiveRecordingExpectedDurationSeconds = null,
        bool LiveRecordingFastPathEligible = false,
        bool LiveRecordingNeedsCfrNormalization = false,
        string? LiveRecordingRecoveryPath = null,
        IReadOnlyList<RecordingSegmentRange>? SegmentRanges = null,
        IReadOnlyList<long>? SegmentLengths = null,
        string? RecoverySessionId = null,
        double? SegmentTimelineDurationSeconds = null,
        int? SegmentDurationSeconds = null);

    private readonly record struct RecordingSegmentRange(
        int FirstSegmentNumber,
        int LastSegmentNumber);

    private sealed class RetryableCaptureLaunchException(
        string message,
        Exception innerException)
        : Exception(message, innerException);

    private sealed class MediaTimelineValidationException(string message)
        : Exception(message);

    private sealed class RecordingAssemblyException(string message)
        : Exception(message);

    internal readonly record struct WindowsSessionCleanupCandidate(
        string Path,
        int SessionId,
        DateTime LatestWriteTimeUtc);

    internal readonly record struct WindowsSessionCleanupScanResult(
        IReadOnlyList<WindowsSessionCleanupCandidate> Candidates,
        string NextSuffix,
        int EnumeratedCount,
        int InspectedCount);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(
        SafeFileHandle hFile,
        StringBuilder lpszFilePath,
        int cchFilePath,
        uint dwFlags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumePathNameW(
        string lpszFileName,
        StringBuilder lpszVolumePathName,
        int cchBufferLength);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeNameForVolumeMountPointW(
        string lpszVolumeMountPoint,
        StringBuilder lpszVolumeName,
        int cchBufferLength);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceExW(
        string lpDirectoryName,
        out ulong lpFreeBytesAvailableToCaller,
        out ulong lpTotalNumberOfBytes,
        out ulong lpTotalNumberOfFreeBytes);
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
