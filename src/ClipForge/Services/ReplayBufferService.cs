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

    private readonly FfmpegSetupService _ffmpegSetupService;
    private readonly FfmpegCapabilityProbe _capabilityProbe = new();
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
    private CancellationTokenSource? _sessionCancellation;
    private Task? _monitorTask;
    private Task? _diagnosticTask;
    private string? _segmentDirectory;
    private TimeSpan _retention = TimeSpan.FromMinutes(2);
    private ReplayStateSnapshot _state = new(
        ReplayState.Stopped,
        TimeSpan.Zero,
        TimeSpan.FromMinutes(2),
        0);
    private string? _lastSavedPath;
    private string? _activeEncoderDescription;
    private long _bufferBytes;
    private long _reportedDroppedAudioBlocks;
    private int _nextSegmentNumber;
    private int _isRunning;
    private int _isSaving;
    private int _isStopping;
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

    public bool IsRunning => Volatile.Read(ref _isRunning) != 0;

    public string? ActiveEncoderDescription => _activeEncoderDescription;

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

    public async Task StartAsync(
        CaptureConfiguration configuration,
        CancellationToken cancellationToken)
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
                lock (_diagnosticGate)
                {
                    _diagnosticLines.Clear();
                }

                var capabilitySelection = await _capabilityProbe
                    .SelectAsync(ffmpegPath, configuration, cancellationToken)
                    .ConfigureAwait(false);
                _activeEncoderDescription = capabilitySelection.Strategy.Description;
                EnqueueDiagnostic(capabilitySelection.Diagnostics);

                CreateAudioPipes(configuration);
                var arguments = FfmpegArgumentBuilder.BuildCaptureArguments(
                    configuration,
                    _audioPipes.Select(pipe => pipe.Specification).ToArray(),
                    capabilitySelection.Strategy,
                    _segmentDirectory);

                var captureProcess = CreateProcess(ffmpegPath, arguments, redirectStandardInput: true);
                try
                {
                    if (!captureProcess.Start())
                    {
                        throw new InvalidOperationException("Windows could not start the capture engine.");
                    }
                }
                catch
                {
                    captureProcess.Dispose();
                    throw;
                }

                _captureProcess = captureProcess;
                if (!ProcessTuning.TryApplyLowImpactPriority(captureProcess))
                {
                    EnqueueDiagnostic("Windows did not allow ClipForge to lower FFmpeg's process priority.");
                }

                captureProcess.StandardInput.AutoFlush = true;
                _diagnosticTask = PumpDiagnosticsAsync(captureProcess);
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

            var manifestLines = selectedSegments.Select(path =>
                $"file '{EscapeConcatPath(path)}'");
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

        var process = _captureProcess;
        if (process is not null)
        {
            await StopProcessGracefullyAsync(process).ConfigureAwait(false);
        }

        _sessionCancellation?.Cancel();
        if (_monitorTask is not null)
        {
            try
            {
                await _monitorTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected when a session is stopped.
            }
        }

        foreach (var audioPipe in _audioPipes)
        {
            try
            {
                await audioPipe.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                // Continue releasing the remaining capture resources.
            }
        }

        _audioPipes.Clear();

        if (_diagnosticTask is not null)
        {
            try
            {
                await _diagnosticTask.ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException)
            {
                // The process stream can close while diagnostics are being drained.
            }
        }

        process?.Dispose();
        _sessionCancellation?.Dispose();
        _captureProcess = null;
        _sessionCancellation = null;
        _monitorTask = null;
        _diagnosticTask = null;

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

    private async Task MonitorCaptureAsync(Process process, CancellationToken cancellationToken)
    {
        using var healthTimer = new PeriodicTimer(CaptureHealthPollInterval);
        var lastBufferRefresh = Stopwatch.GetTimestamp();

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

    private async Task PumpDiagnosticsAsync(Process process)
    {
        while (await process.StandardError.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            EnqueueDiagnostic(line);
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
        bool redirectStandardInput)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardInput = redirectStandardInput
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return new Process { StartInfo = startInfo };
    }

    private static async Task StopProcessGracefullyAsync(Process process)
    {
        if (process.HasExited)
        {
            return;
        }

        try
        {
            await process.StandardInput.WriteLineAsync("q").ConfigureAwait(false);
            await process.StandardInput.FlushAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            // The process may have already closed its control stream.
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            await process.WaitForExitAsync().ConfigureAwait(false);
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
