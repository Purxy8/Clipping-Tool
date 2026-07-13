using System.Globalization;
using System.Diagnostics;
using System.Text.Json;
using ClipForge.Capture;
using ClipForge.Models;
using ClipForge.Services;

try
{
    var artifactRoot = GetOption(args, "--artifacts")
        ?? Path.Combine(Environment.CurrentDirectory, "artifacts", "capture-smoke");
    artifactRoot = Path.GetFullPath(artifactRoot);
    Directory.CreateDirectory(artifactRoot);

    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
    var setup = new FfmpegSetupService(Path.Combine(artifactRoot, "ffmpeg"));
    var ffmpeg = setup.FindExecutable();
    if (ffmpeg is null)
    {
        var lastPercent = -1;
        var progress = new Progress<double>(value =>
        {
            var percent = (int)Math.Round(value * 100);
            if (percent >= lastPercent + 10 || percent == 100)
            {
                lastPercent = percent;
                Console.WriteLine($"FFmpeg setup: {percent}%");
            }
        });
        ffmpeg = await setup.DownloadAsync(progress, timeout.Token);
    }

    Console.WriteLine($"FFmpeg: {ffmpeg}");
    if (args.Contains("--install-only", StringComparer.OrdinalIgnoreCase))
    {
        return 0;
    }

    var discovery = new DeviceDiscoveryService();
    var displays = discovery.GetDisplays();
    var outputs = discovery.GetOutputDevices();
    var microphones = discovery.GetMicrophones();
    Console.WriteLine($"Devices: {displays.Count} display(s), {outputs.Count} output(s), {microphones.Count} microphone(s)");

    var display = displays.FirstOrDefault(candidate => candidate.IsPrimary) ?? displays.FirstOrDefault()
        ?? throw new InvalidOperationException("No display was available for the capture smoke test.");
    var includeSystemAudio = args.Contains("--audio", StringComparer.OrdinalIgnoreCase);
    var includeMicrophone = args.Contains("--microphone", StringComparer.OrdinalIgnoreCase);
    if (includeSystemAudio && outputs.Count == 0)
    {
        throw new InvalidOperationException(
            "System audio was requested, but no active Windows output device was available.");
    }

    if (includeMicrophone && microphones.Count == 0)
    {
        throw new InvalidOperationException(
            "Microphone audio was requested, but no active Windows capture device was available.");
    }
    var verifyPruning = args.Contains("--prune", StringComparer.OrdinalIgnoreCase);
    var forceGdi = args.Contains("--force-gdi", StringComparer.OrdinalIgnoreCase);
    var resolutionId = GetOption(args, "--resolution") ?? "720p";
    var resolution = ResolutionOption.All.FirstOrDefault(option =>
        option.Id.Equals(resolutionId, StringComparison.OrdinalIgnoreCase))
        ?? throw new ArgumentException($"Unsupported smoke-test resolution '{resolutionId}'.");
    var framesPerSecond = int.TryParse(
        GetOption(args, "--fps"),
        NumberStyles.None,
        CultureInfo.InvariantCulture,
        out var requestedFramesPerSecond)
        ? requestedFramesPerSecond
        : 30;
    if (framesPerSecond is not (30 or 60))
    {
        throw new ArgumentOutOfRangeException(nameof(framesPerSecond), "Smoke-test FPS must be 30 or 60.");
    }

    var mode = includeMicrophone
        ? includeSystemAudio ? "mixed-audio" : "microphone"
        : includeSystemAudio ? "audio" : "video";
    var backendLabel = forceGdi ? "-forced-gdi" : string.Empty;
    var runLabel = $"{mode}-{resolution.Id}-{framesPerSecond}fps{backendLabel}";
    var clipDirectory = Path.Combine(artifactRoot, $"clips-{runLabel}");
    var bufferDirectory = Path.Combine(artifactRoot, $"buffer-{runLabel}");
    Directory.CreateDirectory(clipDirectory);

    var configuration = new CaptureConfiguration(
        display,
        resolution,
        framesPerSecond,
        TimeSpan.FromSeconds(verifyPruning ? 6 : 30),
        includeSystemAudio,
        includeSystemAudio ? outputs.FirstOrDefault(device => device.IsDefault) ?? outputs[0] : null,
        includeMicrophone,
        includeMicrophone ? microphones.FirstOrDefault(device => device.IsDefault) ?? microphones[0] : null,
        clipDirectory);

    VideoEncodingStrategy? captureStrategyOverride = null;
    if (forceGdi)
    {
        var verifiedSelection = await new FfmpegCapabilityProbe()
            .SelectAsync(ffmpeg, configuration, timeout.Token);
        captureStrategyOverride = verifiedSelection.Strategy with
        {
            CaptureBackend = DesktopCaptureBackend.Gdi,
            RequiresSystemMemoryTransfer = false
        };
        Console.WriteLine($"Forcing fallback smoke path: {captureStrategyOverride.Description}");
    }

    await using var replay = captureStrategyOverride is null
        ? new ReplayBufferService(setup, bufferDirectory)
        : new ReplayBufferService(setup, bufferDirectory, captureStrategyOverride);
    var buffered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    replay.StateChanged += (_, state) =>
    {
        Console.WriteLine($"Replay: {state.State}, available {state.AvailableDuration.TotalSeconds:0}s");
        if (state.State == ReplayState.Faulted)
        {
            buffered.TrySetException(new InvalidOperationException(state.Message ?? "Capture faulted."));
        }
        else if (state.AvailableDuration >= TimeSpan.FromSeconds(6))
        {
            buffered.TrySetResult();
        }
    };

    string clipPath;
    try
    {
        await replay.StartAsync(configuration, timeout.Token);
        Console.WriteLine($"Capture strategy: {replay.ActiveEncoderDescription ?? "unknown"}");
        var performance = await ReadPerformanceSampleAsync(replay, timeout.Token)
            ?? throw new InvalidDataException("Capture process metrics were unavailable during the smoke test.");
        if (performance.Priority != ProcessPriorityClass.BelowNormal)
        {
            throw new InvalidDataException(
                $"Capture priority was {performance.Priority}; expected BelowNormal.");
        }

        if (performance.NormalizedCpuPercent > 75)
        {
            throw new InvalidDataException(
                $"Capture used {performance.NormalizedCpuPercent:0.0}% normalized CPU; expected at most 75%.");
        }

        if (performance.WorkingSetBytes > 2L * 1024 * 1024 * 1024)
        {
            throw new InvalidDataException(
                $"Capture working set was {performance.WorkingSetBytes / (1024d * 1024d):0.0} MB; expected at most 2048 MB.");
        }

        Console.WriteLine(
            $"Capture performance (5s diagnostic): CPU {performance.NormalizedCpuPercent:0.0}% normalized, " +
            $"working set {performance.WorkingSetBytes / (1024d * 1024d):0.0} MB, " +
            $"priority {performance.Priority}, encoder {replay.ActiveEncoderDescription ?? "unknown"}");
        await buffered.Task.WaitAsync(TimeSpan.FromSeconds(30), timeout.Token);
        if (verifyPruning)
        {
            await Task.Delay(TimeSpan.FromSeconds(8), timeout.Token);
            var retainedFiles = Directory
                .EnumerateFiles(bufferDirectory, "segment-*.mkv", SearchOption.AllDirectories)
                .Select(path => new FileInfo(path))
                .OrderBy(file => file.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var retainedSegments = retainedFiles.Length;
            foreach (var file in retainedFiles)
            {
                Console.WriteLine($"Retained: {file.Name}, {file.Length:N0} bytes, {file.LastWriteTimeUtc:O}");
            }

            if (retainedSegments > 5)
            {
                throw new InvalidDataException(
                    $"Replay pruning retained {retainedSegments} segments; expected at most 5.");
            }

            Console.WriteLine($"Pruning: {retainedSegments} segment(s) retained after rollover");
        }

        clipPath = await replay.SaveClipAsync(TimeSpan.FromSeconds(6), clipDirectory, timeout.Token);
    }
    finally
    {
        if (replay.IsRunning)
        {
            await replay.StopAsync();
        }
    }

    var ffprobe = Path.Combine(Path.GetDirectoryName(ffmpeg)!, "ffprobe.exe");
    var duration = await ReadDurationAsync(ffprobe, clipPath, timeout.Token);
    if (duration is < 5 or > 8)
    {
        throw new InvalidDataException($"Saved clip duration was {duration:0.###} seconds; expected about 6 seconds.");
    }

    var mediaInfo = await ReadMediaInfoAsync(ffprobe, clipPath, timeout.Token);
    var expectedWidth = resolution.Width ?? display.Width - display.Width % 2;
    var expectedHeight = resolution.Height ?? display.Height - display.Height % 2;
    if (mediaInfo.Width != expectedWidth || mediaInfo.Height != expectedHeight)
    {
        throw new InvalidDataException(
            $"Saved video was {mediaInfo.Width}x{mediaInfo.Height}; expected {expectedWidth}x{expectedHeight}.");
    }

    if (mediaInfo.AverageFrameRate < framesPerSecond * 0.85 ||
        mediaInfo.AverageFrameRate > framesPerSecond * 1.15)
    {
        throw new InvalidDataException(
            $"Saved video averaged {mediaInfo.AverageFrameRate:0.###} FPS; expected about {framesPerSecond} FPS.");
    }

    if (mediaInfo.FrameCount < Math.Floor(duration * framesPerSecond * 0.85))
    {
        throw new InvalidDataException(
            $"Saved video contained {mediaInfo.FrameCount} frames over {duration:0.###} seconds; frame pacing was below the smoke-test floor.");
    }

    var expectedAudioStreams = includeSystemAudio || includeMicrophone ? 1 : 0;
    if (mediaInfo.AudioStreamCount != expectedAudioStreams)
    {
        throw new InvalidDataException(
            $"Saved clip contained {mediaInfo.AudioStreamCount} audio stream(s); expected {expectedAudioStreams}.");
    }

    Console.WriteLine($"PASS: {clipPath}");
    Console.WriteLine(
        $"Duration: {duration:0.###} seconds; size: {new FileInfo(clipPath).Length:N0} bytes; " +
        $"video: {mediaInfo.Width}x{mediaInfo.Height} at {mediaInfo.AverageFrameRate:0.###} FPS, " +
        $"frames: {mediaInfo.FrameCount.ToString(CultureInfo.InvariantCulture)}; " +
        $"audio streams: {mediaInfo.AudioStreamCount}; " +
        $"system audio requested: {includeSystemAudio}; microphone requested: {includeMicrophone}");
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"FAIL: {exception.GetBaseException().Message}");
    return 1;
}

static string? GetOption(string[] arguments, string name)
{
    var index = Array.FindIndex(arguments, argument =>
        string.Equals(argument, name, StringComparison.OrdinalIgnoreCase));
    return index >= 0 && index + 1 < arguments.Length ? arguments[index + 1] : null;
}

static async Task<CapturePerformanceSample?> ReadPerformanceSampleAsync(
    ReplayBufferService replay,
    CancellationToken cancellationToken)
{
    if (replay.CaptureProcessId is not { } processId)
    {
        return null;
    }

    try
    {
        using var process = Process.GetProcessById(processId);
        var initialCpu = process.TotalProcessorTime;
        var startedAt = Stopwatch.GetTimestamp();
        await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
        process.Refresh();

        var wallSeconds = Stopwatch.GetElapsedTime(startedAt).TotalSeconds;
        var cpuSeconds = (process.TotalProcessorTime - initialCpu).TotalSeconds;
        var normalizedCpuPercent = cpuSeconds / wallSeconds /
                                   Math.Max(1, Environment.ProcessorCount) * 100;
        return new CapturePerformanceSample(
            normalizedCpuPercent,
            process.WorkingSet64,
            process.PriorityClass);
    }
    catch (Exception exception) when (
        exception is InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception)
    {
        Console.WriteLine($"Capture performance: process metrics unavailable ({exception.Message})");
        return null;
    }
}

static async Task<MediaInfo> ReadMediaInfoAsync(
    string ffprobe,
    string mediaPath,
    CancellationToken cancellationToken)
{
    var startInfo = new ProcessStartInfo
    {
        FileName = ffprobe,
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true
    };
    foreach (var argument in new[]
             {
                 "-v", "error",
                 "-f", "mov",
                 "-protocol_whitelist", "file",
                 "-count_frames",
                 "-show_entries", "stream=codec_type,width,height,avg_frame_rate,nb_read_frames",
                 "-of", "json",
                 mediaPath
             })
    {
        startInfo.ArgumentList.Add(argument);
    }

    using var process = Process.Start(startInfo)
        ?? throw new InvalidOperationException("Could not start ffprobe for stream validation.");
    var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
    var error = await process.StandardError.ReadToEndAsync(cancellationToken);
    await process.WaitForExitAsync(cancellationToken);
    if (process.ExitCode != 0)
    {
        throw new InvalidDataException($"ffprobe stream validation failed: {error.Trim()}");
    }

    using var document = JsonDocument.Parse(output);
    var streams = document.RootElement.GetProperty("streams");
    JsonElement? videoStream = null;
    var audioStreamCount = 0;
    foreach (var stream in streams.EnumerateArray())
    {
        var codecType = stream.GetProperty("codec_type").GetString();
        if (string.Equals(codecType, "video", StringComparison.Ordinal) && videoStream is null)
        {
            videoStream = stream;
        }
        else if (string.Equals(codecType, "audio", StringComparison.Ordinal))
        {
            audioStreamCount++;
        }
    }

    if (videoStream is not { } video)
    {
        throw new InvalidDataException("Saved clip did not contain a video stream.");
    }

    var averageFrameRate = ParseFraction(video.GetProperty("avg_frame_rate").GetString());
    if (!video.TryGetProperty("nb_read_frames", out var frameCountElement) ||
        !long.TryParse(
            frameCountElement.GetString(),
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out var frameCount))
    {
        throw new InvalidDataException("ffprobe did not return a valid decoded frame count.");
    }

    return new MediaInfo(
        video.GetProperty("width").GetInt32(),
        video.GetProperty("height").GetInt32(),
        averageFrameRate,
        frameCount,
        audioStreamCount);
}

static double ParseFraction(string? value)
{
    var parts = value?.Split('/', StringSplitOptions.TrimEntries);
    if (parts is not { Length: 2 } ||
        !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var numerator) ||
        !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var denominator) ||
        denominator == 0)
    {
        throw new InvalidDataException($"ffprobe returned an invalid frame rate '{value}'.");
    }

    return numerator / denominator;
}

static async Task<double> ReadDurationAsync(
    string ffprobe,
    string mediaPath,
    CancellationToken cancellationToken)
{
    var startInfo = new ProcessStartInfo
    {
        FileName = ffprobe,
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true
    };
    foreach (var argument in new[]
             {
                 "-v", "error",
                 "-show_entries", "format=duration",
                 "-of", "default=noprint_wrappers=1:nokey=1",
                 mediaPath
             })
    {
        startInfo.ArgumentList.Add(argument);
    }

    using var process = Process.Start(startInfo)
        ?? throw new InvalidOperationException("Could not start ffprobe.");
    var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
    var error = await process.StandardError.ReadToEndAsync(cancellationToken);
    await process.WaitForExitAsync(cancellationToken);
    if (process.ExitCode != 0)
    {
        throw new InvalidDataException($"ffprobe failed: {error.Trim()}");
    }

    return double.Parse(output.Trim(), CultureInfo.InvariantCulture);
}

internal sealed record CapturePerformanceSample(
    double NormalizedCpuPercent,
    long WorkingSetBytes,
    ProcessPriorityClass Priority);

internal sealed record MediaInfo(
    int Width,
    int Height,
    double AverageFrameRate,
    long FrameCount,
    int AudioStreamCount);
