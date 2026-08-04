using System.Security;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

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
    private const int SchemaVersion = 1;
    private const int MaximumJournalCandidates = 256;
    private const int MaximumJournalRecords = 1_000_000;
    private const int MaximumJournalLineCharacters = 4096;
    private const long MaximumJournalBytes = 64L * 1024 * 1024;
    private const int UncleanRecoverySafetyTailSegments = 15;

    private static readonly UTF8Encoding Utf8WithoutBom = new(
        encoderShouldEmitUTF8Identifier: false);
    private static readonly UTF8Encoding StrictUtf8WithoutBom = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
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
    private Exception? _writerFailure;
    private int _closed;
    private int _disposeStarted;

    private RecordingRecoveryJournal(
        string journalPath,
        string sessionId,
        string sessionDirectory,
        FileStream stream,
        StreamWriter writer)
    {
        JournalPath = journalPath;
        SessionId = sessionId;
        SessionDirectory = sessionDirectory;
        _stream = stream;
        _writer = writer;
        _writerTask = Task.Run(WriteRecordsAsync);
    }

    internal string JournalPath { get; }

    internal string SessionId { get; }

    internal string SessionDirectory { get; }

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

    internal static async Task<RecordingRecoveryJournal> CreateAsync(
        string recoveryRoot,
        string sessionDirectory,
        string bufferRoot,
        int framesPerSecond,
        bool hasAudio,
        CancellationToken cancellationToken)
    {
        if (framesPerSecond is < 1 or > 240)
        {
            throw new ArgumentOutOfRangeException(nameof(framesPerSecond));
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

        var sessionId = Guid.NewGuid().ToString("N");
        var markerPath = Path.Combine(
            normalizedSessionDirectory,
            SessionMarkerFileName);
        var journalPath = GetJournalPath(
            normalizedRecoveryRoot,
            normalizedSessionDirectory);
        FileStream? stream = null;
        StreamWriter? writer = null;
        try
        {
            await WriteDurableTextAsync(
                    markerPath,
                    sessionId,
                    cancellationToken)
                .ConfigureAwait(false);

            stream = new FileStream(
                journalPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
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
                HasAudio: hasAudio);
            await writer.WriteLineAsync(
                    JsonSerializer.Serialize(header, SerializerOptions)
                        .AsMemory(),
                    cancellationToken)
                .ConfigureAwait(false);
            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);

            return new RecordingRecoveryJournal(
                journalPath,
                sessionId,
                normalizedSessionDirectory,
                stream,
                writer);
        }
        catch
        {
            writer?.Dispose();
            stream?.Dispose();
            TryDeleteFile(journalPath);
            TryDeleteFile(markerPath);
            throw;
        }
    }

    internal bool RecordCompleted(string path, long length, int segmentNumber) =>
        TryRecord(new JournalRecord(
            Version: SchemaVersion,
            Kind: "add",
            SessionId: SessionId,
            SegmentPath: path,
            SegmentLength: length,
            SegmentNumber: segmentNumber));

    internal bool RecordUntrusted(string path, int segmentNumber) =>
        TryRecord(new JournalRecord(
            Version: SchemaVersion,
            Kind: "remove",
            SessionId: SessionId,
            SegmentPath: path,
            SegmentNumber: segmentNumber));

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

    internal async Task<bool> CloseAsync(bool detached)
    {
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
                    SessionId: SessionId),
                null));
        }

        _records.Writer.TryComplete();
        return await WaitForWriterCompletionAsync(cancelOnTimeout: true)
            .ConfigureAwait(false);
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

    internal static IReadOnlyList<string> EnumerateCandidatePaths(string recoveryRoot)
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
                .Take(MaximumJournalCandidates)
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
                header.Version != SchemaVersion ||
                !string.Equals(header.Kind, "session", StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(header.SessionId) ||
                header.SessionId.Length != 32 ||
                string.IsNullOrWhiteSpace(header.SessionDirectory) ||
                string.IsNullOrWhiteSpace(header.BufferRoot) ||
                header.FramesPerSecond is < 1 or > 240 ||
                header.HasAudio is null)
            {
                return null;
            }

            var sessionDirectory = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(header.SessionDirectory));
            var bufferRoot = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(header.BufferRoot));
            if (!IsDirectSessionIdentity(bufferRoot, sessionDirectory) ||
                !string.Equals(
                    GetJournalPath(normalizedRoot, sessionDirectory),
                    normalizedJournalPath,
                    StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var selected = new SortedDictionary<int, JournalSegment>();
            var detached = false;
            var discarded = false;
            string? committingOutputPath = null;
            string? committedOutputPath = null;
            long? committedOutputLength = null;
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

                if (record.Version != SchemaVersion ||
                    !string.Equals(
                        record.SessionId,
                        header.SessionId,
                        StringComparison.Ordinal))
                {
                    return null;
                }

                if (discarded &&
                    !string.Equals(record.Kind, "discarded", StringComparison.Ordinal))
                {
                    return null;
                }

                switch (record.Kind)
                {
                    case "add":
                        sawSegmentRecord = true;
                        if (!TryValidateSegmentIdentity(
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
                        if (!TryValidateSegmentIdentity(
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
                        detached = true;
                        break;

                    case "discarded":
                        if (!string.IsNullOrWhiteSpace(committingOutputPath) ||
                            !string.IsNullOrWhiteSpace(committedOutputPath))
                        {
                            return null;
                        }

                        discarded = true;
                        break;

                    case "committing":
                        if (string.IsNullOrWhiteSpace(record.FinalOutputPath))
                        {
                            return null;
                        }

                        committingOutputPath = Path.GetFullPath(record.FinalOutputPath);
                        break;

                    case "committed":
                        if (string.IsNullOrWhiteSpace(record.FinalOutputPath) ||
                            record.FinalOutputLength is null or <= 0)
                        {
                            return null;
                        }

                        committedOutputPath = Path.GetFullPath(record.FinalOutputPath);
                        committedOutputLength = record.FinalOutputLength;
                        if (!string.IsNullOrWhiteSpace(committingOutputPath) &&
                            !string.Equals(
                                committedOutputPath,
                                committingOutputPath,
                                StringComparison.OrdinalIgnoreCase))
                        {
                            return null;
                        }

                        break;

                    default:
                        return null;
                }
            }

            if (!detached)
            {
                // Exclusions are append-only too. If ClipForge was killed during
                // a fault renewal, conservatively drop more than the complete
                // invalidation window so a torn final remove can never be used.
                foreach (var segmentNumber in selected.Keys
                             .TakeLast(UncleanRecoverySafetyTailSegments)
                             .ToArray())
                {
                    _ = selected.Remove(segmentNumber);
                }
            }

            var sourceIdentityVerified =
                IsDirectRegularSession(bufferRoot, sessionDirectory) &&
                HasMatchingSessionMarker(sessionDirectory, header.SessionId);
            var committedOutputExists = IsRegularFileWithExactLength(
                committedOutputPath,
                committedOutputLength);
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
                    Discarded: true);
            }

            if (committedOutputExists)
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
                    CommittedOutputPath: committedOutputPath,
                    MissingSegmentCount: 0);
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
                    MissingSegmentCount: 0);
            }

            if (!detached && !sawSegmentRecord)
            {
                foreach (var physicalSegment in EnumeratePhysicalFallbackSegments(
                             sessionDirectory))
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
                MissingSegmentCount: missingSegmentCount);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException or
                ArgumentException or NotSupportedException or SecurityException)
        {
            return null;
        }
    }

    internal static Task MarkCommittingAsync(
        string recoveryRoot,
        string journalPath,
        string sessionId,
        string finalOutputPath,
        CancellationToken cancellationToken)
        => AppendTerminalRecordAsync(
            recoveryRoot,
            journalPath,
            new JournalRecord(
                Version: SchemaVersion,
                Kind: "committing",
                SessionId: sessionId,
                FinalOutputPath: Path.GetFullPath(finalOutputPath)),
            cancellationToken);

    internal static Task MarkCommittedAsync(
        string recoveryRoot,
        string journalPath,
        string sessionId,
        string finalOutputPath,
        long finalOutputLength,
        CancellationToken cancellationToken)
    {
        if (finalOutputLength <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(finalOutputLength));
        }

        return AppendTerminalRecordAsync(
            recoveryRoot,
            journalPath,
            new JournalRecord(
                Version: SchemaVersion,
                Kind: "committed",
                SessionId: sessionId,
                FinalOutputPath: Path.GetFullPath(finalOutputPath),
                FinalOutputLength: finalOutputLength),
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

    internal static void TryDeleteJournal(string? journalPath)
    {
        if (string.IsNullOrWhiteSpace(journalPath))
        {
            return;
        }

        TryDeleteFile(journalPath);
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
        var discarded = false;
        string? committingOutputPath = null;
        string? committedOutputPath = null;
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

                return new TerminalAppendPlan(lineStart, NeedsSeparator: false);
            }

            if (recordCount == 1)
            {
                header = parsedRecord;
                if (header.Version != SchemaVersion ||
                    !string.Equals(header.Kind, "session", StringComparison.Ordinal) ||
                    !string.Equals(
                        header.SessionId,
                        expectedSessionId,
                        StringComparison.Ordinal) ||
                    string.IsNullOrWhiteSpace(header.SessionDirectory) ||
                    string.IsNullOrWhiteSpace(header.BufferRoot) ||
                    header.FramesPerSecond is < 1 or > 240 ||
                    header.HasAudio is null)
                {
                    throw new InvalidDataException(
                        "Recorder recovery terminal metadata changed identity.");
                }

                sessionDirectory = Path.TrimEndingDirectorySeparator(
                    Path.GetFullPath(header.SessionDirectory));
                var bufferRoot = Path.TrimEndingDirectorySeparator(
                    Path.GetFullPath(header.BufferRoot));
                if (!IsDirectSessionIdentity(bufferRoot, sessionDirectory) ||
                    !string.Equals(
                        GetJournalPath(recoveryRoot, sessionDirectory),
                        journalPath,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        "Recorder recovery terminal metadata changed storage identity.");
                }
            }
            else
            {
                if (header is null || sessionDirectory is null ||
                    parsedRecord.Version != SchemaVersion ||
                    !string.Equals(
                        parsedRecord.SessionId,
                        expectedSessionId,
                        StringComparison.Ordinal) ||
                    (discarded &&
                     !string.Equals(
                         parsedRecord.Kind,
                         "discarded",
                         StringComparison.Ordinal)))
                {
                    throw new InvalidDataException(
                        "Recorder recovery terminal metadata contains an invalid record identity.");
                }

                switch (parsedRecord.Kind)
                {
                    case "add":
                        if (!TryValidateSegmentIdentity(
                                sessionDirectory,
                                parsedRecord.SegmentPath,
                                parsedRecord.SegmentNumber,
                                out _) ||
                            parsedRecord.SegmentLength is null or <= 0)
                        {
                            throw new InvalidDataException(
                                "Recorder recovery terminal metadata contains an invalid segment.");
                        }

                        break;

                    case "remove":
                        if (!TryValidateSegmentIdentity(
                                sessionDirectory,
                                parsedRecord.SegmentPath,
                                parsedRecord.SegmentNumber,
                                out _))
                        {
                            throw new InvalidDataException(
                                "Recorder recovery terminal metadata contains an invalid segment exclusion.");
                        }

                        break;

                    case "detached":
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

                    case "committing":
                        if (string.IsNullOrWhiteSpace(parsedRecord.FinalOutputPath))
                        {
                            throw new InvalidDataException(
                                "Recorder recovery terminal metadata contains an invalid output plan.");
                        }

                        committingOutputPath = Path.GetFullPath(parsedRecord.FinalOutputPath);
                        break;

                    case "committed":
                        if (string.IsNullOrWhiteSpace(parsedRecord.FinalOutputPath) ||
                            parsedRecord.FinalOutputLength is null or <= 0)
                        {
                            throw new InvalidDataException(
                                "Recorder recovery terminal metadata contains an invalid committed output.");
                        }

                        committedOutputPath = Path.GetFullPath(parsedRecord.FinalOutputPath);
                        if (!string.IsNullOrWhiteSpace(committingOutputPath) &&
                            !string.Equals(
                                committedOutputPath,
                                committingOutputPath,
                                StringComparison.OrdinalIgnoreCase))
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
                return new TerminalAppendPlan(journalBytes.Length, NeedsSeparator: true);
            }

            lineStart = nextLineStart;
        }

        return new TerminalAppendPlan(journalBytes.Length, NeedsSeparator: false);
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
        if (string.IsNullOrWhiteSpace(candidatePath) ||
            segmentNumber is not int normalizedSegmentNumber ||
            normalizedSegmentNumber is < 0 or > 999_999_999)
        {
            return false;
        }

        normalizedPath = Path.GetFullPath(candidatePath);
        var expectedPath = Path.GetFullPath(Path.Combine(
            sessionDirectory,
            $"segment-{normalizedSegmentNumber:D9}.mkv"));
        return string.Equals(
            normalizedPath,
            expectedPath,
            StringComparison.OrdinalIgnoreCase);
    }

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

    private static bool HasMatchingSessionMarker(
        string sessionDirectory,
        string sessionId)
    {
        try
        {
            var markerPath = Path.Combine(
                sessionDirectory,
                SessionMarkerFileName);
            var marker = new FileInfo(markerPath);
            return marker.Exists &&
                   marker.Length is > 0 and <= 128 &&
                   (marker.Attributes &
                    (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0 &&
                   string.Equals(
                       File.ReadAllText(markerPath).Trim(),
                       sessionId,
                       StringComparison.Ordinal);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                ArgumentException or NotSupportedException or SecurityException)
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
            if (string.IsNullOrWhiteSpace(path) || expectedLength is null or <= 0)
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

    private static IReadOnlyList<PhysicalSegment> EnumeratePhysicalFallbackSegments(
        string sessionDirectory)
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
            if (segments.Count <= UncleanRecoverySafetyTailSegments)
            {
                return [];
            }

            segments.RemoveRange(
                segments.Count - UncleanRecoverySafetyTailSegments,
                UncleanRecoverySafetyTailSegments);
            if (segments.Count > 0 && segments[0].SegmentNumber == 0)
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
        await using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        var bytes = Utf8WithoutBom.GetBytes(content);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                ArgumentException or NotSupportedException or SecurityException)
        {
            // Recovery cleanup is best effort; a later startup can retry.
        }
    }

    private sealed record JournalRecord(
        int Version,
        string Kind,
        string SessionId,
        string? SessionDirectory = null,
        string? BufferRoot = null,
        int? FramesPerSecond = null,
        bool? HasAudio = null,
        string? SegmentPath = null,
        long? SegmentLength = null,
        int? SegmentNumber = null,
        string? FinalOutputPath = null,
        long? FinalOutputLength = null);

    private sealed record JournalEnvelope(
        JournalRecord? Record,
        TaskCompletionSource<bool>? Checkpoint);

    private readonly record struct TerminalAppendPlan(long Offset, bool NeedsSeparator);

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
    bool Discarded = false);
