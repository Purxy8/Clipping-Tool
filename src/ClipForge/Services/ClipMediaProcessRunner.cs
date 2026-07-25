using System.ComponentModel;
using System.Text;
using ClipForge.Capture;

namespace ClipForge.Services;

internal readonly record struct ClipMediaProcessResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    bool TimedOut)
{
    public bool Succeeded => !TimedOut && ExitCode == 0;
}

internal enum ClipMediaProcessPriority
{
    Background,
    Interactive
}

internal interface IClipMediaProcessRunner
{
    Task<ClipMediaProcessResult> RunAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        ClipMediaProcessPriority priority = ClipMediaProcessPriority.Background);
}

/// <summary>
/// Runs the private FFmpeg tools without a command shell and bounds their runtime and output.
/// </summary>
internal sealed class ClipMediaProcessRunner : IClipMediaProcessRunner
{
    private const int MaximumCapturedCharacters = 64 * 1024;
    // Each owning service keeps its own serialized lane. A static application-
    // wide gate made a foreground trim wait behind an unrelated thumbnail or
    // metadata probe, sometimes for the full helper timeout.
    private readonly SemaphoreSlim _processGate = new(1, 1);

    public async Task<ClipMediaProcessResult> RunAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        ClipMediaProcessPriority priority = ClipMediaProcessPriority.Background)
    {
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        cancellationToken.ThrowIfCancellationRequested();
        await _processGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await RunCoreAsync(
                    executablePath,
                    arguments,
                    timeout,
                    cancellationToken,
                    priority)
                .ConfigureAwait(false);
        }
        finally
        {
            _processGate.Release();
        }
    }

    private static async Task<ClipMediaProcessResult> RunCoreAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        ClipMediaProcessPriority priority)
    {
        using var process = new Process
        {
            StartInfo = CreateStartInfo(executablePath, arguments),
            EnableRaisingEvents = true
        };

        if (!process.Start())
        {
            throw new Win32Exception("The media helper process could not be started.");
        }

        using var processJob = AttachProcessLifetime(process);
        // Background discovery/thumbnail work stays at Idle. A user-requested
        // trim runs BelowNormal so it finishes promptly without outranking the
        // foreground game or live capture.
        _ = priority == ClipMediaProcessPriority.Interactive
            ? ProcessTuning.TryApplyLowImpactPriority(process)
            : ProcessTuning.TryApplyAuxiliaryMediaPriority(process);

        var outputTask = ReadBoundedAsync(process.StandardOutput);
        var errorTask = ReadBoundedAsync(process.StandardError);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);

        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            timedOut = true;
            TryKill(process);
            await WaitForTerminationAsync(process).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            await WaitForTerminationAsync(process).ConfigureAwait(false);
            throw;
        }

        var standardOutput = await ReadCompletedOutputAsync(outputTask).ConfigureAwait(false);
        var standardError = await ReadCompletedOutputAsync(errorTask).ConfigureAwait(false);
        return new ClipMediaProcessResult(
            process.HasExited ? process.ExitCode : -1,
            standardOutput,
            standardError,
            timedOut);
    }

    private static CaptureProcessJob? AttachProcessLifetime(Process process)
    {
        try
        {
            return CaptureProcessJob.Attach(process);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or Win32Exception)
        {
            var processHasExited = false;
            try
            {
                processHasExited = process.HasExited;
            }
            catch (Exception statusException) when (
                statusException is InvalidOperationException or Win32Exception)
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
            TryKill(process);
            throw;
        }
    }

    internal static ProcessStartInfo CreateStartInfo(
        string executablePath,
        IReadOnlyList<string> arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentNullException.ThrowIfNull(arguments);

        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        foreach (var argument in arguments)
        {
            ArgumentNullException.ThrowIfNull(argument);
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader)
    {
        var result = new StringBuilder(capacity: 4096);
        var buffer = new char[2048];

        while (true)
        {
            var read = await reader.ReadAsync(buffer).ConfigureAwait(false);
            if (read == 0)
            {
                return result.ToString();
            }

            var remaining = MaximumCapturedCharacters - result.Length;
            if (remaining > 0)
            {
                result.Append(buffer, 0, Math.Min(read, remaining));
            }
        }
    }

    private static async Task<string> ReadCompletedOutputAsync(Task<string> readTask)
    {
        try
        {
            return await readTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return string.Empty;
        }
        catch (IOException)
        {
            return string.Empty;
        }
        catch (ObjectDisposedException)
        {
            return string.Empty;
        }
    }

    private static async Task WaitForTerminationAsync(Process process)
    {
        try
        {
            await process.WaitForExitAsync()
                .WaitAsync(TimeSpan.FromSeconds(2))
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is TimeoutException or InvalidOperationException)
        {
            // Disposing the Process below closes all redirected handles even if termination was delayed.
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
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // The process may have exited between the state check and Kill.
        }
    }
}
