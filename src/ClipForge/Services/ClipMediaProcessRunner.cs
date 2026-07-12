using System.ComponentModel;
using System.Text;

namespace ClipForge.Services;

internal readonly record struct ClipMediaProcessResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    bool TimedOut)
{
    public bool Succeeded => !TimedOut && ExitCode == 0;
}

internal interface IClipMediaProcessRunner
{
    Task<ClipMediaProcessResult> RunAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}

/// <summary>
/// Runs the private FFmpeg tools without a command shell and bounds their runtime and output.
/// </summary>
internal sealed class ClipMediaProcessRunner : IClipMediaProcessRunner
{
    private const int MaximumCapturedCharacters = 64 * 1024;

    public async Task<ClipMediaProcessResult> RunAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        cancellationToken.ThrowIfCancellationRequested();
        using var process = new Process
        {
            StartInfo = CreateStartInfo(executablePath, arguments),
            EnableRaisingEvents = true
        };

        if (!process.Start())
        {
            throw new Win32Exception("The media helper process could not be started.");
        }

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

