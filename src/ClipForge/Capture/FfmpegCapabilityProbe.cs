using System.ComponentModel;
using ClipForge.Models;
using ClipForge.Services;

namespace ClipForge.Capture;

internal sealed record FfmpegProbeExecution(bool Succeeded, string? Diagnostic = null);

internal readonly record struct FfmpegProbeCadenceObservation(
    int FirstFrame,
    TimeSpan FirstFrameElapsed,
    int LastFrame,
    TimeSpan LastFrameElapsed);

internal interface IFfmpegProbeRunner
{
    Task<FfmpegProbeExecution> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken);
}

internal sealed record FfmpegCapabilitySelection(
    VideoEncodingStrategy Strategy,
    string Diagnostics)
{
    internal DateTimeOffset? CacheExpiresAtUtc { get; init; }
}

/// <summary>
/// Verifies that an encoder can create frames on the current machine instead
/// of trusting FFmpeg's compiled-in encoder list. This catches missing drivers,
/// disabled adapters, unsupported resolutions, and unavailable media runtimes.
/// </summary>
internal sealed class FfmpegCapabilityProbe
{
    private static readonly TimeSpan DefaultDegradedCacheInitialDuration =
        TimeSpan.FromSeconds(15);
    private static readonly TimeSpan DefaultDegradedCacheMaximumDuration =
        TimeSpan.FromMinutes(1);
    private static readonly TimeSpan DefaultPositiveCacheDuration =
        TimeSpan.FromMinutes(10);
    private static readonly VideoEncoderKind[] HardwarePreference =
    [
        VideoEncoderKind.NvidiaNvenc,
        VideoEncoderKind.IntelQuickSync,
        VideoEncoderKind.AmdAmf
    ];

    private readonly IFfmpegProbeRunner _runner;
    private readonly TimeSpan _degradedCacheInitialDuration;
    private readonly TimeSpan _degradedCacheMaximumDuration;
    private readonly TimeSpan _positiveCacheDuration;
    private readonly Func<DateTimeOffset> _getUtcNow;
    private readonly SemaphoreSlim _probeGate = new(1, 1);
    private readonly object _cacheGate = new();
    private readonly Dictionary<string, CachedCapabilitySelection> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    public FfmpegCapabilityProbe(
        IFfmpegProbeRunner? runner = null,
        TimeSpan? degradedCacheInitialDuration = null,
        TimeSpan? degradedCacheMaximumDuration = null,
        TimeSpan? positiveCacheDuration = null,
        Func<DateTimeOffset>? getUtcNow = null)
    {
        _runner = runner ?? new FfmpegProbeRunner();
        _degradedCacheInitialDuration =
            degradedCacheInitialDuration ?? DefaultDegradedCacheInitialDuration;
        _degradedCacheMaximumDuration =
            degradedCacheMaximumDuration ?? DefaultDegradedCacheMaximumDuration;
        _positiveCacheDuration =
            positiveCacheDuration ?? DefaultPositiveCacheDuration;
        _getUtcNow = getUtcNow ?? (static () => DateTimeOffset.UtcNow);

        if (_degradedCacheInitialDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(degradedCacheInitialDuration),
                "The degraded capability cache duration must be positive.");
        }

        if (_degradedCacheMaximumDuration < _degradedCacheInitialDuration)
        {
            throw new ArgumentOutOfRangeException(
                nameof(degradedCacheMaximumDuration),
                "The degraded capability cache maximum must not be shorter than its initial duration.");
        }

        if (_positiveCacheDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(positiveCacheDuration),
                "The positive capability cache duration must be positive.");
        }
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
            CachedCapabilitySelection? previous;
            lock (_cacheGate)
            {
                _cache.TryGetValue(cacheKey, out previous);
            }

            if (previous is not null &&
                (previous.ExpiresAtUtc is null ||
                 _getUtcNow() < previous.ExpiresAtUtc))
            {
                return previous.Selection;
            }

            var selection = await ProbeCoreAsync(ffmpegPath, configuration, cancellationToken)
                .ConfigureAwait(false);
            // A GDI result can be caused by a transient cadence miss while a
            // fullscreen game, DWM, or another capture probe is briefly busy.
            // A short, bounded backoff prevents rapid replay restarts from
            // repeating several expensive probes on machines where WGC is
            // permanently unavailable, while still retrying transient failures.
            if (selection.Strategy.CaptureBackend ==
                DesktopCaptureBackend.WindowsGraphicsCapture)
            {
                var expiresAtUtc = _getUtcNow() + _positiveCacheDuration;
                selection = selection with { CacheExpiresAtUtc = expiresAtUtc };
                lock (_cacheGate)
                {
                    _cache[cacheKey] = new CachedCapabilitySelection(
                        selection,
                        expiresAtUtc,
                        ConsecutiveDegradedSelections: 0);
                }
            }
            else
            {
                var consecutiveDegradedSelections = previous is null
                    ? 1
                    : previous.ConsecutiveDegradedSelections == int.MaxValue
                        ? int.MaxValue
                        : previous.ConsecutiveDegradedSelections + 1;
                var cacheDuration = GetDegradedCacheDuration(
                    consecutiveDegradedSelections);
                var expiresAtUtc = _getUtcNow() + cacheDuration;
                selection = selection with { CacheExpiresAtUtc = expiresAtUtc };
                lock (_cacheGate)
                {
                    _cache[cacheKey] = new CachedCapabilitySelection(
                        selection,
                        expiresAtUtc,
                        consecutiveDegradedSelections);
                }
            }

            return selection;
        }
        finally
        {
            _probeGate.Release();
        }
    }

    internal bool Invalidate(
        string ffmpegPath,
        CaptureConfiguration configuration,
        VideoEncodingStrategy expectedStrategy)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ffmpegPath);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(expectedStrategy);

        var cacheKey = BuildCacheKey(ffmpegPath, configuration);
        lock (_cacheGate)
        {
            if (!_cache.TryGetValue(cacheKey, out var cached) ||
                cached.Selection.Strategy != expectedStrategy)
            {
                return false;
            }

            return _cache.Remove(cacheKey);
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

    private TimeSpan GetDegradedCacheDuration(
        int consecutiveDegradedSelections)
    {
        var durationTicks = _degradedCacheInitialDuration.Ticks;
        for (var selection = 1;
             selection < consecutiveDegradedSelections &&
             durationTicks < _degradedCacheMaximumDuration.Ticks;
             selection++)
        {
            if (durationTicks >= _degradedCacheMaximumDuration.Ticks / 2)
            {
                return _degradedCacheMaximumDuration;
            }

            durationTicks *= 2;
        }

        return TimeSpan.FromTicks(Math.Min(
            durationTicks,
            _degradedCacheMaximumDuration.Ticks));
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

    private sealed record CachedCapabilitySelection(
        FfmpegCapabilitySelection Selection,
        DateTimeOffset? ExpiresAtUtc,
        int ConsecutiveDegradedSelections);
}

internal sealed class FfmpegProbeRunner : IFfmpegProbeRunner
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);
    private const int MaximumDiagnosticLines = 12;
    private const int MaximumDiagnosticCharactersPerLine = 512;
    private readonly Action<int>? _processOwnershipEstablished;

    internal FfmpegProbeRunner(Action<int>? processOwnershipEstablished = null)
    {
        _processOwnershipEstablished = processOwnershipEstablished;
    }

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
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        var startedAt = Stopwatch.GetTimestamp();
        if (!process.Start())
        {
            return new FfmpegProbeExecution(false, "Windows could not start FFmpeg.");
        }

        using var processJob = AttachProcessLifetime(process);
        _processOwnershipEstablished?.Invoke(process.Id);
        _ = FindScaledGraphicsCaptureFilter(arguments) is not null
            ? ProcessTuning.TryApplyScaledGraphicsProbePriority(process)
            : ProcessTuning.TryApplyLowImpactPriority(process);
        var diagnosticsTask = ReadDiagnosticTailAsync(process.StandardError);
        var progressTask = ReadProgressObservationAsync(
            process.StandardOutput,
            startedAt);
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
                _ = progressTask.ContinueWith(
                    task => _ = task.Exception,
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted,
                    TaskScheduler.Default);
                return new FfmpegProbeExecution(
                    false,
                    "runtime probe timed out and could not be terminated promptly");
            }

            var timedOutDiagnostics = await diagnosticsTask.ConfigureAwait(false);
            _ = await progressTask.ConfigureAwait(false);
            return new FfmpegProbeExecution(
                false,
                string.IsNullOrWhiteSpace(timedOutDiagnostics)
                    ? "runtime probe timed out"
                    : timedOutDiagnostics);
        }
        catch
        {
            TryKill(process);
            _ = diagnosticsTask.ContinueWith(
                task => _ = task.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
            _ = progressTask.ContinueWith(
                task => _ = task.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
            throw;
        }

        var diagnostics = await diagnosticsTask.ConfigureAwait(false);
        var progress = await progressTask.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            return new FfmpegProbeExecution(false, diagnostics);
        }

        return IsProbeCadenceAcceptable(arguments, progress, out var cadenceDiagnostic)
            ? new FfmpegProbeExecution(true, diagnostics)
            : new FfmpegProbeExecution(false, cadenceDiagnostic);
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

    internal static bool IsProbeCadenceAcceptable(
        IReadOnlyList<string> arguments,
        FfmpegProbeCadenceObservation? observation,
        out string diagnostic)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        diagnostic = string.Empty;
        var graphicsFilter = FindScaledGraphicsCaptureFilter(arguments);
        if (graphicsFilter is null)
        {
            return true;
        }

        if (!TryReadOptionInteger(arguments, "-frames:v", out var requestedFrames) ||
            !TryReadFilterInteger(graphicsFilter, "max_framerate=", out var requestedFramesPerSecond) ||
            requestedFrames <= 0 ||
            requestedFramesPerSecond <= 0)
        {
            diagnostic = "scaled graphics probe omitted its frame-count or frame-rate contract";
            return false;
        }

        if (observation is not { } sample ||
            sample.FirstFrame <= 0 ||
            sample.LastFrame < requestedFrames ||
            sample.LastFrame <= sample.FirstFrame ||
            sample.LastFrameElapsed <= sample.FirstFrameElapsed)
        {
            diagnostic =
                "scaled graphics probe did not report enough frame-progress samples to verify sustained cadence";
            return false;
        }

        var observedFrameDelta = sample.LastFrame - sample.FirstFrame;
        var minimumFrameDelta = Math.Min(
            requestedFrames - 1,
            Math.Max(2, requestedFramesPerSecond));
        if (observedFrameDelta < minimumFrameDelta)
        {
            diagnostic =
                "scaled graphics probe reported too short a frame-progress interval to verify sustained cadence";
            return false;
        }

        var observedDuration = sample.LastFrameElapsed - sample.FirstFrameElapsed;
        var observedFramesPerSecond =
            observedFrameDelta / observedDuration.TotalSeconds;
        var minimumFramesPerSecond = requestedFramesPerSecond * 0.85;
        if (observedFramesPerSecond >= minimumFramesPerSecond)
        {
            return true;
        }

        diagnostic = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"scaled graphics probe sustained {observedFramesPerSecond:0.##} FPS; " +
            $"the required minimum is {minimumFramesPerSecond:0.##} FPS");
        return false;
    }

    private static string? FindScaledGraphicsCaptureFilter(
        IReadOnlyList<string> arguments) =>
        arguments.FirstOrDefault(argument =>
            argument.Contains("gfxcapture=", StringComparison.Ordinal) &&
            argument.Contains(":resize_mode=scale", StringComparison.Ordinal));

    private static async Task<FfmpegProbeCadenceObservation?> ReadProgressObservationAsync(
        StreamReader reader,
        long processStartedAt)
    {
        int? firstFrame = null;
        var firstFrameElapsed = TimeSpan.Zero;
        var lastFrame = 0;
        var lastFrameElapsed = TimeSpan.Zero;

        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            const string framePrefix = "frame=";
            if (!line.StartsWith(framePrefix, StringComparison.Ordinal) ||
                !int.TryParse(
                    line.AsSpan(framePrefix.Length).Trim(),
                    System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var frame) ||
                frame <= 0 ||
                frame <= lastFrame)
            {
                continue;
            }

            var elapsed = Stopwatch.GetElapsedTime(processStartedAt);
            if (firstFrame is null)
            {
                firstFrame = frame;
                firstFrameElapsed = elapsed;
            }

            lastFrame = frame;
            lastFrameElapsed = elapsed;
        }

        return firstFrame is { } first
            ? new FfmpegProbeCadenceObservation(
                first,
                firstFrameElapsed,
                lastFrame,
                lastFrameElapsed)
            : null;
    }

    private static bool TryReadOptionInteger(
        IReadOnlyList<string> arguments,
        string option,
        out int value)
    {
        value = 0;
        for (var index = 0; index < arguments.Count - 1; index++)
        {
            if (arguments[index].Equals(option, StringComparison.Ordinal) &&
                int.TryParse(
                    arguments[index + 1],
                    System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out value))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryReadFilterInteger(
        string filter,
        string marker,
        out int value)
    {
        value = 0;
        var start = filter.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
        {
            return false;
        }

        start += marker.Length;
        var end = filter.IndexOf(':', start);
        var text = end < 0
            ? filter.AsSpan(start)
            : filter.AsSpan(start, end - start);
        return int.TryParse(
            text,
            System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture,
            out value);
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
