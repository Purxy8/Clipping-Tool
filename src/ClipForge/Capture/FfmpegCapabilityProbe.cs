using System.ComponentModel;
using ClipForge.Models;

namespace ClipForge.Capture;

internal sealed record FfmpegProbeExecution(bool Succeeded, string? Diagnostic = null);

internal interface IFfmpegProbeRunner
{
    Task<FfmpegProbeExecution> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken);
}

internal sealed record FfmpegCapabilitySelection(
    VideoEncodingStrategy Strategy,
    string Diagnostics);

/// <summary>
/// Verifies that an encoder can create frames on the current machine instead
/// of trusting FFmpeg's compiled-in encoder list. This catches missing drivers,
/// disabled adapters, unsupported resolutions, and unavailable media runtimes.
/// </summary>
internal sealed class FfmpegCapabilityProbe
{
    private static readonly VideoEncoderKind[] HardwarePreference =
    [
        VideoEncoderKind.NvidiaNvenc,
        VideoEncoderKind.IntelQuickSync,
        VideoEncoderKind.AmdAmf
    ];

    private readonly IFfmpegProbeRunner _runner;
    private readonly SemaphoreSlim _probeGate = new(1, 1);
    private readonly Dictionary<string, FfmpegCapabilitySelection> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    public FfmpegCapabilityProbe(IFfmpegProbeRunner? runner = null)
    {
        _runner = runner ?? new FfmpegProbeRunner();
    }

    public async Task<FfmpegCapabilitySelection> SelectAsync(
        string ffmpegPath,
        CaptureConfiguration configuration,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ffmpegPath);
        ArgumentNullException.ThrowIfNull(configuration);

        var cacheKey = BuildCacheKey(ffmpegPath, configuration);
        await _probeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cache.TryGetValue(cacheKey, out var cached))
            {
                return cached;
            }

            var selection = await ProbeCoreAsync(ffmpegPath, configuration, cancellationToken)
                .ConfigureAwait(false);
            _cache[cacheKey] = selection;
            return selection;
        }
        finally
        {
            _probeGate.Release();
        }
    }

    internal static VideoEncoderKind SelectBestEncoder(
        bool nvencAvailable,
        bool quickSyncAvailable,
        bool amfAvailable)
    {
        if (nvencAvailable)
        {
            return VideoEncoderKind.NvidiaNvenc;
        }

        if (quickSyncAvailable)
        {
            return VideoEncoderKind.IntelQuickSync;
        }

        return amfAvailable
            ? VideoEncoderKind.AmdAmf
            : VideoEncoderKind.SoftwareX264;
    }

    private async Task<FfmpegCapabilitySelection> ProbeCoreAsync(
        string ffmpegPath,
        CaptureConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var diagnostics = new List<string>();
        VideoEncodingStrategy? firstHardwareGdiFallback = null;
        var outputSize = CaptureGeometry.ResolveOutputSize(
            configuration.Display,
            configuration.Resolution);
        var targetWidth = outputSize.Width;
        var targetHeight = outputSize.Height;
        var graphicsPath = DescribeGraphicsPath(outputSize);

        foreach (var encoder in HardwarePreference)
        {
            var gdiStrategy = new VideoEncodingStrategy(encoder, DesktopCaptureBackend.Gdi);
            var encoderProbe = await RunSafelyAsync(
                    ffmpegPath,
                    FfmpegArgumentBuilder.BuildEncoderProbeArguments(
                        gdiStrategy,
                        targetWidth,
                        targetHeight,
                        configuration.FramesPerSecond),
                    cancellationToken)
                .ConfigureAwait(false);

            if (!encoderProbe.Succeeded)
            {
                diagnostics.Add($"{gdiStrategy.EncoderName}: {Summarize(encoderProbe.Diagnostic)}");
                continue;
            }

            // A working encoder does not imply that its preferred graphics
            // device can consume frames from the selected monitor. Retain the
            // first verified GDI fallback, but try every available hardware
            // encoder's WGC paths before accepting the CPU capture path.
            firstHardwareGdiFallback ??= gdiStrategy;

            var graphicsStrategy = gdiStrategy with
            {
                CaptureBackend = DesktopCaptureBackend.WindowsGraphicsCapture
            };
            var graphicsProbe = await RunSafelyAsync(
                    ffmpegPath,
                    FfmpegArgumentBuilder.BuildGraphicsCaptureProbeArguments(
                        configuration,
                        graphicsStrategy),
                    cancellationToken)
                .ConfigureAwait(false);

            if (graphicsProbe.Succeeded)
            {
                diagnostics.Add(
                    $"Selected {graphicsStrategy.Description} with {graphicsPath} after runtime verification.");
                return new FfmpegCapabilitySelection(
                    graphicsStrategy,
                    string.Join(' ', diagnostics));
            }

            diagnostics.Add(
                $"Direct Windows Graphics Capture with {graphicsPath} unavailable: " +
                Summarize(graphicsProbe.Diagnostic));

            var transferStrategy = graphicsStrategy with
            {
                RequiresSystemMemoryTransfer = true
            };
            var transferProbe = await RunSafelyAsync(
                    ffmpegPath,
                    FfmpegArgumentBuilder.BuildGraphicsCaptureProbeArguments(
                        configuration,
                        transferStrategy),
                    cancellationToken)
                .ConfigureAwait(false);

            if (transferProbe.Succeeded)
            {
                diagnostics.Add(
                    $"Selected {transferStrategy.Description} with {graphicsPath} after runtime verification.");
                return new FfmpegCapabilitySelection(
                    transferStrategy,
                    string.Join(' ', diagnostics));
            }

            diagnostics.Add(
                $"Windows Graphics Capture compatibility transfer with {graphicsPath} unavailable: " +
                Summarize(transferProbe.Diagnostic));
        }

        if (firstHardwareGdiFallback is not null)
        {
            diagnostics.Add(
                $"Selected {firstHardwareGdiFallback.Description} after testing all hardware graphics-capture paths.");
            return new FfmpegCapabilitySelection(
                firstHardwareGdiFallback,
                string.Join(' ', diagnostics));
        }

        var softwareGraphics = new VideoEncodingStrategy(
            VideoEncoderKind.SoftwareX264,
            DesktopCaptureBackend.WindowsGraphicsCapture,
            RequiresSystemMemoryTransfer: true);
        var softwareGraphicsProbe = await RunSafelyAsync(
                ffmpegPath,
                FfmpegArgumentBuilder.BuildGraphicsCaptureProbeArguments(
                    configuration,
                    softwareGraphics),
                cancellationToken)
            .ConfigureAwait(false);

        if (softwareGraphicsProbe.Succeeded)
        {
            diagnostics.Add($"Selected {softwareGraphics.Description} with {graphicsPath}.");
            return new FfmpegCapabilitySelection(
                softwareGraphics,
                string.Join(' ', diagnostics));
        }

        diagnostics.Add(
            $"Windows Graphics Capture with {graphicsPath} unavailable: " +
            Summarize(softwareGraphicsProbe.Diagnostic));
        diagnostics.Add($"Selected {VideoEncodingStrategy.SoftwareGdi.Description} as the safe fallback.");
        return new FfmpegCapabilitySelection(
            VideoEncodingStrategy.SoftwareGdi,
            string.Join(' ', diagnostics));
    }

    private async Task<FfmpegProbeExecution> RunSafelyAsync(
        string ffmpegPath,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _runner.RunAsync(ffmpegPath, arguments, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or InvalidOperationException or Win32Exception)
        {
            return new FfmpegProbeExecution(false, exception.Message);
        }
    }

    private static string BuildCacheKey(
        string ffmpegPath,
        CaptureConfiguration configuration)
    {
        long writeTicks;
        try
        {
            writeTicks = File.GetLastWriteTimeUtc(ffmpegPath).Ticks;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            writeTicks = 0;
        }

        return string.Join("|",
            Path.GetFullPath(ffmpegPath),
            writeTicks,
            configuration.Display.MonitorIndex,
            configuration.Display.Width,
            configuration.Display.Height,
            configuration.Resolution.Width,
            configuration.Resolution.Height,
            configuration.FramesPerSecond);
    }

    private static string Summarize(string? diagnostic)
    {
        if (string.IsNullOrWhiteSpace(diagnostic))
        {
            return "runtime probe failed";
        }

        var summary = diagnostic
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();
        return summary.Length <= 240 ? summary : summary[^240..];
    }

    private static string DescribeGraphicsPath(CaptureOutputSize outputSize) =>
        outputSize.RequiresScaling
            ? $"low-overhead point scaling to {outputSize.Width}x{outputSize.Height}"
            : $"native {outputSize.Width}x{outputSize.Height} surfaces";
}

internal sealed class FfmpegProbeRunner : IFfmpegProbeRunner
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(6);
    private const int MaximumDiagnosticLines = 12;
    private const int MaximumDiagnosticCharactersPerLine = 512;

    public async Task<FfmpegProbeExecution> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            return new FfmpegProbeExecution(false, "Windows could not start FFmpeg.");
        }

        _ = ProcessTuning.TryApplyLowImpactPriority(process);
        var diagnosticsTask = ReadDiagnosticTailAsync(process.StandardError);
        using var timeout = new CancellationTokenSource(ProbeTimeout);
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
            try
            {
                await process.WaitForExitAsync(CancellationToken.None)
                    .WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                _ = diagnosticsTask.ContinueWith(
                    task => _ = task.Exception,
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted,
                    TaskScheduler.Default);
                return new FfmpegProbeExecution(
                    false,
                    "runtime probe timed out and could not be terminated promptly");
            }

            var timedOutDiagnostics = await diagnosticsTask.ConfigureAwait(false);
            return new FfmpegProbeExecution(
                false,
                string.IsNullOrWhiteSpace(timedOutDiagnostics)
                    ? "runtime probe timed out"
                    : timedOutDiagnostics);
        }
        catch
        {
            TryKill(process);
            throw;
        }

        var diagnostics = await diagnosticsTask.ConfigureAwait(false);
        return new FfmpegProbeExecution(process.ExitCode == 0, diagnostics);
    }

    private static async Task<string> ReadDiagnosticTailAsync(StreamReader reader)
    {
        var lines = new Queue<string>();
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var trimmed = line.Trim();
            if (trimmed.Length > MaximumDiagnosticCharactersPerLine)
            {
                trimmed = trimmed[^MaximumDiagnosticCharactersPerLine..];
            }

            lines.Enqueue(trimmed);
            while (lines.Count > MaximumDiagnosticLines)
            {
                _ = lines.Dequeue();
            }
        }

        return string.Join(" | ", lines);
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
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            // Best effort; the probe may have exited between the checks.
        }
    }
}
