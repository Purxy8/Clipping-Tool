using System.Text.Json;
using System.Threading.Channels;

namespace ClipForge.Capture;

/// <summary>
/// Keeps a small local lifecycle journal for diagnosing failures that appear only
/// after many hours. It contains process/resource counters and capture metadata,
/// never pixels, audio, file names, device names, or user input.
/// </summary>
internal sealed class CaptureRuntimeJournal : IAsyncDisposable
{
    private const long MaximumJournalBytes = 1024 * 1024;
    private const int MaximumDetailCharacters = 1000;
    private const int QueueCapacity = 64;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly Channel<CaptureRuntimeEvent> _events;
    private readonly CancellationTokenSource _writerCancellation = new();
    private readonly Task _writerTask;
    private readonly string _journalPath;
    private readonly string _previousJournalPath;
    private int _disposed;

    public CaptureRuntimeJournal(string? diagnosticRoot = null)
    {
        var root = Path.GetFullPath(diagnosticRoot ?? GetDefaultDiagnosticRoot());
        _journalPath = Path.Combine(root, "capture-lifecycle.jsonl");
        _previousJournalPath = Path.Combine(root, "capture-lifecycle.previous.jsonl");
        _events = Channel.CreateBounded<CaptureRuntimeEvent>(
            new BoundedChannelOptions(QueueCapacity)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.DropOldest
            });
        _writerTask = Task.Run(WriteEventsAsync);
    }

    internal string JournalPath => _journalPath;

    public void Record(
        string eventName,
        Process? captureProcess = null,
        string? backend = null,
        int? width = null,
        int? height = null,
        int? framesPerSecond = null,
        bool? captureCursor = null,
        string? detail = null)
    {
        if (Volatile.Read(ref _disposed) != 0 || string.IsNullOrWhiteSpace(eventName))
        {
            return;
        }

        var safeDetail = string.IsNullOrWhiteSpace(detail)
            ? null
            : detail.Trim();
        if (safeDetail?.Length > MaximumDetailCharacters)
        {
            safeDetail = safeDetail[^MaximumDetailCharacters..];
        }

        using var host = Process.GetCurrentProcess();
        var entry = new CaptureRuntimeEvent(
            Schema: 2,
            TimestampUtc: DateTimeOffset.UtcNow,
            Event: eventName.Trim(),
            Backend: string.IsNullOrWhiteSpace(backend) ? null : backend.Trim(),
            Width: width,
            Height: height,
            FramesPerSecond: framesPerSecond,
            CaptureCursor: captureCursor,
            Detail: safeDetail,
            Host: TrySnapshot(host),
            Capture: TrySnapshot(captureProcess),
            DesktopWindowManager: TrySnapshotSystemProcess("dwm", host.SessionId),
            AudioEngine: TrySnapshotSystemProcess("audiodg", host.SessionId));
        _events.Writer.TryWrite(entry);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _events.Writer.TryComplete();
        try
        {
            await _writerTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _writerCancellation.Cancel();
        }
        catch (OperationCanceledException)
        {
            // A bounded shutdown may cancel a slow local filesystem operation.
        }
        catch (IOException)
        {
            // Diagnostics must never hold capture or shutdown hostage.
        }
        catch (UnauthorizedAccessException)
        {
            // A locked-down profile may deny the optional local journal.
        }
        catch (Exception)
        {
            // Optional diagnostics must never make application shutdown fail.
        }
        finally
        {
            _writerCancellation.Cancel();
            _writerCancellation.Dispose();
        }
    }

    private async Task WriteEventsAsync()
    {
        try
        {
            await foreach (var entry in _events.Reader.ReadAllAsync(_writerCancellation.Token)
                               .ConfigureAwait(false))
            {
                var line = JsonSerializer.Serialize(entry, SerializerOptions) + Environment.NewLine;
                var bytesRequired = System.Text.Encoding.UTF8.GetByteCount(line);
                Directory.CreateDirectory(Path.GetDirectoryName(_journalPath)!);
                RotateIfRequired(bytesRequired);
                await File.AppendAllTextAsync(
                        _journalPath,
                        line,
                        System.Text.Encoding.UTF8,
                        _writerCancellation.Token)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_writerCancellation.IsCancellationRequested)
        {
            // Bounded app shutdown.
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or NotSupportedException or
                ArgumentException or System.Security.SecurityException)
        {
            // The journal is optional observability; capture remains local and functional.
        }
    }

    private void RotateIfRequired(int bytesRequired)
    {
        long existingBytes;
        try
        {
            existingBytes = File.Exists(_journalPath)
                ? new FileInfo(_journalPath).Length
                : 0;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return;
        }

        if (existingBytes <= 0 || existingBytes + bytesRequired <= MaximumJournalBytes)
        {
            return;
        }

        File.Move(_journalPath, _previousJournalPath, overwrite: true);
    }

    private static CaptureProcessSnapshot? TrySnapshotSystemProcess(
        string processName,
        int preferredSessionId)
    {
        Process[] processes;
        try
        {
            processes = Process.GetProcessesByName(processName);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or NotSupportedException)
        {
            return null;
        }

        try
        {
            var selected = processes.FirstOrDefault(process => TryGetSessionId(process) == preferredSessionId)
                           ?? processes.FirstOrDefault();
            return TrySnapshot(selected);
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }

    private static int? TryGetSessionId(Process process)
    {
        try
        {
            return process.SessionId;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or NotSupportedException)
        {
            return null;
        }
    }

    private static CaptureProcessSnapshot? TrySnapshot(Process? process)
    {
        if (process is null)
        {
            return null;
        }

        var processId = TryRead(() => process.Id);
        if (processId is null)
        {
            return null;
        }

        try
        {
            process.Refresh();
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or NotSupportedException or
                System.ComponentModel.Win32Exception)
        {
            // Protected system processes can still expose a useful subset of
            // counters, so probe every field independently below.
        }

        var cpuPriority = TryRead(() => process.PriorityClass)?.ToString();
        var graphicsPriority = ProcessTuning.TryReadGraphicsPriority(
            process,
            out var observedGraphicsPriority)
            ? observedGraphicsPriority.ToString()
            : null;

        return new CaptureProcessSnapshot(
            processId.Value,
            TryRead(() => process.HandleCount),
            TryRead(() => process.Threads.Count),
            TryRead(() => process.PrivateMemorySize64),
            TryRead(() => process.WorkingSet64),
            TryRead(() => process.TotalProcessorTime.TotalMilliseconds),
            cpuPriority,
            graphicsPriority);
    }

    private static T? TryRead<T>(Func<T> read) where T : struct
    {
        try
        {
            return read();
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or NotSupportedException or
                System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private static string GetDefaultDiagnosticRoot()
    {
        var localApplicationData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localApplicationData, "ClipForge", "Diagnostics");
    }

    private sealed record CaptureRuntimeEvent(
        int Schema,
        DateTimeOffset TimestampUtc,
        string Event,
        string? Backend,
        int? Width,
        int? Height,
        int? FramesPerSecond,
        bool? CaptureCursor,
        string? Detail,
        CaptureProcessSnapshot? Host,
        CaptureProcessSnapshot? Capture,
        CaptureProcessSnapshot? DesktopWindowManager,
        CaptureProcessSnapshot? AudioEngine);

    private sealed record CaptureProcessSnapshot(
        int ProcessId,
        int? Handles,
        int? Threads,
        long? PrivateMemoryBytes,
        long? WorkingSetBytes,
        double? TotalProcessorTimeMilliseconds,
        string? CpuPriority,
        string? GraphicsPriority);
}
