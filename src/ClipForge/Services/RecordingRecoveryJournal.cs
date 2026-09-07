using System.Buffers.Binary;
using System.Security;
using System.Security.Cryptography;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using ClipForge.Capture;

namespace ClipForge.Services;

/// <summary>
/// Maintains the small, drive-independent recovery index for a full-session
/// recording. Segment payloads stay on the selected recording drive; this
/// journal only records completed/trusted segment identities so a hard process
/// termination cannot make a long recording undiscoverable.
/// </summary>
internal sealed class RecordingRecoveryJournal : IAsyncDisposable
{
    internal const string SessionMarkerFileName = ".clipforge-recorder-session";
    internal const string LiveRecordingRecoveryFileName =
        ".clipforge-live-recording.mp4";
    private const string LegacyLiveRecordingRecoveryFileName =
        ".clipforge-live-recording.part-1.mp4";
    internal const string LiveRecordingInvalidationMarkerFileName =
        ".clipforge-live-recording.invalid";
    private const int LegacySchemaVersion = 1;
    private const int SchemaVersion = 2;
    private const int MaximumJournalCandidates = 256;
    private const int MaximumJournalCandidatePages = 4;
    private const int MaximumJournalFamilyCandidates = 1024;
    private const int MaximumJournalRecords = 1_000_000;
    private const int MaximumJournalLineCharacters = 4096;
    private const long MaximumJournalBytes = 64L * 1024 * 1024;
    // Health/display recovery invalidates a 24-second Recorder window. Three
    // additional records cover a torn final append/checkpoint. The resulting
    // tail is duration-aware because schema-v1 journals used two-second
    // segments while current Recorder journals use ten-second segments.
    private const int UncleanRecoveryInvalidationSeconds = 24;
    private const int UncleanRecoveryExtraTailSegments = 3;
    private const int OutputFingerprintSampleBytes = 64 * 1024;

    private static readonly UTF8Encoding Utf8WithoutBom = new(
        encoderShouldEmitUTF8Identifier: false);
    private static readonly UTF8Encoding StrictUtf8WithoutBom = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        // Segment records are emitted every ten seconds. Omitting the many
        // unused optional fields keeps multi-day Recorder journals bounded
        // without changing the schema understood by existing installations.
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly Channel<JournalEnvelope> _records = Channel.CreateUnbounded<JournalEnvelope>(
        new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
    private readonly CancellationTokenSource _writerCancellation = new();
    private readonly FileStream _stream;
    private readonly StreamWriter _writer;
    private readonly Task _writerTask;
    private readonly string _workingJournalPath;
    private bool _requiresAtomicPublish;
    private Exception? _writerFailure;
    private int _closed;
    private int _disposeStarted;

    private RecordingRecoveryJournal(
        string journalPath,
        string workingJournalPath,
        string sessionId,
        string sessionDirectory,
        FileStream stream,
        StreamWriter writer,
        bool ownsSessionMarker)
    {
        JournalPath = journalPath;
        _workingJournalPath = workingJournalPath;
        _requiresAtomicPublish = !string.Equals(
            journalPath,
            workingJournalPath,
            StringComparison.OrdinalIgnoreCase);
        SessionId = sessionId;
        SessionDirectory = sessionDirectory;
        OwnsSessionMarker = ownsSessionMarker;
        _stream = stream;
        _writer = writer;
        _writerTask = Task.Run(WriteRecordsAsync);
    }

    internal string JournalPath { get; }

    internal string SessionId { get; }

    internal string SessionDirectory { get; }

    internal bool OwnsSessionMarker { get; }

    internal bool RequiresAtomicPublish => _requiresAtomicPublish;

    internal Exception? WriterFailure => Volatile.Read(ref _writerFailure);

    internal bool IsHealthy =>
        Volatile.Read(ref _closed) == 0 &&
        WriterFailure is null &&
        !_writerTask.IsCompleted;

    internal static string GetRecoveryRoot(string bufferRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bufferRoot);
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(bufferRoot));
        var name = Path.GetFileName(normalized);
        var parent = Path.GetDirectoryName(normalized);
        return name.StartsWith("WindowsSession-", StringComparison.OrdinalIgnoreCase) &&
               !string.IsNullOrWhiteSpace(parent)
            ? Path.Combine(parent, "RecorderRecovery")
            : Path.Combine(normalized, "RecorderRecovery");
    }

    internal static string GetJournalPath(string recoveryRoot, string sessionDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recoveryRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionDirectory);
        return Path.Combine(
            Path.GetFullPath(recoveryRoot),
            $"{Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(sessionDirectory)))}.jsonl");
    }

    internal static string GetJournalPath(
        string recoveryRoot,
        string sessionDirectory,
        string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recoveryRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionDirectory);
        if (!Guid.TryParseExact(sessionId, "N", out _))
        {
            throw new ArgumentException(
                "Recorder recovery session identity must be a 32-character GUID.",
                nameof(sessionId));
        }

        return Path.Combine(
            Path.GetFullPath(recoveryRoot),
            $"{Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(sessionDirectory)))}-{sessionId}.jsonl");
    }

    private static bool IsExpectedJournalPath(
        string recoveryRoot,
        string sessionDirectory,
        string sessionId,
        string journalPath)
    {
        if (string.Equals(
                GetJournalPath(recoveryRoot, sessionDirectory),
                journalPath,
                StringComparison.OrdinalIgnoreCase) ||
            string.Equals(
                GetJournalPath(recoveryRoot, sessionDirectory, sessionId),
                journalPath,
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var expectedPrefix =
            Path.GetFileNameWithoutExtension(
                GetJournalPath(recoveryRoot, sessionDirectory, sessionId)) +
            "-";
        var candidateName = Path.GetFileNameWithoutExtension(journalPath);
        return candidateName.StartsWith(
                   expectedPrefix,
                   StringComparison.OrdinalIgnoreCase) &&
               candidateName.Length == expectedPrefix.Length + 32 &&
               Guid.TryParseExact(
                   candidateName[expectedPrefix.Length..],
                   "N",
                   out _);
    }

    private static string GetRepairJournalPath(
        string recoveryRoot,
        string sessionDirectory,
        string sessionId) =>
        Path.Combine(
            Path.GetFullPath(recoveryRoot),
            $"{Path.GetFileNameWithoutExtension(GetJournalPath(recoveryRoot, sessionDirectory, sessionId))}-{Guid.NewGuid():N}.jsonl");

    internal static async Task<RecordingRecoveryJournal> CreateAsync(
        string recoveryRoot,
        string sessionDirectory,
        string bufferRoot,
        int framesPerSecond,
        bool hasAudio,
        CancellationToken cancellationToken) =>
        await CreateAsync(
                recoveryRoot,
                sessionDirectory,
                bufferRoot,
                framesPerSecond,
                hasAudio,
                FfmpegArgumentBuilder.SegmentSeconds,
                liveRecordingPath: null,
                cancellationToken)
            .ConfigureAwait(false);

    internal static async Task<RecordingRecoveryJournal> CreateAsync(
        string recoveryRoot,
        string sessionDirectory,
        string bufferRoot,
        int framesPerSecond,
        bool hasAudio,
        string? liveRecordingPath,
        CancellationToken cancellationToken) =>
        await CreateAsync(
                recoveryRoot,
                sessionDirectory,
                bufferRoot,
                framesPerSecond,
                hasAudio,
                FfmpegArgumentBuilder.SegmentSeconds,
                liveRecordingPath,
                cancellationToken)
            .ConfigureAwait(false);

    internal static async Task<RecordingRecoveryJournal> CreateAsync(
        string recoveryRoot,
        string sessionDirectory,
        string bufferRoot,
        int framesPerSecond,
        bool hasAudio,
        int segmentDurationSeconds,
        string? liveRecordingPath,
        CancellationToken cancellationToken) =>
        await CreateCoreAsync(
                recoveryRoot,
                sessionDirectory,
                bufferRoot,
                framesPerSecond,
                hasAudio,
                segmentDurationSeconds,
                liveRecordingPath,
                existingSessionId: null,
                cancellationToken)
            .ConfigureAwait(false);

    internal static async Task<RecordingRecoveryJournal> RepairAsync(
        string recoveryRoot,
        string sessionDirectory,
        string bufferRoot,
        int framesPerSecond,
        bool hasAudio,
        int segmentDurationSeconds,
        string? liveRecordingPath,
        CancellationToken cancellationToken)
    {
        if (!TryGetSessionMarkerId(sessionDirectory, out var existingSessionId) ||
            existingSessionId is null)
        {
            throw new InvalidDataException(
                "Recorder recovery could not authenticate the existing session marker.");
        }

        var existingJournalFamily = await TryReadJournalFamilyAsync(
                recoveryRoot,
                sessionDirectory,
                existingSessionId,
                cancellationToken)
            .ConfigureAwait(false);
        if (existingJournalFamily.Any(snapshot => !snapshot.Superseded))
        {
            throw new InvalidOperationException(
                "Recorder recovery refused to replace an already valid central locator.");
        }

        return await CreateCoreAsync(
                recoveryRoot,
                sessionDirectory,
                bufferRoot,
                framesPerSecond,
                hasAudio,
                segmentDurationSeconds,
                liveRecordingPath,
                existingSessionId,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<RecordingRecoveryJournal> CreateCoreAsync(
        string recoveryRoot,
        string sessionDirectory,
        string bufferRoot,
        int framesPerSecond,
        bool hasAudio,
        int segmentDurationSeconds,
        string? liveRecordingPath,
        string? existingSessionId,
        CancellationToken cancellationToken)
    {
        if (framesPerSecond is < 1 or > 240)
        {
            throw new ArgumentOutOfRangeException(nameof(framesPerSecond));
        }
        if (segmentDurationSeconds is < 1 or > 300)
        {
            throw new ArgumentOutOfRangeException(nameof(segmentDurationSeconds));
        }

        var normalizedRecoveryRoot = Path.GetFullPath(recoveryRoot);
        var normalizedBufferRoot = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(bufferRoot));
        var normalizedSessionDirectory = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(sessionDirectory));
        if (!IsDirectRegularSession(normalizedBufferRoot, normalizedSessionDirectory))
        {
            throw new InvalidOperationException(
                "Recorder recovery refused an unsafe session directory.");
        }

        string? normalizedLiveRecordingPath = null;
        if (!string.IsNullOrWhiteSpace(liveRecordingPath) &&
            !TryValidateLiveRecordingPath(
                normalizedSessionDirectory,
                liveRecordingPath,
                requireExistingFile: false,
                out normalizedLiveRecordingPath))
        {
            throw new InvalidOperationException(
                "Recorder recovery refused an unsafe live-output path.");
        }

        Directory.CreateDirectory(normalizedRecoveryRoot);
        var recoveryRootInfo = new DirectoryInfo(normalizedRecoveryRoot);
        if (!recoveryRootInfo.Exists ||
            (recoveryRootInfo.Attributes &
             (FileAttributes.Directory | FileAttributes.ReparsePoint)) !=
                FileAttributes.Directory)
        {
            throw new InvalidOperationException(
                "Recorder recovery refused an unsafe local journal directory.");
        }

        var sessionId = existingSessionId ?? Guid.NewGuid().ToString("N");
        var markerPath = Path.Combine(
            normalizedSessionDirectory,
            SessionMarkerFileName);
        var journalPath = existingSessionId is null
            ? GetJournalPath(
                normalizedRecoveryRoot,
                normalizedSessionDirectory,
                sessionId)
            : GetRepairJournalPath(
                normalizedRecoveryRoot,
                normalizedSessionDirectory,
                sessionId);
        var workingJournalPath = existingSessionId is null
            ? journalPath
            : Path.Combine(
                normalizedRecoveryRoot,
                $".{Path.GetFileName(journalPath)}.{Guid.NewGuid():N}.repair.tmp");
        FileStream? stream = null;
        StreamWriter? writer = null;
        var markerCreated = false;
        var journalCreated = false;
        try
        {
            if (existingSessionId is null)
            {
                await WriteDurableTextAsync(
                        markerPath,
                        sessionId,
                        cancellationToken)
                    .ConfigureAwait(false);
                markerCreated = true;
            }
            else
            {
                // A valid exact state may outlive a torn/corrupt central
                // locator. Reuse its authenticated session id, but build the
                // replacement beside it so the old locator remains intact
                // until a complete new journal can be atomically published.
            }

            stream = new FileStream(
                workingJournalPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            journalCreated = true;
            writer = new StreamWriter(
                stream,
                Utf8WithoutBom,
                bufferSize: 4096,
                leaveOpen: true);
            var header = new JournalRecord(
                Version: SchemaVersion,
                Kind: "session",
                SessionId: sessionId,
                SessionDirectory: normalizedSessionDirectory,
                BufferRoot: normalizedBufferRoot,
                FramesPerSecond: framesPerSecond,
                HasAudio: hasAudio,
                SegmentDurationSeconds: segmentDurationSeconds,
                LiveRecordingPath: normalizedLiveRecordingPath);
            await writer.WriteLineAsync(
                    JsonSerializer.Serialize(header, SerializerOptions)
                        .AsMemory(),
                    cancellationToken)
                .ConfigureAwait(false);
            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);

            return new RecordingRecoveryJournal(
                journalPath,
                workingJournalPath,
                sessionId,
                normalizedSessionDirectory,
                stream,
                writer,
                ownsSessionMarker: markerCreated);
        }
        catch
        {
            try
            {
                writer?.Dispose();
            }
            catch
            {
                // Preserve the creation failure; cleanup continues below.
            }

            try
            {
                stream?.Dispose();
            }
            catch
            {
                // Preserve the creation failure; owned files still need their
                // best-effort cleanup even when handle disposal also fails.
            }

            if (journalCreated)
            {
                TryDeleteFile(workingJournalPath);
            }
            if (markerCreated)
            {
                TryDeleteFile(markerPath);
            }
            throw;
        }
    }

    internal bool RecordCompleted(string path, long length, int segmentNumber) =>
        length > 0 &&
        TryValidateSegmentIdentity(
            SessionDirectory,
            path,
            segmentNumber,
            out _) &&
        TryRecord(new JournalRecord(
            Version: SchemaVersion,
            Kind: "add",
            // Segment names are deterministic beneath the authenticated
            // session directory. Do not repeat a long path and 32-byte session
            // id every ten seconds for multi-day recordings.
            SessionId: null,
            SegmentLength: length,
            SegmentNumber: segmentNumber));

    internal bool RecordUntrusted(string path, int segmentNumber) =>
        TryValidateSegmentIdentity(
            SessionDirectory,
            path,
            segmentNumber,
            out _) &&
        TryRecord(new JournalRecord(
            Version: SchemaVersion,
            Kind: "remove",
            SessionId: null,
            SegmentNumber: segmentNumber));

    internal bool RecordLiveOutputInvalidated()
    {
        // The direct MP4 is only an optimization. Once FFmpeg reports a FIFO
        // drop or capture recovery replaces the process, startup must never
        // mistake the old direct file for a complete recording. Keep the
        // append-only journal for audit/recovery and also create a tiny durable
        // marker beside the payload. The marker closes the asynchronous writer
        // race if Windows terminates ClipForge immediately after invalidation.
        var markerWritten = TryWriteLiveOutputInvalidationMarker();
        var recordQueued = TryRecord(new JournalRecord(
            Version: SchemaVersion,
            Kind: "invalidate-live",
            SessionId: SessionId));
        if (markerWritten)
        {
            return true;
        }

        if (!recordQueued)
        {
            return false;
        }

        try
        {
            return CheckpointAsync(CancellationToken.None)
                .GetAwaiter()
                .GetResult();
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                InvalidOperationException or OperationCanceledException or
                TimeoutException)
        {
            return false;
        }
    }

    internal async Task<bool> CheckpointAsync(CancellationToken cancellationToken)
    {
        if (!IsHealthy)
        {
            return false;
        }

        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_records.Writer.TryWrite(new JournalEnvelope(null, completion)))
        {
            return false;
        }

        try
        {
            return await completion.Task
                .WaitAsync(TimeSpan.FromSeconds(2), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    internal async Task<bool> CloseAsync(
        bool detached,
        TimeSpan? segmentTimelineDuration = null)
    {
        if (!detached && segmentTimelineDuration is not null)
        {
            throw new ArgumentException(
                "A Recorder segment timeline can only be sealed with a detached journal.",
                nameof(segmentTimelineDuration));
        }

        double? segmentTimelineDurationSeconds = null;
        if (segmentTimelineDuration is { } timelineDuration)
        {
            var timelineSeconds = timelineDuration.TotalSeconds;
            if (timelineDuration <= TimeSpan.Zero ||
                !double.IsFinite(timelineSeconds))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(segmentTimelineDuration));
            }

            segmentTimelineDurationSeconds = timelineSeconds;
        }

        if (Interlocked.Exchange(ref _closed, 1) != 0)
        {
            return await WaitForWriterCompletionAsync(cancelOnTimeout: false)
                .ConfigureAwait(false);
        }

        if (detached)
        {
            _records.Writer.TryWrite(new JournalEnvelope(
                new JournalRecord(
                    Version: SchemaVersion,
                    Kind: "detached",
                    SessionId: SessionId,
                    SegmentTimelineDurationSeconds:
                        segmentTimelineDurationSeconds),
                null));
        }

        _records.Writer.TryComplete();
        return await WaitForWriterCompletionAsync(cancelOnTimeout: true)
            .ConfigureAwait(false);
    }

    internal async Task PublishRepairAsync(
        string recoveryRoot,
        CancellationToken cancellationToken)
    {
        if (!_requiresAtomicPublish)
        {
            return;
        }

        if (Volatile.Read(ref _closed) == 0 || WriterFailure is not null)
        {
            throw new InvalidOperationException(
                "Recorder recovery cannot publish an incomplete locator repair.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var replacementInfo = new FileInfo(_workingJournalPath);
        if (!replacementInfo.Exists ||
            replacementInfo.Length is <= 0 or > MaximumJournalBytes ||
            (replacementInfo.Attributes &
             (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
        {
            throw new InvalidDataException(
                "Recorder recovery locator repair did not produce a regular journal.");
        }

        // If another path repaired the locator while this one was being built,
        // never overwrite its now-valid terminal state.
        if ((await TryReadJournalFamilyAsync(
                 recoveryRoot,
                 SessionDirectory,
                 SessionId,
                 cancellationToken).ConfigureAwait(false)).Any(
                snapshot => !snapshot.Superseded))
        {
            throw new InvalidOperationException(
                "Recorder recovery locator changed while its repair was being prepared.");
        }

        if (File.Exists(JournalPath))
        {
            throw new InvalidOperationException(
                "Recorder recovery locator appeared while its repair was being prepared.");
        }

        // Repairs publish to a fresh identity-bound path, never on top of the
        // corrupt/legacy locator that prompted them. CreateNew/no-overwrite is
        // the final filesystem CAS and cannot regress a concurrently written
        // terminal journal.
        File.Move(_workingJournalPath, JournalPath, overwrite: false);
        _requiresAtomicPublish = false;
    }

    internal void AbandonUnpublishedJournal()
    {
        if (_requiresAtomicPublish)
        {
            TryDeleteFile(_workingJournalPath);
        }
        else
        {
            TryDeleteFile(JournalPath);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
        {
            return;
        }

        try
        {
            _ = await CloseAsync(detached: false).ConfigureAwait(false);
        }
        finally
        {
            TryCancelWriter();
            if (_writerTask.IsCompleted)
            {
                _ = _writerTask.Exception;
                _writerCancellation.Dispose();
            }
            else
            {
                _ = DisposeWriterCancellationWhenCompleteAsync();
            }
        }
    }

    private async Task<bool> WaitForWriterCompletionAsync(bool cancelOnTimeout)
    {
        try
        {
            await _writerTask.WaitAsync(TimeSpan.FromSeconds(2))
                .ConfigureAwait(false);
            return WriterFailure is null;
        }
        catch (TimeoutException)
        {
            if (cancelOnTimeout)
            {
                TryCancelWriter();
            }

            return false;
        }
        catch (OperationCanceledException) when (_writerCancellation.IsCancellationRequested)
        {
            // A blocked local profile must not hold Recorder shutdown forever.
            return false;
        }
        catch (Exception exception) when (IsExpectedWriterFailure(exception))
        {
            RecordWriterFailure(exception);
            return false;
        }
    }

    private async Task DisposeWriterCancellationWhenCompleteAsync()
    {
        try
        {
            await _writerTask.ConfigureAwait(false);
        }
        catch
        {
            _ = _writerTask.Exception;
        }
        finally
        {
            _writerCancellation.Dispose();
        }
    }

    private void TryCancelWriter()
    {
        try
        {
            _writerCancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // A concurrent repeated DisposeAsync already released the token.
        }
    }

    internal static IReadOnlyList<string> EnumerateCandidatePaths(string recoveryRoot) =>
        EnumerateCandidatePathsCore(recoveryRoot, MaximumJournalCandidates);

    internal static IReadOnlyList<IReadOnlyList<string>> EnumerateCandidatePathPages(
        string recoveryRoot)
    {
        var candidates = EnumerateCandidatePathsCore(
            recoveryRoot,
            MaximumJournalCandidates * MaximumJournalCandidatePages);
        return candidates
            .Chunk(MaximumJournalCandidates)
            .Select(page => (IReadOnlyList<string>)page)
            .ToArray();
    }

    private static IReadOnlyList<string> EnumerateCandidatePathsCore(
        string recoveryRoot,
        int maximumCandidates)
    {
        try
        {
            var normalizedRoot = Path.GetFullPath(recoveryRoot);
            if (!Directory.Exists(normalizedRoot))
            {
                return [];
            }

            var rootInfo = new DirectoryInfo(normalizedRoot);
            if ((rootInfo.Attributes &
                 (FileAttributes.Directory | FileAttributes.ReparsePoint)) !=
                    FileAttributes.Directory)
            {
                return [];
            }

            return Directory.EnumerateFiles(
                    normalizedRoot,
                    "session-*.jsonl",
                    SearchOption.TopDirectoryOnly)
                .Select(path => new FileInfo(path))
                .Where(info =>
                    info.Exists &&
                    info.Length is > 0 and <= MaximumJournalBytes &&
                    (info.Attributes &
                     (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0)
                .OrderByDescending(info => info.LastWriteTimeUtc)
                .ThenBy(info => info.FullName, StringComparer.OrdinalIgnoreCase)
                .Take(maximumCandidates)
                .Select(info => info.FullName)
                .ToArray();
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                ArgumentException or NotSupportedException or SecurityException)
        {
            return [];
        }
    }

    internal static async Task<IReadOnlyList<RecordingRecoveryJournalSnapshot>>
        TryReadJournalFamilyAsync(
            string recoveryRoot,
            string sessionDirectory,
            string sessionId,
            CancellationToken cancellationToken)
    {
        var snapshots = new List<RecordingRecoveryJournalSnapshot>();
        foreach (var journalPath in EnumerateJournalFamilyCandidatePaths(
                     recoveryRoot,
                     sessionDirectory,
                     sessionId))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = await TryReadAsync(
                    recoveryRoot,
                    journalPath,
                    cancellationToken)
                .ConfigureAwait(false);
            if (snapshot is not null &&
                string.Equals(
                    snapshot.SessionId,
                    sessionId,
                    StringComparison.Ordinal) &&
                PathsEqual(snapshot.SessionDirectory, sessionDirectory))
            {
                snapshots.Add(snapshot);
            }
        }

        return snapshots;
    }

    private static IReadOnlyList<string> EnumerateJournalFamilyCandidatePaths(
        string recoveryRoot,
        string sessionDirectory,
        string sessionId)
    {
        try
        {
            if (!Guid.TryParseExact(sessionId, "N", out _))
            {
                return [];
            }

            var normalizedRoot = Path.GetFullPath(recoveryRoot);
            if (!Directory.Exists(normalizedRoot))
            {
                return [];
            }

            var rootInfo = new DirectoryInfo(normalizedRoot);
            if ((rootInfo.Attributes &
                 (FileAttributes.Directory | FileAttributes.ReparsePoint)) !=
                    FileAttributes.Directory)
            {
                return [];
            }

            var canonicalPath = GetJournalPath(
                normalizedRoot,
                sessionDirectory,
                sessionId);
            var legacyPath = GetJournalPath(normalizedRoot, sessionDirectory);
            var repairPattern =
                Path.GetFileNameWithoutExtension(canonicalPath) + "-*.jsonl";
            return Directory.EnumerateFiles(
                    normalizedRoot,
                    repairPattern,
                    SearchOption.TopDirectoryOnly)
                .Append(canonicalPath)
                .Append(legacyPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(path => IsExpectedJournalPath(
                    normalizedRoot,
                    sessionDirectory,
                    sessionId,
                    Path.GetFullPath(path)))
                .Select(path => new FileInfo(path))
                .Where(info =>
                    info.Exists &&
                    info.Length is > 0 and <= MaximumJournalBytes &&
                    (info.Attributes &
                     (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0)
                .OrderByDescending(info => info.LastWriteTimeUtc)
                .ThenBy(info => info.FullName, StringComparer.OrdinalIgnoreCase)
                .Take(MaximumJournalFamilyCandidates)
                .Select(info => info.FullName)
                .ToArray();
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                ArgumentException or NotSupportedException or SecurityException)
        {
            return [];
        }
    }

    private static bool PathsEqual(string left, string right)
    {
        try
        {
            return string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                ArgumentException or NotSupportedException or SecurityException)
        {
            return false;
        }
    }

    internal static async Task<RecordingRecoveryJournalSnapshot?> TryReadAsync(
        string recoveryRoot,
        string journalPath,
        CancellationToken cancellationToken)
    {
        try
        {
            var normalizedRoot = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(recoveryRoot));
            var normalizedJournalPath = Path.GetFullPath(journalPath);
            if (!string.Equals(
                    Path.GetDirectoryName(normalizedJournalPath),
                    normalizedRoot,
                    StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var journalInfo = new FileInfo(normalizedJournalPath);
            if (!journalInfo.Exists ||
                journalInfo.Length is <= 0 or > MaximumJournalBytes ||
                (journalInfo.Attributes &
                 (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            {
                return null;
            }

            await using var stream = new FileStream(
                normalizedJournalPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var reader = new StreamReader(
                stream,
                Utf8WithoutBom,
                detectEncodingFromByteOrderMarks: true,
                bufferSize: 4096,
                leaveOpen: false);
            var headerLine = await reader.ReadLineAsync(cancellationToken)
                .ConfigureAwait(false);
            var header = DeserializeRecord(headerLine);
            if (header is null ||
                !IsSupportedSchemaVersion(header.Version) ||
                !string.Equals(header.Kind, "session", StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(header.SessionId) ||
                header.SessionId.Length != 32 ||
                string.IsNullOrWhiteSpace(header.SessionDirectory) ||
                string.IsNullOrWhiteSpace(header.BufferRoot) ||
                header.FramesPerSecond is < 1 or > 240 ||
                header.HasAudio is null ||
                header.SegmentTimelineDurationSeconds is not null ||
                header.Version == SchemaVersion &&
                header.SegmentDurationSeconds is null ||
                header.SegmentDurationSeconds is < 1 or > 300)
            {
                return null;
            }

            var sessionDirectory = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(header.SessionDirectory));
            var segmentDurationSeconds = header.SegmentDurationSeconds ??
                FfmpegArgumentBuilder.SegmentSeconds;
            var bufferRoot = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(header.BufferRoot));
            if (!IsDirectSessionIdentity(bufferRoot, sessionDirectory) ||
                !IsExpectedJournalPath(
                    normalizedRoot,
                    sessionDirectory,
                    header.SessionId,
                    normalizedJournalPath))
            {
                return null;
            }

            string? journalLiveRecordingPath = null;
            if (!string.IsNullOrWhiteSpace(header.LiveRecordingPath) &&
                !TryValidateLiveRecordingPath(
                    sessionDirectory,
                    header.LiveRecordingPath,
                    requireExistingFile: false,
                    out journalLiveRecordingPath))
            {
                return null;
            }

            var selected = new SortedDictionary<int, JournalSegment>();
            var detached = false;
            double? detachedSegmentTimelineSeconds = null;
            var discarded = false;
            var superseded = false;
            var liveOutputInvalidated = false;
            string? committingOutputPath = null;
            long? committingOutputLength = null;
            string? committingOutputFingerprint = null;
            string? committedOutputPath = null;
            long? committedOutputLength = null;
            string? committedOutputFingerprint = null;
            var sawSegmentRecord = false;
            var recordCount = 1;
            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++recordCount > MaximumJournalRecords ||
                    line.Length > MaximumJournalLineCharacters)
                {
                    return null;
                }

                var record = DeserializeRecord(line);
                if (record is null)
                {
                    // A hard termination can leave only the final append torn.
                    // Earlier flushed records remain authoritative.
                    if (await reader.ReadLineAsync(cancellationToken)
                            .ConfigureAwait(false) is null)
                    {
                        break;
                    }

                    return null;
                }

                if (record.Version != header.Version ||
                    !HasValidRecordSessionIdentity(
                        record,
                        header.SessionId,
                        header.Version) ||
                    record.SegmentTimelineDurationSeconds is not null &&
                    (header.Version != SchemaVersion ||
                     !string.Equals(
                         record.Kind,
                         "detached",
                         StringComparison.Ordinal)))
                {
                    return null;
                }

                if (superseded &&
                    !string.Equals(record.Kind, "superseded", StringComparison.Ordinal) ||
                    discarded &&
                    !string.Equals(record.Kind, "discarded", StringComparison.Ordinal) &&
                    !string.Equals(record.Kind, "superseded", StringComparison.Ordinal))
                {
                    return null;
                }

                switch (record.Kind)
                {
                    case "add":
                        sawSegmentRecord = true;
                        if (!HasValidSegmentPathForSchema(record, header.Version) ||
                            !TryValidateSegmentIdentity(
                                sessionDirectory,
                                record.SegmentPath,
                                record.SegmentNumber,
                                out var addPath) ||
                            record.SegmentLength is null or <= 0)
                        {
                            return null;
                        }

                        selected[record.SegmentNumber.GetValueOrDefault()] = new JournalSegment(
                            addPath,
                            record.SegmentLength.Value);
                        break;

                    case "remove":
                        sawSegmentRecord = true;
                        if (!HasValidSegmentPathForSchema(record, header.Version) ||
                            !TryValidateSegmentIdentity(
                                sessionDirectory,
                                record.SegmentPath,
                                record.SegmentNumber,
                                out _))
                        {
                            return null;
                        }

                        _ = selected.Remove(record.SegmentNumber.GetValueOrDefault());
                        break;

                    case "detached":
                        if (detached ||
                            record.SegmentTimelineDurationSeconds is { } timelineSeconds &&
                            (!double.IsFinite(timelineSeconds) || timelineSeconds <= 0))
                        {
                            return null;
                        }

                        detached = true;
                        detachedSegmentTimelineSeconds =
                            record.SegmentTimelineDurationSeconds;
                        break;

                    case "invalidate-live":
                        liveOutputInvalidated = true;
                        break;

                    case "discarded":
                        if (!string.IsNullOrWhiteSpace(committingOutputPath) ||
                            !string.IsNullOrWhiteSpace(committedOutputPath))
                        {
                            return null;
                        }

                        discarded = true;
                        break;

                    case "superseded":
                        superseded = true;
                        break;

                    case "committing":
                        var hasNewCommitIdentity =
                            record.FinalOutputLength is > 0 &&
                            IsValidOutputFingerprint(
                                record.FinalOutputFingerprint);
                        var hasLegacyCommitIdentity =
                            header.Version == LegacySchemaVersion &&
                            record.FinalOutputLength is null &&
                            record.FinalOutputFingerprint is null;
                        if (string.IsNullOrWhiteSpace(record.FinalOutputPath) ||
                            !hasNewCommitIdentity &&
                            !hasLegacyCommitIdentity)
                        {
                            return null;
                        }

                        committingOutputPath = Path.GetFullPath(record.FinalOutputPath);
                        committingOutputLength = record.FinalOutputLength;
                        committingOutputFingerprint =
                            record.FinalOutputFingerprint;
                        break;

                    case "committed":
                        var hasValidCommittedIdentity =
                            record.FinalOutputLength is > 0 &&
                            (IsValidOutputFingerprint(
                                 record.FinalOutputFingerprint) ||
                             header.Version == LegacySchemaVersion &&
                             record.FinalOutputFingerprint is null);
                        if (string.IsNullOrWhiteSpace(record.FinalOutputPath) ||
                            !hasValidCommittedIdentity)
                        {
                            return null;
                        }

                        committedOutputPath = Path.GetFullPath(record.FinalOutputPath);
                        committedOutputLength = record.FinalOutputLength;
                        committedOutputFingerprint =
                            record.FinalOutputFingerprint;
                        if (!string.IsNullOrWhiteSpace(committingOutputPath) &&
                            (!string.Equals(
                                 committedOutputPath,
                                 committingOutputPath,
                                 StringComparison.OrdinalIgnoreCase) ||
                             committingOutputLength is not null &&
                             committedOutputLength != committingOutputLength ||
                             committingOutputFingerprint is not null &&
                             !string.Equals(
                                 committedOutputFingerprint,
                                 committingOutputFingerprint,
                                 StringComparison.Ordinal)))
                        {
                            return null;
                        }

                        break;

                    default:
                        return null;
                }
            }

            TimeSpan? detachedSegmentTimelineDuration = null;
            if (detachedSegmentTimelineSeconds is { } persistedTimelineSeconds &&
                !TryResolveSegmentTimelineDuration(
                    persistedTimelineSeconds,
                    selected.Count,
                    segmentDurationSeconds,
                    header.FramesPerSecond.GetValueOrDefault(),
                    out detachedSegmentTimelineDuration))
            {
                return null;
            }

            if (!detached)
            {
                // Exclusions are append-only too. If ClipForge was killed during
                // a fault renewal, conservatively drop more than the complete
                // invalidation window so a torn final remove can never be used.
                foreach (var segmentNumber in selected.Keys
                             .TakeLast(GetUncleanRecoverySafetyTailSegments(
                                 segmentDurationSeconds))
                             .ToArray())
                {
                    _ = selected.Remove(segmentNumber);
                }
            }

            var sourceIdentityVerified =
                IsDirectRegularSession(bufferRoot, sessionDirectory) &&
                HasMatchingSessionMarker(sessionDirectory, header.SessionId);
            var liveOutputWasInvalidated =
                liveOutputInvalidated ||
                HasLiveOutputInvalidationMarker(sessionDirectory);
            string? liveRecordingRecoveryPath = null;
            if (sourceIdentityVerified &&
                !liveOutputWasInvalidated &&
                !string.IsNullOrWhiteSpace(journalLiveRecordingPath))
            {
                _ = TryValidateLiveRecordingPath(
                    sessionDirectory,
                    journalLiveRecordingPath,
                    requireExistingFile: true,
                    out liveRecordingRecoveryPath);
            }
            var committedOutputExists =
                IsSafeCommittedOutputPath(
                    committedOutputPath,
                    bufferRoot,
                    sessionDirectory) &&
                (committedOutputFingerprint is null
                    ? IsRegularFileWithExactLength(
                        committedOutputPath,
                        committedOutputLength)
                    : IsRegularFileWithExactIdentity(
                        committedOutputPath,
                        committedOutputLength,
                        committedOutputFingerprint));
            var interruptedCommitOutputExists =
                !committedOutputExists &&
                IsSafeCommittedOutputPath(
                    committingOutputPath,
                    bufferRoot,
                    sessionDirectory) &&
                IsRegularFileWithExactIdentity(
                    committingOutputPath,
                    committingOutputLength,
                    committingOutputFingerprint);
            var resolvedCommittedOutputPath = committedOutputExists
                ? committedOutputPath
                : interruptedCommitOutputExists
                    ? committingOutputPath
                    : null;
            var commitTerminalRecorded =
                !string.IsNullOrWhiteSpace(committingOutputPath) ||
                !string.IsNullOrWhiteSpace(committedOutputPath);
            if (superseded)
            {
                return new RecordingRecoveryJournalSnapshot(
                    header.SessionId,
                    sessionDirectory,
                    bufferRoot,
                    [],
                    0,
                    header.FramesPerSecond.GetValueOrDefault(),
                    header.HasAudio.GetValueOrDefault(),
                    SourceAvailable: false,
                    Detached: detached,
                    JournalPath: normalizedJournalPath,
                    CommittedOutputPath: null,
                    MissingSegmentCount: 0,
                    LiveRecordingRecoveryPath: null,
                    SegmentDurationSeconds: segmentDurationSeconds,
                    CommitTerminalRecorded: commitTerminalRecorded,
                    Superseded: true);
            }

            if (discarded)
            {
                return new RecordingRecoveryJournalSnapshot(
                    header.SessionId,
                    sessionDirectory,
                    bufferRoot,
                    [],
                    0,
                    header.FramesPerSecond.GetValueOrDefault(),
                    header.HasAudio.GetValueOrDefault(),
                    SourceAvailable: sourceIdentityVerified,
                    Detached: detached,
                    JournalPath: normalizedJournalPath,
                    CommittedOutputPath: null,
                    MissingSegmentCount: 0,
                    Discarded: true,
                    LiveRecordingRecoveryPath: null,
                    SegmentDurationSeconds: segmentDurationSeconds,
                    CommitTerminalRecorded: false);
            }

            if (!string.IsNullOrWhiteSpace(resolvedCommittedOutputPath))
            {
                return new RecordingRecoveryJournalSnapshot(
                    header.SessionId,
                    sessionDirectory,
                    bufferRoot,
                    [],
                    0,
                    header.FramesPerSecond.GetValueOrDefault(),
                    header.HasAudio.GetValueOrDefault(),
                    SourceAvailable: sourceIdentityVerified,
                    Detached: detached,
                    JournalPath: normalizedJournalPath,
                    CommittedOutputPath: resolvedCommittedOutputPath,
                    MissingSegmentCount: 0,
                    LiveRecordingRecoveryPath: null,
                    SegmentDurationSeconds: segmentDurationSeconds,
                    CommitTerminalRecorded: true);
            }

            if (!sourceIdentityVerified)
            {
                return new RecordingRecoveryJournalSnapshot(
                    header.SessionId,
                    sessionDirectory,
                    bufferRoot,
                    [],
                    0,
                    header.FramesPerSecond.GetValueOrDefault(),
                    header.HasAudio.GetValueOrDefault(),
                    SourceAvailable: false,
                    Detached: detached,
                    JournalPath: normalizedJournalPath,
                    CommittedOutputPath: null,
                    MissingSegmentCount: 0,
                    LiveRecordingRecoveryPath: null,
                    SegmentDurationSeconds: segmentDurationSeconds,
                    CommitTerminalRecorded: commitTerminalRecorded);
            }

            if (!detached && !sawSegmentRecord)
            {
                foreach (var physicalSegment in EnumeratePhysicalFallbackSegments(
                             sessionDirectory,
                             segmentDurationSeconds,
                             preserveInitialSegment:
                                 header.Version != LegacySchemaVersion))
                {
                    selected[physicalSegment.SegmentNumber] = new JournalSegment(
                        physicalSegment.Path,
                        physicalSegment.Length);
                }
            }

            var paths = new List<string>(selected.Count);
            long bytes = 0;
            var missingSegmentCount = 0;
            foreach (var segment in selected.Values)
            {
                if (!TryReadRegularSegmentLength(segment.Path, out var actualLength) ||
                    actualLength != segment.Length ||
                    bytes > long.MaxValue - actualLength)
                {
                    missingSegmentCount++;
                    continue;
                }

                bytes += actualLength;
                paths.Add(segment.Path);
            }

            return new RecordingRecoveryJournalSnapshot(
                header.SessionId,
                sessionDirectory,
                bufferRoot,
                paths,
                bytes,
                header.FramesPerSecond.GetValueOrDefault(),
                header.HasAudio.GetValueOrDefault(),
                SourceAvailable: true,
                Detached: detached,
                JournalPath: normalizedJournalPath,
                CommittedOutputPath: null,
                MissingSegmentCount: missingSegmentCount,
                LiveRecordingRecoveryPath: liveRecordingRecoveryPath,
                SegmentDurationSeconds: segmentDurationSeconds,
                LiveOutputInvalidated: liveOutputWasInvalidated,
                CommitTerminalRecorded: commitTerminalRecorded,
                SegmentTimelineDuration:
                    missingSegmentCount == 0
                        ? detachedSegmentTimelineDuration
                        : null);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException or
                ArgumentException or NotSupportedException or SecurityException)
        {
            return null;
        }
    }

    internal static string ComputeOutputFingerprint(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var normalizedPath = Path.GetFullPath(path);
        var info = new FileInfo(normalizedPath);
        if (!info.Exists ||
            info.Length <= 0 ||
            (info.Attributes &
             (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
        {
            throw new InvalidDataException(
                "Recorder output identity requires a non-empty regular file.");
        }

        using var stream = new FileStream(
            normalizedPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: OutputFingerprintSampleBytes,
            FileOptions.RandomAccess);
        var length = stream.Length;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        // This is a bounded commit-identity token rather than a full media
        // checksum: hashing a 40+ GiB Recorder output at Stop would undo the
        // metadata-only fast path. Bind the sampled bytes to the file's durable
        // Windows identity and last-write token so a same-length middle edit or
        // path replacement is rejected without rereading the complete video.
        // The FileStream denies write/delete sharing while these values and the
        // byte samples are captured, keeping one coherent observation.
        Span<byte> identityBytes = stackalloc byte[1 + sizeof(long) * 5];
        identityBytes.Clear();
        BinaryPrimitives.WriteInt64LittleEndian(
            identityBytes.Slice(1, sizeof(long)),
            length);
        BinaryPrimitives.WriteInt64LittleEndian(
            identityBytes.Slice(1 + sizeof(long), sizeof(long)),
            File.GetLastWriteTimeUtc(normalizedPath).Ticks);
        if (ClipLibraryService.TryGetCurrentFileIdentity(
                normalizedPath,
                out var fileIdentity))
        {
            identityBytes[0] = 1;
            BinaryPrimitives.WriteUInt64LittleEndian(
                identityBytes.Slice(1 + sizeof(long) * 2, sizeof(ulong)),
                fileIdentity.VolumeSerialNumber);
            BinaryPrimitives.WriteUInt64LittleEndian(
                identityBytes.Slice(1 + sizeof(long) * 3, sizeof(ulong)),
                fileIdentity.FileIdLow);
            BinaryPrimitives.WriteUInt64LittleEndian(
                identityBytes.Slice(1 + sizeof(long) * 4, sizeof(ulong)),
                fileIdentity.FileIdHigh);
        }

        hash.AppendData(identityBytes);

        AppendOutputSamples(stream, hash);
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    // A copy has a new file ID and last-write token even when every byte is
    // unchanged. Use this bounded content check only while the copy operation
    // pins the source against writes; commit/recovery still requires the full
    // file-identity token above.
    internal static string ComputeOutputContentSampleFingerprint(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: OutputFingerprintSampleBytes,
            FileOptions.RandomAccess);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> lengthBytes = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64LittleEndian(lengthBytes, stream.Length);
        hash.AppendData(lengthBytes);
        AppendOutputSamples(stream, hash);
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static void AppendOutputSamples(
        FileStream stream,
        IncrementalHash hash)
    {
        var length = stream.Length;
        var sample = new byte[OutputFingerprintSampleBytes];
        var firstLength = checked((int)Math.Min(length, sample.Length));
        stream.ReadExactly(sample.AsSpan(0, firstLength));
        hash.AppendData(sample, 0, firstLength);

        if (length > firstLength)
        {
            var tailStart = Math.Max(firstLength, length - sample.Length);
            var tailLength = checked((int)(length - tailStart));
            stream.Position = tailStart;
            stream.ReadExactly(sample.AsSpan(0, tailLength));
            hash.AppendData(sample, 0, tailLength);
        }

    }

    internal static bool HasMatchingOutputIdentity(
        string? path,
        long expectedLength,
        string expectedFingerprint) =>
        IsRegularFileWithExactIdentity(
            path,
            expectedLength,
            expectedFingerprint);

    internal static Task MarkCommittingAsync(
        string recoveryRoot,
        string journalPath,
        string sessionId,
        string finalOutputPath,
        long finalOutputLength,
        string finalOutputFingerprint,
        CancellationToken cancellationToken)
    {
        ValidateOutputIdentity(
            finalOutputLength,
            finalOutputFingerprint);
        return AppendTerminalRecordAsync(
            recoveryRoot,
            journalPath,
            new JournalRecord(
                Version: SchemaVersion,
                Kind: "committing",
                SessionId: sessionId,
                FinalOutputPath: Path.GetFullPath(finalOutputPath),
                FinalOutputLength: finalOutputLength,
                FinalOutputFingerprint: finalOutputFingerprint),
            cancellationToken);
    }

    private static void ValidateOutputIdentity(
        long finalOutputLength,
        string finalOutputFingerprint)
    {
        if (finalOutputLength <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(finalOutputLength));
        }

        if (!IsValidOutputFingerprint(finalOutputFingerprint))
        {
            throw new ArgumentException(
                "Recorder output fingerprint must be a SHA-256 hex value.",
                nameof(finalOutputFingerprint));
        }
    }

    internal static Task MarkCommittedAsync(
        string recoveryRoot,
        string journalPath,
        string sessionId,
        string finalOutputPath,
        long finalOutputLength,
        string finalOutputFingerprint,
        CancellationToken cancellationToken)
    {
        ValidateOutputIdentity(
            finalOutputLength,
            finalOutputFingerprint);

        return AppendTerminalRecordAsync(
            recoveryRoot,
            journalPath,
            new JournalRecord(
                Version: SchemaVersion,
                Kind: "committed",
                SessionId: sessionId,
                FinalOutputPath: Path.GetFullPath(finalOutputPath),
                FinalOutputLength: finalOutputLength,
                FinalOutputFingerprint: finalOutputFingerprint),
            cancellationToken);
    }

    internal static Task MarkDiscardedAsync(
        string recoveryRoot,
        string journalPath,
        string sessionId,
        CancellationToken cancellationToken)
        => AppendTerminalRecordAsync(
            recoveryRoot,
            journalPath,
            new JournalRecord(
                Version: SchemaVersion,
                Kind: "discarded",
                SessionId: sessionId),
            cancellationToken);

    internal static Task MarkSupersededAsync(
        string recoveryRoot,
        string journalPath,
        string sessionId,
        CancellationToken cancellationToken)
        => AppendTerminalRecordAsync(
            recoveryRoot,
            journalPath,
            new JournalRecord(
                Version: SchemaVersion,
                Kind: "superseded",
                SessionId: sessionId),
            cancellationToken);

    internal static bool TryDeleteJournal(string? journalPath)
    {
        if (string.IsNullOrWhiteSpace(journalPath))
        {
            return true;
        }

        return TryDeleteFile(journalPath);
    }

    internal static bool CanRetireMissingSession(
        string bufferRoot,
        string sessionDirectory)
    {
        try
        {
            var normalizedRoot = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(bufferRoot));
            var normalizedSession = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(sessionDirectory));
            if (!IsDirectSessionIdentity(normalizedRoot, normalizedSession) ||
                !IsRegularDirectory(normalizedRoot))
            {
                return false;
            }

            try
            {
                _ = File.GetAttributes(normalizedSession);
                return false;
            }
            catch (Exception exception) when (
                exception is FileNotFoundException or DirectoryNotFoundException)
            {
                // Re-read the root after observing the missing child. This
                // distinguishes a genuinely deleted session from a drive/root
                // that disappeared between the two checks.
                return IsRegularDirectory(normalizedRoot);
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                ArgumentException or NotSupportedException or SecurityException)
        {
            return false;
        }
    }

    private static async Task AppendTerminalRecordAsync(
        string recoveryRoot,
        string journalPath,
        JournalRecord record,
        CancellationToken cancellationToken)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(recoveryRoot));
        var normalizedJournalPath = Path.GetFullPath(journalPath);
        if (!string.Equals(
                Path.GetDirectoryName(normalizedJournalPath),
                normalizedRoot,
                StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(record.SessionId) ||
            record.SessionId.Length != 32)
        {
            throw new InvalidOperationException(
                "Recorder recovery refused an unsafe terminal marker.");
        }

        var rootInfo = new DirectoryInfo(normalizedRoot);
        var journalInfo = new FileInfo(normalizedJournalPath);
        if (!rootInfo.Exists ||
            (rootInfo.Attributes &
             (FileAttributes.Directory | FileAttributes.ReparsePoint)) !=
                FileAttributes.Directory ||
            !journalInfo.Exists ||
            journalInfo.Length is <= 0 or > MaximumJournalBytes ||
            (journalInfo.Attributes &
             (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
        {
            throw new InvalidOperationException(
                "Recorder recovery terminal metadata is unavailable or unsafe.");
        }

        var snapshot = await TryReadAsync(
                normalizedRoot,
                normalizedJournalPath,
                cancellationToken)
            .ConfigureAwait(false);
        if (snapshot is null ||
            !string.Equals(snapshot.SessionId, record.SessionId, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Recorder recovery terminal metadata is invalid or changed identity.");
        }

        await using var stream = new FileStream(
            normalizedJournalPath,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough);
        journalInfo.Refresh();
        if (!journalInfo.Exists ||
            journalInfo.Length is <= 0 or > MaximumJournalBytes ||
            (journalInfo.Attributes &
             (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0 ||
            stream.Length != journalInfo.Length)
        {
            throw new InvalidOperationException(
                "Recorder recovery terminal metadata changed or became unsafe.");
        }

        var journalBytes = GC.AllocateUninitializedArray<byte>((int)stream.Length);
        await stream.ReadExactlyAsync(journalBytes, cancellationToken).ConfigureAwait(false);
        var appendPlan = FindTerminalAppendPlan(
            journalBytes,
            normalizedRoot,
            normalizedJournalPath,
            record.SessionId);
        if (appendPlan.Superseded &&
            !string.Equals(record.Kind, "superseded", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Recorder recovery refused to change a superseded locator.");
        }

        if (string.Equals(record.Kind, "discarded", StringComparison.Ordinal) &&
            appendPlan.HasCommitTerminal)
        {
            throw new InvalidOperationException(
                "Recorder recovery refused to discard a session with an unfinished or completed commit record.");
        }

        record = record with { Version = appendPlan.SchemaVersion };

        var separatorBytes = appendPlan.NeedsSeparator
            ? Utf8WithoutBom.GetBytes(Environment.NewLine)
            : [];
        var terminalBytes = Utf8WithoutBom.GetBytes(
            JsonSerializer.Serialize(record, SerializerOptions) + Environment.NewLine);
        if (appendPlan.Offset > MaximumJournalBytes - separatorBytes.Length - terminalBytes.Length)
        {
            throw new InvalidOperationException(
                "Recorder recovery terminal metadata reached its safe size limit.");
        }

        // A crash after this truncation can leave only a valid journal prefix or
        // another torn final append. The caller may delete source data only after
        // the new terminal record has been flushed durably below.
        stream.SetLength(appendPlan.Offset);
        stream.Position = appendPlan.Offset;
        if (separatorBytes.Length > 0)
        {
            await stream.WriteAsync(separatorBytes, cancellationToken).ConfigureAwait(false);
        }

        await stream.WriteAsync(terminalBytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
    }

    private static TerminalAppendPlan FindTerminalAppendPlan(
        byte[] journalBytes,
        string recoveryRoot,
        string journalPath,
        string expectedSessionId)
    {
        if (journalBytes.Length == 0)
        {
            throw new InvalidDataException(
                "Recorder recovery terminal metadata has no session header.");
        }

        JournalRecord? header = null;
        string? sessionDirectory = null;
        var selectedSegmentNumbers = new HashSet<int>();
        var detached = false;
        double? detachedSegmentTimelineSeconds = null;
        var discarded = false;
        var superseded = false;
        string? committingOutputPath = null;
        long? committingOutputLength = null;
        string? committingOutputFingerprint = null;
        string? committedOutputPath = null;
        long? committedOutputLength = null;
        string? committedOutputFingerprint = null;
        var recordCount = 0;
        var lineStart = 0;
        while (lineStart < journalBytes.Length)
        {
            var lineEnd = lineStart;
            while (lineEnd < journalBytes.Length &&
                   journalBytes[lineEnd] is not (byte)'\r' and not (byte)'\n')
            {
                lineEnd++;
            }

            var nextLineStart = lineEnd;
            if (nextLineStart < journalBytes.Length)
            {
                nextLineStart++;
                if (journalBytes[lineEnd] == (byte)'\r' &&
                    nextLineStart < journalBytes.Length &&
                    journalBytes[nextLineStart] == (byte)'\n')
                {
                    nextLineStart++;
                }
            }

            if (++recordCount > MaximumJournalRecords)
            {
                throw new InvalidDataException(
                    "Recorder recovery terminal metadata contains too many records.");
            }

            var encodedLine = journalBytes.AsSpan(lineStart, lineEnd - lineStart);
            if (recordCount == 1 &&
                encodedLine.Length >= 3 &&
                encodedLine[0] == 0xEF &&
                encodedLine[1] == 0xBB &&
                encodedLine[2] == 0xBF)
            {
                encodedLine = encodedLine[3..];
            }

            var lineTooLong = encodedLine.Length > MaximumJournalLineCharacters * 4;
            string? line = null;
            try
            {
                if (!lineTooLong)
                {
                    line = StrictUtf8WithoutBom.GetString(encodedLine);
                    lineTooLong = line.Length > MaximumJournalLineCharacters;
                }
            }
            catch (DecoderFallbackException)
            {
                // A hard termination can split the final UTF-8 sequence too.
            }

            var parsedRecord = !lineTooLong
                ? DeserializeRecord(line)
                : null;
            if (parsedRecord is null)
            {
                var isFinalPhysicalLine = nextLineStart >= journalBytes.Length;
                if (recordCount == 1 || !isFinalPhysicalLine || lineTooLong)
                {
                    throw new InvalidDataException(
                        "Recorder recovery terminal metadata is corrupt before its final record.");
                }

                EnsureValidTerminalSegmentTimeline(
                    detached,
                    detachedSegmentTimelineSeconds,
                    selectedSegmentNumbers.Count,
                    header!);

                return new TerminalAppendPlan(
                    lineStart,
                    NeedsSeparator: false,
                    header!.Version,
                    HasCommitTerminal:
                        !string.IsNullOrWhiteSpace(committingOutputPath) ||
                        !string.IsNullOrWhiteSpace(committedOutputPath),
                    Superseded: superseded);
            }

            if (recordCount == 1)
            {
                header = parsedRecord;
                if (!IsSupportedSchemaVersion(header.Version) ||
                    !string.Equals(header.Kind, "session", StringComparison.Ordinal) ||
                    !string.Equals(
                        header.SessionId,
                        expectedSessionId,
                        StringComparison.Ordinal) ||
                    string.IsNullOrWhiteSpace(header.SessionDirectory) ||
                    string.IsNullOrWhiteSpace(header.BufferRoot) ||
                    header.FramesPerSecond is < 1 or > 240 ||
                    header.HasAudio is null ||
                    header.SegmentTimelineDurationSeconds is not null ||
                    header.Version == SchemaVersion &&
                    header.SegmentDurationSeconds is null ||
                    header.SegmentDurationSeconds is < 1 or > 300)
                {
                    throw new InvalidDataException(
                        "Recorder recovery terminal metadata changed identity.");
                }

                sessionDirectory = Path.TrimEndingDirectorySeparator(
                    Path.GetFullPath(header.SessionDirectory));
                var bufferRoot = Path.TrimEndingDirectorySeparator(
                    Path.GetFullPath(header.BufferRoot));
                if (!IsDirectSessionIdentity(bufferRoot, sessionDirectory) ||
                    !IsExpectedJournalPath(
                        recoveryRoot,
                        sessionDirectory,
                        expectedSessionId,
                        journalPath))
                {
                    throw new InvalidDataException(
                        "Recorder recovery terminal metadata changed storage identity.");
                }

                if (!string.IsNullOrWhiteSpace(header.LiveRecordingPath) &&
                    !TryValidateLiveRecordingPath(
                        sessionDirectory,
                        header.LiveRecordingPath,
                        requireExistingFile: false,
                        out _))
                {
                    throw new InvalidDataException(
                        "Recorder recovery terminal metadata contains an unsafe live-output path.");
                }
            }
            else
            {
                if (header is null || sessionDirectory is null ||
                    parsedRecord.Version != header.Version ||
                    !HasValidRecordSessionIdentity(
                        parsedRecord,
                        expectedSessionId,
                        header.Version) ||
                    parsedRecord.SegmentTimelineDurationSeconds is not null &&
                    (header.Version != SchemaVersion ||
                     !string.Equals(
                         parsedRecord.Kind,
                         "detached",
                         StringComparison.Ordinal)) ||
                    (superseded &&
                     !string.Equals(
                         parsedRecord.Kind,
                         "superseded",
                         StringComparison.Ordinal)) ||
                    (discarded &&
                     !string.Equals(
                         parsedRecord.Kind,
                         "discarded",
                         StringComparison.Ordinal) &&
                     !string.Equals(
                         parsedRecord.Kind,
                         "superseded",
                         StringComparison.Ordinal)))
                {
                    throw new InvalidDataException(
                        "Recorder recovery terminal metadata contains an invalid record identity.");
                }

                switch (parsedRecord.Kind)
                {
                    case "add":
                        if (!HasValidSegmentPathForSchema(
                                parsedRecord,
                                header.Version) ||
                            !TryValidateSegmentIdentity(
                                sessionDirectory,
                                parsedRecord.SegmentPath,
                                parsedRecord.SegmentNumber,
                                out _) ||
                            parsedRecord.SegmentLength is null or <= 0)
                        {
                            throw new InvalidDataException(
                                "Recorder recovery terminal metadata contains an invalid segment.");
                        }

                        _ = selectedSegmentNumbers.Add(
                            parsedRecord.SegmentNumber.GetValueOrDefault());

                        break;

                    case "remove":
                        if (!HasValidSegmentPathForSchema(
                                parsedRecord,
                                header.Version) ||
                            !TryValidateSegmentIdentity(
                                sessionDirectory,
                                parsedRecord.SegmentPath,
                                parsedRecord.SegmentNumber,
                                out _))
                        {
                            throw new InvalidDataException(
                                "Recorder recovery terminal metadata contains an invalid segment exclusion.");
                        }

                        _ = selectedSegmentNumbers.Remove(
                            parsedRecord.SegmentNumber.GetValueOrDefault());

                        break;

                    case "detached":
                        if (detached ||
                            parsedRecord.SegmentTimelineDurationSeconds is { } timelineSeconds &&
                            (!double.IsFinite(timelineSeconds) || timelineSeconds <= 0))
                        {
                            throw new InvalidDataException(
                                "Recorder recovery terminal metadata contains an invalid detached timeline.");
                        }

                        detached = true;
                        detachedSegmentTimelineSeconds =
                            parsedRecord.SegmentTimelineDurationSeconds;
                        break;

                    case "invalidate-live":
                        break;

                    case "discarded":
                        if (!string.IsNullOrWhiteSpace(committingOutputPath) ||
                            !string.IsNullOrWhiteSpace(committedOutputPath))
                        {
                            throw new InvalidDataException(
                                "Recorder recovery terminal metadata contains conflicting terminals.");
                        }

                        discarded = true;
                        break;

                    case "superseded":
                        superseded = true;
                        break;

                    case "committing":
                        var hasNewCommitIdentity =
                            parsedRecord.FinalOutputLength is > 0 &&
                            IsValidOutputFingerprint(
                                parsedRecord.FinalOutputFingerprint);
                        var hasLegacyCommitIdentity =
                            header.Version == LegacySchemaVersion &&
                            parsedRecord.FinalOutputLength is null &&
                            parsedRecord.FinalOutputFingerprint is null;
                        if (string.IsNullOrWhiteSpace(parsedRecord.FinalOutputPath) ||
                            !hasNewCommitIdentity &&
                            !hasLegacyCommitIdentity)
                        {
                            throw new InvalidDataException(
                                "Recorder recovery terminal metadata contains an invalid output plan.");
                        }

                        committingOutputPath = Path.GetFullPath(parsedRecord.FinalOutputPath);
                        committingOutputLength = parsedRecord.FinalOutputLength;
                        committingOutputFingerprint =
                            parsedRecord.FinalOutputFingerprint;
                        break;

                    case "committed":
                        var hasValidCommittedIdentity =
                            parsedRecord.FinalOutputLength is > 0 &&
                            (IsValidOutputFingerprint(
                                 parsedRecord.FinalOutputFingerprint) ||
                             header.Version == LegacySchemaVersion &&
                             parsedRecord.FinalOutputFingerprint is null);
                        if (string.IsNullOrWhiteSpace(parsedRecord.FinalOutputPath) ||
                            !hasValidCommittedIdentity)
                        {
                            throw new InvalidDataException(
                                "Recorder recovery terminal metadata contains an invalid committed output.");
                        }

                        committedOutputPath = Path.GetFullPath(parsedRecord.FinalOutputPath);
                        committedOutputLength = parsedRecord.FinalOutputLength;
                        committedOutputFingerprint =
                            parsedRecord.FinalOutputFingerprint;
                        if (!string.IsNullOrWhiteSpace(committingOutputPath) &&
                            (!string.Equals(
                                 committedOutputPath,
                                 committingOutputPath,
                                 StringComparison.OrdinalIgnoreCase) ||
                             committingOutputLength is not null &&
                             committedOutputLength != committingOutputLength ||
                             committingOutputFingerprint is not null &&
                             !string.Equals(
                                 committedOutputFingerprint,
                                 committingOutputFingerprint,
                                 StringComparison.Ordinal)))
                        {
                            throw new InvalidDataException(
                                "Recorder recovery terminal metadata changed its output plan.");
                        }

                        break;

                    default:
                        throw new InvalidDataException(
                            "Recorder recovery terminal metadata contains an unknown record.");
                }
            }

            if (lineEnd == journalBytes.Length)
            {
                EnsureValidTerminalSegmentTimeline(
                    detached,
                    detachedSegmentTimelineSeconds,
                    selectedSegmentNumbers.Count,
                    header!);
                return new TerminalAppendPlan(
                    journalBytes.Length,
                    NeedsSeparator: true,
                    header!.Version,
                    HasCommitTerminal:
                        !string.IsNullOrWhiteSpace(committingOutputPath) ||
                        !string.IsNullOrWhiteSpace(committedOutputPath),
                    Superseded: superseded);
            }

            lineStart = nextLineStart;
        }

        EnsureValidTerminalSegmentTimeline(
            detached,
            detachedSegmentTimelineSeconds,
            selectedSegmentNumbers.Count,
            header!);

        return new TerminalAppendPlan(
            journalBytes.Length,
            NeedsSeparator: false,
            header!.Version,
            HasCommitTerminal:
                !string.IsNullOrWhiteSpace(committingOutputPath) ||
                !string.IsNullOrWhiteSpace(committedOutputPath),
            Superseded: superseded);
    }

    private static void EnsureValidTerminalSegmentTimeline(
        bool detached,
        double? timelineSeconds,
        int segmentCount,
        JournalRecord header)
    {
        if (timelineSeconds is null)
        {
            return;
        }

        var segmentDurationSeconds = header.SegmentDurationSeconds ??
            FfmpegArgumentBuilder.SegmentSeconds;
        if (!detached ||
            !TryResolveSegmentTimelineDuration(
                timelineSeconds.Value,
                segmentCount,
                segmentDurationSeconds,
                header.FramesPerSecond.GetValueOrDefault(),
                out _))
        {
            throw new InvalidDataException(
                "Recorder recovery terminal metadata contains an invalid detached timeline.");
        }
    }

    private bool TryRecord(JournalRecord record) =>
        IsHealthy &&
        _records.Writer.TryWrite(new JournalEnvelope(record, null));

    private async Task WriteRecordsAsync()
    {
        try
        {
            while (await _records.Reader.WaitToReadAsync(_writerCancellation.Token)
                       .ConfigureAwait(false))
            {
                var checkpoints = new List<TaskCompletionSource<bool>>();
                while (_records.Reader.TryRead(out var envelope))
                {
                    if (envelope.Record is { } record)
                    {
                        await _writer.WriteLineAsync(
                                JsonSerializer.Serialize(record, SerializerOptions))
                            .ConfigureAwait(false);
                    }

                    if (envelope.Checkpoint is { } checkpoint)
                    {
                        checkpoints.Add(checkpoint);
                    }
                }

                await _writer.FlushAsync(_writerCancellation.Token).ConfigureAwait(false);
                if (checkpoints.Count > 0)
                {
                    _stream.Flush(flushToDisk: true);
                }

                foreach (var checkpoint in checkpoints)
                {
                    checkpoint.TrySetResult(true);
                }
            }
        }
        catch (OperationCanceledException) when (_writerCancellation.IsCancellationRequested)
        {
            // Bounded shutdown after a stalled local profile write.
        }
        catch (Exception exception) when (
            IsExpectedWriterFailure(exception))
        {
            RecordWriterFailure(exception);
            _records.Writer.TryComplete(exception);
        }
        finally
        {
            while (_records.Reader.TryRead(out var envelope))
            {
                envelope.Checkpoint?.TrySetResult(false);
            }

            try
            {
                await _writer.FlushAsync().ConfigureAwait(false);
            }
            catch (Exception exception) when (IsExpectedWriterFailure(exception))
            {
                RecordWriterFailure(exception);
            }

            try
            {
                // CloseAsync is the durability boundary for detached recovery
                // and for atomically published locator repairs. Force every
                // queued add/remove/terminal record to stable storage even
                // when no explicit checkpoint envelope preceded shutdown.
                _stream.Flush(flushToDisk: true);
            }
            catch (Exception exception) when (IsExpectedWriterFailure(exception))
            {
                RecordWriterFailure(exception);
            }

            try
            {
                _writer.Dispose();
            }
            catch (Exception exception) when (IsExpectedWriterFailure(exception))
            {
                RecordWriterFailure(exception);
            }

            try
            {
                await _stream.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception) when (IsExpectedWriterFailure(exception))
            {
                RecordWriterFailure(exception);
            }
        }
    }

    private static bool IsExpectedWriterFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or ObjectDisposedException or
            NotSupportedException or SecurityException;

    private void RecordWriterFailure(Exception exception) =>
        Interlocked.CompareExchange(ref _writerFailure, exception, null);

    private static JournalRecord? DeserializeRecord(string? line)
    {
        if (string.IsNullOrWhiteSpace(line) ||
            line.Length > MaximumJournalLineCharacters)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<JournalRecord>(line, SerializerOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool TryValidateSegmentIdentity(
        string sessionDirectory,
        string? candidatePath,
        int? segmentNumber,
        out string normalizedPath)
    {
        normalizedPath = string.Empty;
        if (segmentNumber is not int normalizedSegmentNumber ||
            normalizedSegmentNumber is < 0 or > 999_999_999)
        {
            return false;
        }

        var expectedPath = Path.GetFullPath(Path.Combine(
            sessionDirectory,
            $"segment-{normalizedSegmentNumber:D9}.mkv"));
        if (string.IsNullOrWhiteSpace(candidatePath))
        {
            normalizedPath = expectedPath;
            return true;
        }

        normalizedPath = Path.GetFullPath(candidatePath);
        return string.Equals(
            normalizedPath,
            expectedPath,
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasValidRecordSessionIdentity(
        JournalRecord record,
        string expectedSessionId,
        int schemaVersion) =>
        string.Equals(
            record.SessionId,
            expectedSessionId,
            StringComparison.Ordinal) ||
        schemaVersion >= SchemaVersion &&
        record.SessionId is null &&
        record.Kind is "add" or "remove";

    private static bool HasValidSegmentPathForSchema(
        JournalRecord record,
        int schemaVersion) =>
        schemaVersion >= SchemaVersion ||
        !string.IsNullOrWhiteSpace(record.SegmentPath);

    private static bool TryResolveSegmentTimelineDuration(
        double timelineSeconds,
        int segmentCount,
        int segmentDurationSeconds,
        int framesPerSecond,
        out TimeSpan? timelineDuration)
    {
        timelineDuration = null;
        if (!double.IsFinite(timelineSeconds) ||
            segmentCount <= 0 ||
            segmentDurationSeconds is < 1 or > 300 ||
            framesPerSecond is < 1 or > 240)
        {
            return false;
        }

        var minimumTimelineSeconds =
            checked((long)segmentCount - 1) * segmentDurationSeconds +
            1d / framesPerSecond;
        var maximumTimelineSeconds =
            checked((long)segmentCount) * segmentDurationSeconds;
        if (timelineSeconds < minimumTimelineSeconds ||
            timelineSeconds > maximumTimelineSeconds)
        {
            return false;
        }

        timelineDuration = TimeSpan.FromSeconds(timelineSeconds);
        return true;
    }

    private static bool IsSupportedSchemaVersion(int version) =>
        version is LegacySchemaVersion or SchemaVersion;

    private static bool IsDirectRegularSession(string bufferRoot, string sessionDirectory)
    {
        try
        {
            if (!IsDirectSessionIdentity(bufferRoot, sessionDirectory))
            {
                return false;
            }

            var root = new DirectoryInfo(bufferRoot);
            var session = new DirectoryInfo(sessionDirectory);
            return root.Exists &&
                   session.Exists &&
                   (root.Attributes &
                    (FileAttributes.Directory | FileAttributes.ReparsePoint)) ==
                       FileAttributes.Directory &&
                   (session.Attributes &
                    (FileAttributes.Directory | FileAttributes.ReparsePoint)) ==
                       FileAttributes.Directory;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                ArgumentException or NotSupportedException or SecurityException)
        {
            return false;
        }
    }

    private static bool IsRegularDirectory(string path)
    {
        var attributes = File.GetAttributes(path);
        return (attributes &
                (FileAttributes.Directory | FileAttributes.ReparsePoint)) ==
               FileAttributes.Directory;
    }

    private static bool IsDirectSessionIdentity(string bufferRoot, string sessionDirectory) =>
        string.Equals(
            Path.GetFileName(bufferRoot),
            ".clipforge-recordings",
            StringComparison.OrdinalIgnoreCase) &&
        string.Equals(
            Path.GetDirectoryName(sessionDirectory),
            bufferRoot,
            StringComparison.OrdinalIgnoreCase) &&
        Path.GetFileName(sessionDirectory).StartsWith(
            "session-",
            StringComparison.OrdinalIgnoreCase);

    private static bool TryValidateLiveRecordingPath(
        string sessionDirectory,
        string candidatePath,
        bool requireExistingFile,
        out string? normalizedPath)
    {
        normalizedPath = null;
        try
        {
            var normalizedSession = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(sessionDirectory));
            var candidate = Path.GetFullPath(candidatePath);
            var expected = Path.Combine(
                normalizedSession,
                LiveRecordingRecoveryFileName);
            var legacyExpected = Path.Combine(
                normalizedSession,
                LegacyLiveRecordingRecoveryFileName);
            if (!candidate.Equals(
                    expected,
                    StringComparison.OrdinalIgnoreCase) &&
                !candidate.Equals(
                    legacyExpected,
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (requireExistingFile)
            {
                var info = new FileInfo(candidate);
                if (!info.Exists ||
                    info.Length <= 0 ||
                    (info.Attributes &
                     (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                {
                    return false;
                }
            }

            normalizedPath = candidate;
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                ArgumentException or NotSupportedException or SecurityException)
        {
            return false;
        }
    }

    private bool TryWriteLiveOutputInvalidationMarker() =>
        TryPersistLiveOutputInvalidationMarker(
            SessionDirectory,
            SessionId);

    internal static bool TryPersistLiveOutputInvalidationMarker(
        string sessionDirectory,
        string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId) ||
            sessionId.Length != 32 ||
            !HasMatchingSessionMarker(sessionDirectory, sessionId))
        {
            return false;
        }

        var markerPath = Path.Combine(
            sessionDirectory,
            LiveRecordingInvalidationMarkerFileName);
        try
        {
            using var stream = new FileStream(
                markerPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.WriteThrough);
            var payload = Utf8WithoutBom.GetBytes(sessionId);
            stream.Write(payload);
            stream.Flush(flushToDisk: true);
            return true;
        }
        catch (IOException) when (HasLiveOutputInvalidationMarker(sessionDirectory))
        {
            // A previous invalidation already made the conservative decision
            // durable. Never overwrite/follow an existing filesystem object.
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                ArgumentException or NotSupportedException or SecurityException)
        {
            return false;
        }
    }

    internal static bool HasLiveOutputInvalidationMarker(string sessionDirectory)
    {
        try
        {
            var markerPath = Path.Combine(
                sessionDirectory,
                LiveRecordingInvalidationMarkerFileName);
            return File.Exists(markerPath) || Directory.Exists(markerPath);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                ArgumentException or NotSupportedException or SecurityException)
        {
            // An inaccessible marker is conservatively equivalent to an
            // invalidation: recovery segments remain the authoritative source.
            return true;
        }
    }

    internal static bool TryGetSessionMarkerId(
        string sessionDirectory,
        out string? sessionId)
    {
        sessionId = null;
        try
        {
            var markerPath = Path.Combine(
                sessionDirectory,
                SessionMarkerFileName);
            var marker = new FileInfo(markerPath);
            if (!marker.Exists ||
                marker.Length is <= 0 or > 128 ||
                (marker.Attributes &
                 (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            {
                return false;
            }

            var value = File.ReadAllText(markerPath).Trim();
            if (!Guid.TryParseExact(value, "N", out _))
            {
                return false;
            }

            sessionId = value;
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                ArgumentException or NotSupportedException or SecurityException)
        {
            return false;
        }
    }

    private static bool HasMatchingSessionMarker(
        string sessionDirectory,
        string sessionId) =>
        TryGetSessionMarkerId(sessionDirectory, out var actualSessionId) &&
        string.Equals(actualSessionId, sessionId, StringComparison.Ordinal);

    internal static void TryDeleteOwnedSessionMarker(
        string sessionDirectory,
        string sessionId)
    {
        if (!HasMatchingSessionMarker(sessionDirectory, sessionId))
        {
            return;
        }

        TryDeleteFile(Path.Combine(sessionDirectory, SessionMarkerFileName));
    }

    private static bool IsRegularFileWithExactIdentity(
        string? path,
        long? expectedLength,
        string? expectedFingerprint)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) ||
                expectedLength is null or <= 0 ||
                !IsValidOutputFingerprint(expectedFingerprint))
            {
                return false;
            }

            var info = new FileInfo(Path.GetFullPath(path));
            if (!info.Exists ||
                info.Length != expectedLength.Value ||
                (info.Attributes &
                 (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            {
                return false;
            }

            var actualFingerprint = ComputeOutputFingerprint(info.FullName);
            return CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(actualFingerprint),
                Convert.FromHexString(expectedFingerprint!));
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                ArgumentException or NotSupportedException or SecurityException or
                InvalidDataException or FormatException)
        {
            return false;
        }
    }

    private static bool IsRegularFileWithExactLength(
        string? path,
        long? expectedLength)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) ||
                expectedLength is null or <= 0)
            {
                return false;
            }

            var info = new FileInfo(Path.GetFullPath(path));
            return info.Exists &&
                   info.Length == expectedLength.Value &&
                   (info.Attributes &
                    (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                ArgumentException or NotSupportedException or SecurityException)
        {
            return false;
        }
    }

    private static bool IsValidOutputFingerprint(string? fingerprint)
    {
        if (fingerprint is null || fingerprint.Length != 64)
        {
            return false;
        }

        foreach (var character in fingerprint)
        {
            if (!Uri.IsHexDigit(character))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsSafeCommittedOutputPath(
        string? outputPath,
        string bufferRoot,
        string sessionDirectory)
    {
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            return false;
        }

        try
        {
            var normalizedOutput = Path.GetFullPath(outputPath);
            var outputDirectory = Path.GetDirectoryName(normalizedOutput);
            return string.Equals(
                       Path.GetExtension(normalizedOutput),
                       ".mp4",
                       StringComparison.OrdinalIgnoreCase) &&
                   !string.IsNullOrWhiteSpace(outputDirectory) &&
                   ReplayBufferService.IsSafeBufferRootPath(outputDirectory) &&
                   !IsSameOrDescendantPath(outputDirectory, bufferRoot) &&
                   !IsSameOrDescendantPath(outputDirectory, sessionDirectory);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                ArgumentException or NotSupportedException or SecurityException)
        {
            return false;
        }
    }

    private static bool IsSameOrDescendantPath(
        string candidatePath,
        string ancestorPath)
    {
        var relative = Path.GetRelativePath(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(ancestorPath)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidatePath)));
        return relative.Equals(".", StringComparison.Ordinal) ||
               !relative.Equals("..", StringComparison.Ordinal) &&
               !relative.StartsWith(
                   $"..{Path.DirectorySeparatorChar}",
                   StringComparison.Ordinal) &&
               !Path.IsPathFullyQualified(relative);
    }

    internal static int GetUncleanRecoverySafetyTailSegments(
        int segmentDurationSeconds)
    {
        if (segmentDurationSeconds is < 1 or > 300)
        {
            throw new ArgumentOutOfRangeException(nameof(segmentDurationSeconds));
        }

        return (UncleanRecoveryInvalidationSeconds + segmentDurationSeconds - 1) /
            segmentDurationSeconds + UncleanRecoveryExtraTailSegments;
    }

    private static IReadOnlyList<PhysicalSegment> EnumeratePhysicalFallbackSegments(
        string sessionDirectory,
        int segmentDurationSeconds,
        bool preserveInitialSegment)
    {
        try
        {
            var segments = Directory.EnumerateFiles(
                    sessionDirectory,
                    "segment-*.mkv",
                    SearchOption.TopDirectoryOnly)
                .Take(MaximumJournalRecords)
                .Select(path =>
                {
                    var fileName = Path.GetFileName(path);
                    if (fileName.Length != "segment-000000000.mkv".Length ||
                        !int.TryParse(
                            fileName.AsSpan("segment-".Length, 9),
                            out var segmentNumber) ||
                        !TryValidateSegmentIdentity(
                            sessionDirectory,
                            path,
                            segmentNumber,
                            out var normalizedPath) ||
                        !TryReadRegularSegmentLength(normalizedPath, out var length))
                    {
                        return (PhysicalSegment?)null;
                    }

                    return new PhysicalSegment(normalizedPath, length, segmentNumber);
                })
                .Where(segment => segment is not null)
                .Select(segment => segment!.Value)
                .OrderBy(segment => segment.SegmentNumber)
                .ToList();
            var safetyTailSegments =
                GetUncleanRecoverySafetyTailSegments(segmentDurationSeconds);
            if (segments.Count <= safetyTailSegments)
            {
                return [];
            }

            segments.RemoveRange(
                segments.Count - safetyTailSegments,
                safetyTailSegments);
            if (!preserveInitialSegment &&
                segments.Count > 0 &&
                segments[0].SegmentNumber == 0)
            {
                segments.RemoveAt(0);
            }

            return segments;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                ArgumentException or NotSupportedException or SecurityException)
        {
            return [];
        }
    }

    private static bool TryReadRegularSegmentLength(string path, out long length)
    {
        length = 0;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists ||
                info.Length <= 0 ||
                (info.Attributes &
                 (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            {
                return false;
            }

            length = info.Length;
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                ArgumentException or NotSupportedException or SecurityException)
        {
            return false;
        }
    }

    private static async Task WriteDurableTextAsync(
        string path,
        string content,
        CancellationToken cancellationToken)
    {
        FileStream? stream = null;
        var created = false;
        ExceptionDispatchInfo? failure = null;
        try
        {
            stream = new FileStream(
                path,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough);
            created = true;
            var bytes = Utf8WithoutBom.GetBytes(content);
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }
        catch (Exception exception)
        {
            failure = ExceptionDispatchInfo.Capture(exception);
        }

        if (stream is not null)
        {
            try
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failure ??= ExceptionDispatchInfo.Capture(exception);
            }
        }

        if (failure is not null)
        {
            if (created)
            {
                TryDeleteFile(path);
            }

            failure.Throw();
        }
    }

    private static bool TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            return !File.Exists(path);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                ArgumentException or NotSupportedException or SecurityException)
        {
            // Recovery cleanup is best effort; a later startup can retry.
            return false;
        }
    }

    private sealed record JournalRecord(
        int Version,
        string Kind,
        string? SessionId,
        string? SessionDirectory = null,
        string? BufferRoot = null,
        int? FramesPerSecond = null,
        bool? HasAudio = null,
        int? SegmentDurationSeconds = null,
        string? SegmentPath = null,
        long? SegmentLength = null,
        int? SegmentNumber = null,
        string? LiveRecordingPath = null,
        double? SegmentTimelineDurationSeconds = null,
        string? FinalOutputPath = null,
        long? FinalOutputLength = null,
        string? FinalOutputFingerprint = null);

    private sealed record JournalEnvelope(
        JournalRecord? Record,
        TaskCompletionSource<bool>? Checkpoint);

    private readonly record struct TerminalAppendPlan(
        long Offset,
        bool NeedsSeparator,
        int SchemaVersion,
        bool HasCommitTerminal,
        bool Superseded);

    private readonly record struct JournalSegment(string Path, long Length);

    private readonly record struct PhysicalSegment(
        string Path,
        long Length,
        int SegmentNumber);
}

internal sealed record RecordingRecoveryJournalSnapshot(
    string SessionId,
    string SessionDirectory,
    string BufferRoot,
    IReadOnlyList<string> SegmentPaths,
    long SegmentBytes,
    int FramesPerSecond,
    bool HasAudio,
    bool SourceAvailable,
    bool Detached,
    string JournalPath,
    string? CommittedOutputPath,
    int MissingSegmentCount,
    bool Discarded = false,
    string? LiveRecordingRecoveryPath = null,
    int SegmentDurationSeconds = FfmpegArgumentBuilder.SegmentSeconds,
    bool LiveOutputInvalidated = false,
    bool CommitTerminalRecorded = false,
    bool Superseded = false,
    TimeSpan? SegmentTimelineDuration = null);
