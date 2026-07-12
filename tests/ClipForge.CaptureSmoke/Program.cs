using System.Globalization;
using System.Diagnostics;
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

    var display = displays.FirstOrDefault()
        ?? throw new InvalidOperationException("No display was available for the capture smoke test.");
    var includeSystemAudio = args.Contains("--audio", StringComparer.OrdinalIgnoreCase) && outputs.Count > 0;
    var includeMicrophone = args.Contains("--microphone", StringComparer.OrdinalIgnoreCase) && microphones.Count > 0;
    var verifyPruning = args.Contains("--prune", StringComparer.OrdinalIgnoreCase);
    var mode = includeMicrophone
        ? includeSystemAudio ? "mixed-audio" : "microphone"
        : includeSystemAudio ? "audio" : "video";
    var clipDirectory = Path.Combine(artifactRoot, $"clips-{mode}");
    var bufferDirectory = Path.Combine(artifactRoot, $"buffer-{mode}");
    Directory.CreateDirectory(clipDirectory);

    var configuration = new CaptureConfiguration(
        display,
        ResolutionOption.All.Single(option => option.Id == "720p"),
        30,
        TimeSpan.FromSeconds(verifyPruning ? 6 : 30),
        includeSystemAudio,
        includeSystemAudio ? outputs.FirstOrDefault(device => device.IsDefault) ?? outputs[0] : null,
        includeMicrophone,
        includeMicrophone ? microphones.FirstOrDefault(device => device.IsDefault) ?? microphones[0] : null,
        clipDirectory);

    await using var replay = new ReplayBufferService(setup, bufferDirectory);
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
        await ReportPerformanceSampleAsync(replay, timeout.Token);
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

    Console.WriteLine($"PASS: {clipPath}");
    Console.WriteLine(
        $"Duration: {duration:0.###} seconds; size: {new FileInfo(clipPath).Length:N0} bytes; " +
        $"system audio: {includeSystemAudio}; microphone: {includeMicrophone}");
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

static async Task ReportPerformanceSampleAsync(
    ReplayBufferService replay,
    CancellationToken cancellationToken)
{
    if (replay.CaptureProcessId is not { } processId)
    {
        Console.WriteLine("Capture performance: process metrics unavailable");
        return;
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
        Console.WriteLine(
            $"Capture performance (5s diagnostic): CPU {normalizedCpuPercent:0.0}% normalized, " +
            $"working set {process.WorkingSet64 / (1024d * 1024d):0.0} MB, " +
            $"priority {process.PriorityClass}, encoder {replay.ActiveEncoderDescription ?? "unknown"}");
    }
    catch (Exception exception) when (
        exception is InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception)
    {
        Console.WriteLine($"Capture performance: process metrics unavailable ({exception.Message})");
    }
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
