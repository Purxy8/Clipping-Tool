using System.Globalization;
using System.Diagnostics;
using System.Security.Cryptography;
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

    if (args.Contains("--resolution-matrix", StringComparer.OrdinalIgnoreCase))
    {
        await RunResolutionMatrixAsync(
            setup,
            ffmpeg,
            artifactRoot,
            args.Contains("--matrix-exhaustive", StringComparer.OrdinalIgnoreCase),
            GetOption(args, "--matrix-fps"),
            timeout.Token);
        return 0;
    }

    if (args.Contains("--wgc-matrix", StringComparer.OrdinalIgnoreCase))
    {
        await RunInteractiveCaptureMatrixAsync(args, artifactRoot, timeout.Token);
        return 0;
    }

    if (args.Contains("--replay-trim-smoke", StringComparer.OrdinalIgnoreCase))
    {
        await RunReplayConcurrentTrimSmokeAsync(
            setup,
            ffmpeg,
            artifactRoot,
            args,
            timeout.Token);
        return 0;
    }

    if (args.Contains("--trim-smoke", StringComparer.OrdinalIgnoreCase))
    {
        var includeTrimAudio = args.Contains("--audio", StringComparer.OrdinalIgnoreCase);
        var trimExecutionMode = args.Contains("--replay-coexisting", StringComparer.OrdinalIgnoreCase)
            ? ClipTrimExecutionMode.ReplayCoexisting
            : ClipTrimExecutionMode.Standard;
        await RunTrimSmokeAsync(
            setup,
            ffmpeg,
            artifactRoot,
            includeTrimAudio,
            GetOption(args, "--trim-source"),
            trimExecutionMode,
            timeout.Token);
        return 0;
    }

    if (args.Contains("--concat-smoke", StringComparer.OrdinalIgnoreCase))
    {
        await RunConcatTimingSmokeAsync(
            setup,
            ffmpeg,
            artifactRoot,
            timeout.Token);
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
    var verifyRenewal = args.Contains("--renew", StringComparer.OrdinalIgnoreCase);
    var verifyServiceOwnedRenewal = args.Contains(
        "--scheduled-renew",
        StringComparer.OrdinalIgnoreCase);
    var renewalCount = verifyRenewal
        ? ParseRenewalCount(GetOption(args, "--renew-count"))
        : 0;
    var forceGdi = args.Contains("--force-gdi", StringComparer.OrdinalIgnoreCase);
    if (verifyRenewal && forceGdi)
    {
        throw new ArgumentException("--renew requires the Windows Graphics Capture path.");
    }
    if (verifyServiceOwnedRenewal && !verifyRenewal)
    {
        throw new ArgumentException("--scheduled-renew also requires --renew.");
    }
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
    var renewalReportPath = Path.Combine(artifactRoot, $"renewal-{runLabel}.json");
    var renewalReports = new List<RenewalSmokeReport>();
    Directory.CreateDirectory(clipDirectory);

    var configuration = new CaptureConfiguration(
        display,
        resolution,
        framesPerSecond,
        verifyPruning
            ? TimeSpan.FromSeconds(6)
            : verifyRenewal
                ? TimeSpan.FromMinutes(5)
                : TimeSpan.FromSeconds(30),
        false,
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
        if (int.TryParse(
                GetOption(args, "--countdown"),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var countdownSeconds) && countdownSeconds > 0)
        {
            Console.WriteLine(
                $"Capture begins in {countdownSeconds}s. Switch to the fullscreen application now.");
            await Task.Delay(TimeSpan.FromSeconds(countdownSeconds), timeout.Token);
        }

        await replay.StartAsync(configuration, timeout.Token);
        Console.WriteLine($"Capture strategy: {replay.ActiveEncoderDescription ?? "unknown"}");
        var performance = await ReadPerformanceSampleAsync(replay, timeout.Token)
            ?? throw new InvalidDataException("Capture process metrics were unavailable during the smoke test.");
        if (performance.Priority is not (ProcessPriorityClass.BelowNormal or ProcessPriorityClass.Normal))
        {
            throw new InvalidDataException(
                $"Capture priority was {performance.Priority}; expected Normal for direct hardware capture " +
                "or BelowNormal for a fallback path.");
        }

        if (forceGdi && performance.Priority != ProcessPriorityClass.BelowNormal)
        {
            throw new InvalidDataException(
                $"Forced GDI capture priority was {performance.Priority}; expected BelowNormal.");
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
        if (verifyRenewal)
        {
            for (var renewalIndex = 1; renewalIndex <= renewalCount; renewalIndex++)
            {
                var previousProcessId = replay.CaptureProcessId
                    ?? throw new InvalidDataException(
                        $"The WGC process id was unavailable before renewal {renewalIndex}.");
                var beforeRenewal = ReadSegmentSnapshots(bufferDirectory);
                if (beforeRenewal.Count < 3)
                {
                    throw new InvalidDataException(
                        "The replay ring did not contain enough completed segments to verify renewal retention.");
                }

                AssertContiguousSegmentIds(beforeRenewal, $"before renewal {renewalIndex}");
                var previousTail = beforeRenewal[^1];
                var retainedBeforeRenewal = beforeRenewal
                    .Take(beforeRenewal.Count - 1)
                    .ToArray();
                var lastRetainedSegmentId = retainedBeforeRenewal[^1].Id;
                var refreshRequestedUtc = DateTimeOffset.UtcNow;
                var refreshTimer = Stopwatch.StartNew();
                bool renewed;
                if (verifyServiceOwnedRenewal)
                {
                    if (!replay.RequestScheduledCaptureRefresh(
                            $"Capture smoke scheduled renewal {renewalIndex}."))
                    {
                        throw new InvalidDataException(
                            $"The service-owned WGC renewal {renewalIndex} was not queued.");
                    }

                    await replay.WaitForScheduledCaptureRefreshIdleAsync()
                        .WaitAsync(timeout.Token)
                        .ConfigureAwait(false);
                    renewed = replay.CaptureProcessId != previousProcessId;
                }
                else
                {
                    renewed = await replay.RefreshCaptureAsync(previousProcessId, timeout.Token);
                }
                refreshTimer.Stop();
                var refreshCompletedUtc = DateTimeOffset.UtcNow;
                if (!renewed)
                {
                    throw new InvalidDataException(
                        $"WGC process renewal {renewalIndex} was unexpectedly rejected.");
                }

                var replacementProcessId = replay.CaptureProcessId
                    ?? throw new InvalidDataException(
                        $"The replacement WGC process id was unavailable after renewal {renewalIndex}.");
                if (replacementProcessId == previousProcessId)
                {
                    throw new InvalidDataException(
                        $"WGC renewal {renewalIndex} did not replace the FFmpeg process.");
                }

                var immediatelyAfterRenewal = ReadSegmentSnapshots(bufferDirectory);
                if (immediatelyAfterRenewal.Count == 0)
                {
                    throw new InvalidDataException(
                        $"WGC renewal {renewalIndex} left no segment in the replay ring.");
                }

                AssertContiguousSegmentIds(
                    immediatelyAfterRenewal,
                    $"immediately after renewal {renewalIndex}");
                var replacementFirstSegmentId = immediatelyAfterRenewal[^1].Id;
                if (replacementFirstSegmentId <= lastRetainedSegmentId)
                {
                    throw new InvalidDataException(
                        $"WGC renewal {renewalIndex} reset or reused completed segment ids: " +
                        $"last retained {lastRetainedSegmentId}, replacement {replacementFirstSegmentId}.");
                }

                var rollover = await WaitForCompletedReplacementSegmentAsync(
                    bufferDirectory,
                    replacementFirstSegmentId,
                    timeout.Token);
                AssertContiguousSegmentIds(
                    rollover.Segments,
                    $"after replacement segment rollover {renewalIndex}");
                var completedReplacement = rollover.Segments.Single(segment =>
                    segment.Id == replacementFirstSegmentId);
                if (completedReplacement.Length <= 0)
                {
                    throw new InvalidDataException(
                        $"Replacement process {replacementProcessId} produced an empty completed segment.");
                }

                var priorVersionOfReplacementSegment = beforeRenewal.FirstOrDefault(segment =>
                    segment.Id == replacementFirstSegmentId);
                if (priorVersionOfReplacementSegment is not null &&
                    priorVersionOfReplacementSegment.Length == completedReplacement.Length &&
                    priorVersionOfReplacementSegment.LastWriteTimeUtc >= completedReplacement.LastWriteTimeUtc)
                {
                    throw new InvalidDataException(
                        $"Replacement process {replacementProcessId} did not rewrite segment " +
                        $"{replacementFirstSegmentId} before its successor appeared.");
                }

                if (retainedBeforeRenewal.Any(segment => !File.Exists(segment.Path)))
                {
                    throw new InvalidDataException(
                        $"WGC renewal {renewalIndex} removed a completed segment from the existing replay ring.");
                }

                if (!replay.IsRunning)
                {
                    throw new InvalidDataException(
                        $"Replay stopped after WGC process renewal {renewalIndex}.");
                }

                if (IsFfmpegProcessAlive(previousProcessId))
                {
                    throw new InvalidDataException(
                        $"The retired FFmpeg process {previousProcessId} remained alive after WGC renewal {renewalIndex}.");
                }

                var successorSegmentId = rollover.Segments
                    .Where(segment => segment.Id > replacementFirstSegmentId)
                    .Min(segment => segment.Id);
                using var hostMetrics = Process.GetCurrentProcess();
                hostMetrics.Refresh();
                using var replacementMetrics = Process.GetProcessById(replacementProcessId);
                replacementMetrics.Refresh();
                var report = new RenewalSmokeReport(
                    renewalIndex,
                    previousProcessId,
                    replacementProcessId,
                    refreshRequestedUtc,
                    refreshCompletedUtc,
                    refreshTimer.Elapsed.TotalMilliseconds,
                    previousTail.Id,
                    previousTail.LastWriteTimeUtc,
                    retainedBeforeRenewal.Length,
                    lastRetainedSegmentId,
                    replacementFirstSegmentId,
                    completedReplacement.Path,
                    completedReplacement.Length,
                    completedReplacement.LastWriteTimeUtc,
                    successorSegmentId,
                    rollover.ObservedUtc,
                    hostMetrics.HandleCount,
                    hostMetrics.PrivateMemorySize64,
                    replacementMetrics.HandleCount,
                    replacementMetrics.WorkingSet64);
                renewalReports.Add(report);
                await WriteRenewalReportAsync(
                    renewalReportPath,
                    runLabel,
                    renewalReports,
                    timeout.Token);

                Console.WriteLine(
                    $"Renewal {renewalIndex}/{renewalCount}: FFmpeg {previousProcessId} -> " +
                    $"{replacementProcessId}; API restart window " +
                    $"{refreshRequestedUtc:O} .. {refreshCompletedUtc:O} " +
                    $"({refreshTimer.Elapsed.TotalMilliseconds:0.0} ms); retained " +
                    $"{retainedBeforeRenewal.Length} completed segment(s); replacement segment " +
                    $"{replacementFirstSegmentId} completed before segment {successorSegmentId} appeared; " +
                    $"host handles {hostMetrics.HandleCount}, FFmpeg handles {replacementMetrics.HandleCount}.");
            }

            if (renewalReports.Select(report => report.ReplacementProcessId).Distinct().Count() != renewalCount)
            {
                throw new InvalidDataException(
                    "Consecutive WGC renewals reused a replacement FFmpeg process id.");
            }

            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true);
            await Task.Delay(TimeSpan.FromMilliseconds(500), timeout.Token);
            using var settledHost = Process.GetCurrentProcess();
            settledHost.Refresh();
            var settledHostResources = new HostResourceSample(
                settledHost.HandleCount,
                settledHost.PrivateMemorySize64);
            var firstHostResources = renewalReports[0];
            if (settledHostResources.HandleCount > firstHostResources.HostHandleCount + 64)
            {
                throw new InvalidDataException(
                    "Host handle usage remained materially elevated after renewal finalization.");
            }

            if (settledHostResources.PrivateMemoryBytes >
                firstHostResources.HostPrivateMemoryBytes + (96L * 1024 * 1024))
            {
                throw new InvalidDataException(
                    "Host private memory remained materially elevated after renewal finalization.");
            }

            await WriteRenewalReportAsync(
                renewalReportPath,
                runLabel,
                renewalReports,
                timeout.Token,
                settledHostResources);
            Console.WriteLine(
                $"Settled host after forced finalization: {settledHostResources.HandleCount} handles, " +
                $"{settledHostResources.PrivateMemoryBytes / 1024d / 1024d:0.0} MB private memory.");
        }

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

        if (renewalReports.Count == 2)
        {
            var ringBeforeSave = ReadSegmentSnapshots(bufferDirectory);
            AssertContiguousSegmentIds(ringBeforeSave, "before the cross-renewal save");
            var completedForSixSecondSave = ringBeforeSave
                .Take(Math.Max(0, ringBeforeSave.Count - 1))
                .TakeLast(3)
                .Select(segment => segment.Id)
                .ToArray();
            var replacementSegmentIds = renewalReports
                .Select(report => report.ReplacementFirstSegmentId)
                .ToArray();
            if (replacementSegmentIds.Any(id => !completedForSixSecondSave.Contains(id)))
            {
                throw new InvalidDataException(
                    "The final six-second save would not span completed segments produced by both " +
                    $"replacement processes. Selected ids: {string.Join(", ", completedForSixSecondSave)}; " +
                    $"replacement ids: {string.Join(", ", replacementSegmentIds)}.");
            }

            Console.WriteLine(
                $"Cross-renewal save provenance: completed segment ids " +
                $"{string.Join(", ", completedForSixSecondSave)} include both replacements " +
                $"({string.Join(", ", replacementSegmentIds)}).");
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
    var expectedOutput = CaptureGeometry.ResolveOutputSize(display, resolution);
    var expectedWidth = expectedOutput.Width;
    var expectedHeight = expectedOutput.Height;
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

    var videoPacketTimes = await ReadPacketTimestampsAsync(
        ffprobe,
        clipPath,
        "v:0",
        "pts_time",
        timeout.Token);
    var maxVideoFrameDelta = ReadMaximumPositiveDelta(videoPacketTimes);
    var maximumAllowedFrameDelta = 1.6 / framesPerSecond;
    if (maxVideoFrameDelta > maximumAllowedFrameDelta)
    {
        throw new InvalidDataException(
            $"Saved video contained a {maxVideoFrameDelta * 1000:0.###} ms frame gap; " +
            $"expected at most {maximumAllowedFrameDelta * 1000:0.###} ms at {framesPerSecond} FPS.");
    }

    FrameMotionStats? motionStats = null;
    if (args.Contains("--motion-validation", StringComparer.OrdinalIgnoreCase))
    {
        motionStats = await ReadFrameMotionStatsAsync(
            ffmpeg,
            clipPath,
            framesPerSecond,
            timeout.Token);
        AssertMovingSurfaceCadence(motionStats, framesPerSecond, "live capture");
    }

    var expectedAudioStreams = includeSystemAudio || includeMicrophone ? 1 : 0;
    if (mediaInfo.AudioStreamCount != expectedAudioStreams)
    {
        throw new InvalidDataException(
            $"Saved clip contained {mediaInfo.AudioStreamCount} audio stream(s); expected {expectedAudioStreams}.");
    }

    if (expectedAudioStreams > 0)
    {
        var audioPacketTimes = await ReadPacketTimestampsAsync(
            ffprobe,
            clipPath,
            "a:0",
            "dts_time",
            timeout.Token);
        AssertMonotonicTimestamps(audioPacketTimes, "audio DTS");

        if (!double.IsFinite(mediaInfo.VideoDuration) ||
            !double.IsFinite(mediaInfo.AudioDuration) ||
            Math.Abs(mediaInfo.VideoDuration - mediaInfo.AudioDuration) >
            Math.Max(0.05, 2d / framesPerSecond))
        {
            throw new InvalidDataException(
                $"Saved clip A/V duration delta was " +
                $"{Math.Abs(mediaInfo.VideoDuration - mediaInfo.AudioDuration) * 1000:0.###} ms; " +
                "audio and video did not remain synchronized across segment joins.");
        }
    }

    Console.WriteLine($"PASS: {clipPath}");
    Console.WriteLine(
        $"Duration: {duration:0.###} seconds; size: {new FileInfo(clipPath).Length:N0} bytes; " +
        $"video: {mediaInfo.Width}x{mediaInfo.Height} at {mediaInfo.AverageFrameRate:0.###} FPS, " +
        $"frames: {mediaInfo.FrameCount.ToString(CultureInfo.InvariantCulture)}; " +
        $"max frame delta: {maxVideoFrameDelta * 1000:0.###} ms; " +
        (motionStats is null
            ? string.Empty
            : $"identical-frame ratio: {motionStats.DuplicateRatio:P1}; " +
              $"longest identical run: {motionStats.MaximumIdenticalDurationMilliseconds:0.###} ms; ") +
        $"audio streams: {mediaInfo.AudioStreamCount}; " +
        $"system audio requested: {includeSystemAudio}; microphone requested: {includeMicrophone}");
    if (renewalReports.Count > 0)
    {
        Console.WriteLine(
            $"Renewal report: {renewalReportPath} ({renewalReports.Count} consecutive renewal(s))");
    }

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

static int ParseRenewalCount(string? value)
{
    if (value is null)
    {
        // A renewal smoke is intended to prove the process can be rotated more
        // than once in one uninterrupted replay session. Callers can still use
        // --renew-count 1 for a faster local diagnostic.
        return 2;
    }

    if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var count) ||
        count is < 1 or > 12)
    {
        throw new ArgumentOutOfRangeException(
            nameof(value),
            "--renew-count must be an integer between 1 and 12.");
    }

    return count;
}

static IReadOnlyList<SegmentSnapshot> ReadSegmentSnapshots(string bufferDirectory) =>
    Directory
        .EnumerateFiles(bufferDirectory, "segment-*.mkv", SearchOption.AllDirectories)
        .Select(path =>
        {
            var file = new FileInfo(path);
            return new SegmentSnapshot(
                ParseSegmentId(file.Name),
                file.FullName,
                file.Length,
                file.LastWriteTimeUtc);
        })
        .OrderBy(segment => segment.Id)
        .ThenBy(segment => segment.Path, StringComparer.OrdinalIgnoreCase)
        .ToArray();

static int ParseSegmentId(string fileName)
{
    const string prefix = "segment-";
    const string extension = ".mkv";
    if (!fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
        !fileName.EndsWith(extension, StringComparison.OrdinalIgnoreCase) ||
        !int.TryParse(
            fileName.AsSpan(prefix.Length, fileName.Length - prefix.Length - extension.Length),
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out var id) ||
        id < 0)
    {
        throw new InvalidDataException($"Unexpected replay segment name '{fileName}'.");
    }

    return id;
}

static void AssertContiguousSegmentIds(
    IReadOnlyList<SegmentSnapshot> segments,
    string phase)
{
    for (var index = 1; index < segments.Count; index++)
    {
        if (segments[index].Id != segments[index - 1].Id + 1)
        {
            throw new InvalidDataException(
                $"Replay segment ids were not contiguous {phase}: " +
                $"{segments[index - 1].Id} -> {segments[index].Id}.");
        }
    }
}

static async Task<ReplacementSegmentRollover> WaitForCompletedReplacementSegmentAsync(
    string bufferDirectory,
    int replacementFirstSegmentId,
    CancellationToken cancellationToken)
{
    var timer = Stopwatch.StartNew();
    while (timer.Elapsed < TimeSpan.FromSeconds(10))
    {
        cancellationToken.ThrowIfCancellationRequested();
        var segments = ReadSegmentSnapshots(bufferDirectory);
        if (segments.Any(segment => segment.Id == replacementFirstSegmentId) &&
            segments.Any(segment => segment.Id > replacementFirstSegmentId))
        {
            return new ReplacementSegmentRollover(
                segments,
                DateTimeOffset.UtcNow);
        }

        await Task.Delay(50, cancellationToken);
    }

    throw new InvalidDataException(
        $"Replacement segment {replacementFirstSegmentId} did not complete within 10 seconds.");
}

static Task WriteRenewalReportAsync(
    string reportPath,
    string runLabel,
    IReadOnlyList<RenewalSmokeReport> renewals,
    CancellationToken cancellationToken,
    HostResourceSample? settledHostResources = null)
{
    var artifact = new RenewalSmokeArtifact(
        SchemaVersion: 3,
        GeneratedUtc: DateTimeOffset.UtcNow,
        RunLabel: runLabel,
        RenewalCount: renewals.Count,
        Renewals: renewals.ToArray(),
        SettledHostResources: settledHostResources);
    return File.WriteAllTextAsync(
        reportPath,
        JsonSerializer.Serialize(
            artifact,
            new JsonSerializerOptions { WriteIndented = true }),
        cancellationToken);
}

static bool IsFfmpegProcessAlive(int processId)
{
    try
    {
        using var process = Process.GetProcessById(processId);
        return !process.HasExited &&
               process.ProcessName.Equals("ffmpeg", StringComparison.OrdinalIgnoreCase);
    }
    catch (ArgumentException)
    {
        return false;
    }
    catch (InvalidOperationException)
    {
        return false;
    }
}

static async Task RunResolutionMatrixAsync(
    FfmpegSetupService setup,
    string ffmpeg,
    string artifactRoot,
    bool exhaustive,
    string? framesPerSecondOption,
    CancellationToken cancellationToken)
{
    var ffprobe = setup.FindProbeExecutable()
        ?? throw new InvalidOperationException("The verified FFprobe tool is unavailable for resolution matrix smoke.");
    var framesPerSecondValues = ParseMatrixFrameRates(framesPerSecondOption);
    MatrixSource[] sources =
    [
        new("hd", 1280, 720),
        new("laptop", 1366, 768),
        new("hd-plus", 1600, 900),
        new("full-hd", 1920, 1080),
        new("sixteen-ten", 1920, 1200),
        new("ultrawide-small", 2560, 1080),
        new("qhd", 2560, 1440),
        new("ultrawide", 3440, 1440),
        new("four-k", 3840, 2160),
        new("super-ultrawide", 5120, 1440),
        new("four-three", 1024, 768),
        new("square-custom", 1080, 1080),
        new("portrait", 1080, 1920),
        new("odd-full-hd", 1919, 1079),
        new("odd-laptop", 1365, 767)
    ];

    var geometryCases = new List<ResolutionMatrixCase>();
    foreach (var source in sources)
    {
        var display = new DisplayOption(
            $"synthetic-{source.Id}",
            source.Id,
            0,
            0,
            source.Width,
            source.Height,
            IsPrimary: true);
        foreach (var resolution in ResolutionOption.All)
        {
            var output = CaptureGeometry.ResolveOutputSize(display, resolution);
            AssertBoundedAspectGeometry(source, resolution, output);
            geometryCases.Add(new ResolutionMatrixCase(source, resolution, output));
        }
    }

    Console.WriteLine(
        $"Geometry matrix: {geometryCases.Count} source/preset combinations passed " +
        "(standard, 16:10, 4:3, ultrawide, super-ultrawide, square, portrait, and odd inputs).");

    IReadOnlyList<ResolutionMatrixCase> encodedCases = exhaustive
        ? geometryCases
        : geometryCases.Where(IsCuratedEncodedMatrixCase).ToArray();
    var runDirectory = Path.Combine(
        artifactRoot,
        $"resolution-matrix-{(exhaustive ? "exhaustive" : "curated")}-" +
        $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}");
    Directory.CreateDirectory(runDirectory);
    var runner = new ClipMediaProcessRunner();
    var passed = 0;
    foreach (var matrixCase in encodedCases)
    {
        foreach (var framesPerSecond in framesPerSecondValues)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var outputPath = Path.Combine(
                runDirectory,
                $"{matrixCase.Source.Id}-{matrixCase.Resolution.Id}-{framesPerSecond}fps.mp4");
            var arguments = BuildSyntheticResolutionArguments(matrixCase, framesPerSecond, outputPath);
            var result = await runner.RunAsync(
                    ffmpeg,
                    arguments,
                    TimeSpan.FromMinutes(3),
                    cancellationToken)
                .ConfigureAwait(false);
            if (!result.Succeeded || !File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
            {
                throw new InvalidDataException(
                    $"Resolution matrix encode failed for {matrixCase.Source.Width}x{matrixCase.Source.Height} " +
                    $"to {matrixCase.Resolution.Id} at {framesPerSecond} FPS: {result.StandardError.Trim()}");
            }

            var media = await ReadMediaInfoAsync(ffprobe, outputPath, cancellationToken).ConfigureAwait(false);
            var duration = await ReadDurationAsync(ffprobe, outputPath, cancellationToken).ConfigureAwait(false);
            var packetTimes = await ReadPacketTimestampsAsync(
                    ffprobe,
                    outputPath,
                    "v:0",
                    "pts_time",
                    cancellationToken)
                .ConfigureAwait(false);
            var maximumFrameDelta = ReadMaximumPositiveDelta(packetTimes);
            var motion = await ReadFrameMotionStatsAsync(
                    ffmpeg,
                    outputPath,
                    framesPerSecond,
                    cancellationToken)
                .ConfigureAwait(false);
            var expectedFrames = framesPerSecond;
            if (media.Width != matrixCase.Output.Width ||
                media.Height != matrixCase.Output.Height ||
                Math.Abs(media.AverageFrameRate - framesPerSecond) > 0.05 ||
                Math.Abs(media.FrameCount - expectedFrames) > 1 ||
                Math.Abs(motion.TotalFrames - media.FrameCount) > 1 ||
                duration is < 0.95 or > 1.10 ||
                maximumFrameDelta > 1.1 / framesPerSecond ||
                motion.DuplicateRatio > 0.05 ||
                motion.MaximumIdenticalDurationMilliseconds > 1.1 * 1000 / framesPerSecond)
            {
                throw new InvalidDataException(
                    $"Resolution/cadence regression for {matrixCase.Source.Width}x{matrixCase.Source.Height} " +
                    $"to {matrixCase.Resolution.Id} at {framesPerSecond} FPS: got " +
                    $"{media.Width}x{media.Height}, {media.AverageFrameRate:0.###} FPS, " +
                    $"{media.FrameCount} frames, {duration:0.###}s, maximum gap " +
                    $"{maximumFrameDelta * 1000:0.###}ms, identical ratio " +
                    $"{motion.DuplicateRatio:P1}, longest identical run " +
                    $"{motion.MaximumIdenticalDurationMilliseconds:0.###}ms.");
            }

            passed++;
            Console.WriteLine(
                $"PASS matrix {passed}: {matrixCase.Source.Width}x{matrixCase.Source.Height} -> " +
                $"{matrixCase.Output.Width}x{matrixCase.Output.Height} ({matrixCase.Resolution.Id}), " +
                $"{framesPerSecond} FPS, {media.FrameCount} frames, max gap " +
                $"{maximumFrameDelta * 1000:0.###}ms, duplicate ratio {motion.DuplicateRatio:P1}");
        }
    }

    Console.WriteLine(
        $"PASS resolution matrix: {geometryCases.Count} geometry checks and {passed} real FFmpeg encodes. " +
        $"Artifacts: {runDirectory}");
    if (!exhaustive)
    {
        Console.WriteLine(
            "The default encoded matrix is curated for release speed. Add --matrix-exhaustive to encode " +
            "every source/preset pair; geometry is exhaustive in both modes.");
    }
}

static IReadOnlyList<int> ParseMatrixFrameRates(string? value)
{
    if (string.IsNullOrWhiteSpace(value) ||
        value.Equals("both", StringComparison.OrdinalIgnoreCase))
    {
        return [30, 60];
    }

    var parsed = value
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(item => int.TryParse(item, NumberStyles.None, CultureInfo.InvariantCulture, out var fps)
            ? fps
            : 0)
        .Distinct()
        .ToArray();
    if (parsed.Length == 0 || parsed.Any(fps => fps is not (30 or 60)))
    {
        throw new ArgumentException("--matrix-fps must be 30, 60, 30,60, or both.");
    }

    return parsed;
}

static void AssertBoundedAspectGeometry(
    MatrixSource source,
    ResolutionOption resolution,
    CaptureOutputSize output)
{
    var evenSourceWidth = source.Width - source.Width % 2;
    var evenSourceHeight = source.Height - source.Height % 2;
    if (output.Width < 2 || output.Height < 2 || output.Width % 2 != 0 || output.Height % 2 != 0)
    {
        throw new InvalidDataException(
            $"Geometry {source.Width}x{source.Height}/{resolution.Id} produced invalid " +
            $"{output.Width}x{output.Height}.");
    }

    if (output.Width > evenSourceWidth || output.Height > evenSourceHeight)
    {
        throw new InvalidDataException(
            $"Geometry {source.Width}x{source.Height}/{resolution.Id} upscaled to " +
            $"{output.Width}x{output.Height}.");
    }

    if (resolution.Width is { } maximumWidth && resolution.Height is { } maximumHeight &&
        (output.Width > maximumWidth || output.Height > maximumHeight))
    {
        throw new InvalidDataException(
            $"Geometry {source.Width}x{source.Height}/{resolution.Id} exceeded its " +
            $"{maximumWidth}x{maximumHeight} bounds.");
    }

    var sourceAspect = source.Width / (double)source.Height;
    var outputAspect = output.Width / (double)output.Height;
    var relativeAspectError = Math.Abs(outputAspect - sourceAspect) / sourceAspect;
    var aspectTolerance = Math.Max(0.005, 2d / Math.Min(output.Width, output.Height));
    if (relativeAspectError > aspectTolerance)
    {
        throw new InvalidDataException(
            $"Geometry {source.Width}x{source.Height}/{resolution.Id} changed aspect ratio by " +
            $"{relativeAspectError:P3}: {output.Width}x{output.Height}.");
    }

    var presetNeedsDownscale = resolution.Width is { } width && resolution.Height is { } height &&
                               (source.Width > width || source.Height > height);
    if (presetNeedsDownscale != output.RequiresScaling)
    {
        throw new InvalidDataException(
            $"Geometry {source.Width}x{source.Height}/{resolution.Id} reported scaling=" +
            $"{output.RequiresScaling}, expected {presetNeedsDownscale}.");
    }
}

static bool IsCuratedEncodedMatrixCase(ResolutionMatrixCase matrixCase) =>
    matrixCase.Source.Id switch
    {
        "full-hd" => matrixCase.Resolution.Id is "source" or "720p" or "1080p",
        "sixteen-ten" => matrixCase.Resolution.Id is "1080p",
        "ultrawide" => matrixCase.Resolution.Id is "source" or "720p" or "1080p",
        "super-ultrawide" => matrixCase.Resolution.Id is "1080p",
        "four-three" => matrixCase.Resolution.Id is "720p" or "1080p",
        "square-custom" => matrixCase.Resolution.Id is "source" or "720p" or "1080p",
        "portrait" => matrixCase.Resolution.Id is "source" or "720p" or "1080p",
        "odd-full-hd" => matrixCase.Resolution.Id is "source" or "720p",
        _ => false
    };

static IReadOnlyList<string> BuildSyntheticResolutionArguments(
    ResolutionMatrixCase matrixCase,
    int framesPerSecond,
    string outputPath)
{
    List<string> arguments =
    [
        "-hide_banner",
        "-loglevel", "error",
        "-nostdin",
        "-f", "lavfi",
        "-i", $"testsrc2=duration=1:size={matrixCase.Source.Width}x{matrixCase.Source.Height}:rate={framesPerSecond}"
    ];
    if (matrixCase.Source.Width != matrixCase.Output.Width ||
        matrixCase.Source.Height != matrixCase.Output.Height)
    {
        arguments.AddRange(
        [
            "-vf",
            $"scale={matrixCase.Output.Width}:{matrixCase.Output.Height}:flags=fast_bilinear"
        ]);
    }

    arguments.AddRange(
    [
        "-map", "0:v:0",
        "-an",
        "-c:v", "libx264",
        "-preset", "ultrafast",
        "-tune", "zerolatency",
        "-crf", "23",
        "-pix_fmt", "yuv420p",
        "-r", framesPerSecond.ToString(CultureInfo.InvariantCulture),
        "-fps_mode", "cfr",
        "-g", (framesPerSecond * 2).ToString(CultureInfo.InvariantCulture),
        "-keyint_min", (framesPerSecond * 2).ToString(CultureInfo.InvariantCulture),
        "-sc_threshold", "0",
        "-bf", "0",
        "-threads", "2",
        "-movflags", "+faststart",
        "-f", "mp4",
        "-y",
        outputPath
    ]);
    return arguments;
}

static async Task RunInteractiveCaptureMatrixAsync(
    string[] arguments,
    string artifactRoot,
    CancellationToken cancellationToken)
{
    var requestedIds = (GetOption(arguments, "--matrix-resolutions") ??
                        string.Join(',', ResolutionOption.All.Select(option => option.Id)))
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    if (requestedIds.Length == 0)
    {
        throw new ArgumentException("--matrix-resolutions did not contain a resolution ID.");
    }

    var resolutions = requestedIds
        .Select(id => ResolutionOption.All.FirstOrDefault(option =>
            option.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"Unsupported WGC matrix resolution '{id}'."))
        .DistinctBy(option => option.Id, StringComparer.OrdinalIgnoreCase)
        .ToArray();
    var executable = Environment.ProcessPath
        ?? throw new InvalidOperationException("Could not locate the current capture smoke executable.");
    var entryAssembly = System.Reflection.Assembly.GetEntryAssembly()?.Location;
    var hostedByDotnet = Path.GetFileNameWithoutExtension(executable)
        .Equals("dotnet", StringComparison.OrdinalIgnoreCase);
    if (hostedByDotnet && string.IsNullOrWhiteSpace(entryAssembly))
    {
        throw new InvalidOperationException("Could not locate the capture smoke assembly for child runs.");
    }

    Console.WriteLine(
        "LIVE WGC MATRIX: this validates the currently selected display and active fullscreen surface. " +
        "It cannot change a game's internal/custom resolution; repeat it after changing that resolution.");
    if (arguments.Contains("--motion-validation", StringComparer.OrdinalIgnoreCase))
    {
        Console.WriteLine(
            "MOTION VALIDATION IS ENABLED: keep a continuously moving game, video, or test pattern visible " +
            "through every case. A static/paused surface intentionally fails duplicate-frame validation.");
    }
    foreach (var resolution in resolutions)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = false
        };
        if (hostedByDotnet)
        {
            startInfo.ArgumentList.Add(entryAssembly!);
        }

        foreach (var value in new[]
                 {
                     "--resolution", resolution.Id,
                     "--fps", GetOption(arguments, "--fps") ?? "60",
                     "--artifacts", artifactRoot
                 })
        {
            startInfo.ArgumentList.Add(value);
        }

        foreach (var switchName in new[]
                 {
                     "--audio", "--microphone", "--force-gdi", "--prune", "--motion-validation"
                 })
        {
            if (arguments.Contains(switchName, StringComparer.OrdinalIgnoreCase))
            {
                startInfo.ArgumentList.Add(switchName);
            }
        }

        if (GetOption(arguments, "--countdown") is { } countdown)
        {
            startInfo.ArgumentList.Add("--countdown");
            startInfo.ArgumentList.Add(countdown);
        }

        Console.WriteLine($"Starting live capture case {resolution.Id}...");
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Could not start WGC matrix case {resolution.Id}.");
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new InvalidDataException(
                $"Live WGC matrix case {resolution.Id} failed with exit code {process.ExitCode}.");
        }
    }

    Console.WriteLine(
        $"PASS live WGC matrix: {resolutions.Length} preset(s). " +
        "An exclusive-fullscreen game, its custom mode, driver, and affected GPU must still be tested on the target PC.");
}

static async Task RunReplayConcurrentTrimSmokeAsync(
    FfmpegSetupService setup,
    string ffmpeg,
    string artifactRoot,
    string[] arguments,
    CancellationToken cancellationToken)
{
    var ffprobe = setup.FindProbeExecutable()
        ?? throw new InvalidOperationException("The verified FFprobe tool is unavailable for replay/trim smoke.");
    var runDirectory = Path.Combine(
        artifactRoot,
        $"replay-trim-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}");
    var clipDirectory = Path.Combine(runDirectory, "clips");
    var bufferDirectory = Path.Combine(runDirectory, "buffer");
    Directory.CreateDirectory(clipDirectory);
    var includeSystemAudio = arguments.Contains("--audio", StringComparer.OrdinalIgnoreCase);
    var includeMicrophone = arguments.Contains("--microphone", StringComparer.OrdinalIgnoreCase);
    var seedPath = Path.Combine(runDirectory, "Clip_2026-07-13_15-45-00.mp4");
    await GenerateSyntheticTrimSourceAsync(
            ffmpeg,
            seedPath,
            includeAudio: true,
            cancellationToken)
        .ConfigureAwait(false);

    var discovery = new DeviceDiscoveryService();
    var displays = discovery.GetDisplays();
    var outputs = discovery.GetOutputDevices();
    var microphones = discovery.GetMicrophones();
    var display = displays.FirstOrDefault(candidate => candidate.IsPrimary) ?? displays.FirstOrDefault()
        ?? throw new InvalidOperationException("No display was available for replay/trim smoke.");
    if (includeSystemAudio && outputs.Count == 0)
    {
        throw new InvalidOperationException("Replay/trim smoke requested system audio but found no output device.");
    }

    if (includeMicrophone && microphones.Count == 0)
    {
        throw new InvalidOperationException("Replay/trim smoke requested a microphone but found no capture device.");
    }

    var resolutionId = GetOption(arguments, "--resolution") ?? "1080p";
    var resolution = ResolutionOption.All.FirstOrDefault(option =>
        option.Id.Equals(resolutionId, StringComparison.OrdinalIgnoreCase))
        ?? throw new ArgumentException($"Unsupported replay/trim resolution '{resolutionId}'.");
    var framesPerSecond = int.TryParse(
        GetOption(arguments, "--fps"),
        NumberStyles.None,
        CultureInfo.InvariantCulture,
        out var requestedFramesPerSecond)
        ? requestedFramesPerSecond
        : 60;
    if (framesPerSecond is not (30 or 60))
    {
        throw new ArgumentOutOfRangeException(nameof(framesPerSecond), "Replay/trim FPS must be 30 or 60.");
    }

    var configuration = new CaptureConfiguration(
        display,
        resolution,
        framesPerSecond,
        TimeSpan.FromSeconds(30),
        false,
        includeSystemAudio,
        includeSystemAudio ? outputs.FirstOrDefault(device => device.IsDefault) ?? outputs[0] : null,
        includeMicrophone,
        includeMicrophone ? microphones.FirstOrDefault(device => device.IsDefault) ?? microphones[0] : null,
        clipDirectory);
    VideoEncodingStrategy? strategyOverride = null;
    if (arguments.Contains("--force-gdi", StringComparer.OrdinalIgnoreCase))
    {
        var verifiedSelection = await new FfmpegCapabilityProbe()
            .SelectAsync(ffmpeg, configuration, cancellationToken)
            .ConfigureAwait(false);
        strategyOverride = verifiedSelection.Strategy with
        {
            CaptureBackend = DesktopCaptureBackend.Gdi,
            RequiresSystemMemoryTransfer = false
        };
    }

    await using var replay = strategyOverride is null
        ? new ReplayBufferService(setup, bufferDirectory)
        : new ReplayBufferService(setup, bufferDirectory, strategyOverride);
    var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    long availableTicks = 0;
    var faulted = 0;
    string? faultMessage = null;
    replay.StateChanged += (_, state) =>
    {
        Interlocked.Exchange(ref availableTicks, state.AvailableDuration.Ticks);
        Console.WriteLine(
            $"Replay/trim: {state.State}, available {state.AvailableDuration.TotalSeconds:0.###}s");
        if (state.State == ReplayState.Faulted)
        {
            Interlocked.Exchange(ref faulted, 1);
            faultMessage = state.Message;
            ready.TrySetException(new InvalidOperationException(state.Message ?? "Capture faulted."));
        }
        else if (state.AvailableDuration >= TimeSpan.FromSeconds(4))
        {
            ready.TrySetResult();
        }
    };

    string replayClipPath;
    try
    {
        await replay.StartAsync(configuration, cancellationToken).ConfigureAwait(false);
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        var beforeTrimTicks = Interlocked.Read(ref availableTicks);
        Console.WriteLine(
            $"Replay ready via {replay.ActiveEncoderDescription ?? "unknown"}; starting replay-safe trim.");
        await RunTrimSmokeAsync(
                setup,
                ffmpeg,
                runDirectory,
                includeAudio: true,
                seedPath,
                ClipTrimExecutionMode.ReplayCoexisting,
                cancellationToken)
            .ConfigureAwait(false);
        if (!replay.IsRunning || Volatile.Read(ref faulted) != 0)
        {
            throw new InvalidDataException(
                $"Replay stopped or faulted during replay-safe trim: {faultMessage ?? "no diagnostic"}");
        }

        var afterTrimTicks = Interlocked.Read(ref availableTicks);
        if (afterTrimTicks <= beforeTrimTicks)
        {
            throw new InvalidDataException(
                "Replay availability did not advance while the replay-safe trim was running.");
        }

        replayClipPath = await replay.SaveClipAsync(
                TimeSpan.FromSeconds(4),
                clipDirectory,
                cancellationToken)
            .ConfigureAwait(false);
    }
    finally
    {
        if (replay.IsRunning)
        {
            await replay.StopAsync().ConfigureAwait(false);
        }
    }

    var duration = await ReadDurationAsync(ffprobe, replayClipPath, cancellationToken).ConfigureAwait(false);
    var media = await ReadMediaInfoAsync(ffprobe, replayClipPath, cancellationToken).ConfigureAwait(false);
    var packetTimes = await ReadPacketTimestampsAsync(
            ffprobe,
            replayClipPath,
            "v:0",
            "pts_time",
            cancellationToken)
        .ConfigureAwait(false);
    var maximumFrameDelta = ReadMaximumPositiveDelta(packetTimes);
    var expectedOutput = CaptureGeometry.ResolveOutputSize(display, resolution);
    var expectedAudioStreams = includeSystemAudio || includeMicrophone ? 1 : 0;
    if (duration is < 3 or > 6 ||
        media.Width != expectedOutput.Width ||
        media.Height != expectedOutput.Height ||
        Math.Abs(media.AverageFrameRate - framesPerSecond) > framesPerSecond * 0.15 ||
        maximumFrameDelta > 1.6 / framesPerSecond ||
        media.AudioStreamCount != expectedAudioStreams)
    {
        throw new InvalidDataException(
            $"Replay output after concurrent trim was invalid: {duration:0.###}s, " +
            $"{media.Width}x{media.Height}, {media.AverageFrameRate:0.###} FPS, " +
            $"max gap {maximumFrameDelta * 1000:0.###}ms, audio streams {media.AudioStreamCount}.");
    }

    Console.WriteLine(
        $"PASS replay + concurrent trim: replay advanced during a paced one-thread trim, then saved " +
        $"{duration:0.###}s at {media.Width}x{media.Height}/{media.AverageFrameRate:0.###} FPS " +
        $"with maximum frame gap {maximumFrameDelta * 1000:0.###}ms. Artifacts: {runDirectory}");
}

static async Task GenerateSyntheticTrimSourceAsync(
    string ffmpeg,
    string sourcePath,
    bool includeAudio,
    CancellationToken cancellationToken)
{
    List<string> sourceArguments =
    [
        "-hide_banner",
        "-loglevel", "error",
        "-nostdin",
        "-f", "lavfi",
        "-i", "testsrc2=duration=8:size=640x360:rate=30"
    ];
    if (includeAudio)
    {
        sourceArguments.AddRange(
        [
            "-f", "lavfi",
            "-i", "sine=frequency=880:sample_rate=48000:duration=8"
        ]);
    }

    sourceArguments.AddRange(["-map", "0:v:0"]);
    if (includeAudio)
    {
        sourceArguments.AddRange(["-map", "1:a:0"]);
    }

    sourceArguments.AddRange(
    [
        "-c:v", "libx264",
        "-preset", "ultrafast",
        "-crf", "18",
        "-pix_fmt", "yuv420p",
        "-g", "60",
        "-keyint_min", "60",
        "-sc_threshold", "0"
    ]);
    if (includeAudio)
    {
        sourceArguments.AddRange(["-c:a", "aac", "-b:a", "192k", "-ar", "48000", "-ac", "2"]);
    }
    else
    {
        sourceArguments.Add("-an");
    }

    sourceArguments.AddRange(["-movflags", "+faststart", "-f", "mp4", "-n", sourcePath]);
    var generation = await new ClipMediaProcessRunner().RunAsync(
            ffmpeg,
            sourceArguments,
            TimeSpan.FromMinutes(5),
            cancellationToken)
        .ConfigureAwait(false);
    if (!generation.Succeeded || !File.Exists(sourcePath) || new FileInfo(sourcePath).Length == 0)
    {
        throw new InvalidDataException(
            $"Synthetic trim source generation failed: {generation.StandardError.Trim()}");
    }
}

static async Task RunConcatTimingSmokeAsync(
    FfmpegSetupService setup,
    string ffmpeg,
    string artifactRoot,
    CancellationToken cancellationToken)
{
    const int framesPerSecond = 60;
    const int segmentCount = 3;
    var ffprobe = setup.FindProbeExecutable()
        ?? throw new InvalidOperationException("The verified FFprobe tool is unavailable for concat smoke.");
    var runDirectory = Path.Combine(
        artifactRoot,
        $"concat-smoke-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}");
    Directory.CreateDirectory(runDirectory);
    var processRunner = new ClipMediaProcessRunner();
    var segments = new List<string>(segmentCount);

    for (var index = 0; index < segmentCount; index++)
    {
        var segmentPath = Path.Combine(runDirectory, $"segment-{index:D9}.mkv");
        IReadOnlyList<string> generationArguments =
        [
            "-hide_banner",
            "-loglevel", "error",
            "-nostdin",
            "-f", "lavfi",
            "-i", $"testsrc2=duration=2:size=1920x1080:rate={framesPerSecond}",
            "-f", "lavfi",
            "-i", $"sine=frequency={660 + (index * 110)}:sample_rate=48000:duration=2",
            "-map", "0:v:0",
            "-map", "1:a:0",
            "-c:v", "libx264",
            "-preset", "ultrafast",
            "-tune", "zerolatency",
            "-crf", "23",
            "-pix_fmt", "yuv420p",
            "-g", "120",
            "-keyint_min", "120",
            "-sc_threshold", "0",
            "-bf", "0",
            "-c:a", "aac",
            "-b:a", "192k",
            "-ar", "48000",
            "-ac", "2",
            "-f", "matroska",
            "-y",
            segmentPath
        ];
        var generated = await processRunner.RunAsync(
                ffmpeg,
                generationArguments,
                TimeSpan.FromMinutes(3),
                cancellationToken)
            .ConfigureAwait(false);
        if (!generated.Succeeded || !File.Exists(segmentPath))
        {
            throw new InvalidDataException(
                $"Synthetic replay segment {index} failed: {generated.StandardError.Trim()}");
        }

        segments.Add(segmentPath);
    }

    var manifestPath = Path.Combine(runDirectory, "manifest.txt");
    await File.WriteAllLinesAsync(
            manifestPath,
            ReplayBufferService.BuildConcatManifestLines(segments),
            cancellationToken)
        .ConfigureAwait(false);
    var outputPath = Path.Combine(runDirectory, "Clip_2026-07-13_16-30-00.mp4");
    var requestedDuration = TimeSpan.FromSeconds(
        segmentCount * FfmpegArgumentBuilder.SegmentSeconds);
    var concat = await processRunner.RunAsync(
            ffmpeg,
            FfmpegArgumentBuilder.BuildConcatArguments(
                manifestPath,
                outputPath,
                TimeSpan.Zero,
                requestedDuration),
            TimeSpan.FromMinutes(3),
            cancellationToken)
        .ConfigureAwait(false);
    if (!concat.Succeeded || !File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
    {
        throw new InvalidDataException($"Synthetic replay concat failed: {concat.StandardError.Trim()}");
    }

    var media = await ReadMediaInfoAsync(ffprobe, outputPath, cancellationToken).ConfigureAwait(false);
    var videoPacketTimes = await ReadPacketTimestampsAsync(
            ffprobe,
            outputPath,
            "v:0",
            "pts_time",
            cancellationToken)
        .ConfigureAwait(false);
    var maxVideoFrameDelta = ReadMaximumPositiveDelta(videoPacketTimes);
    if (media.Width != 1920 ||
        media.Height != 1080 ||
        Math.Abs(media.AverageFrameRate - framesPerSecond) > 0.25 ||
        Math.Abs(media.FrameCount - segmentCount * 2 * framesPerSecond) > 2 ||
        maxVideoFrameDelta > 0.025)
    {
        throw new InvalidDataException(
            $"Concat timing regression: {media.Width}x{media.Height}, " +
            $"{media.AverageFrameRate:0.###} FPS, {media.FrameCount} frames, " +
            $"maximum frame delta {maxVideoFrameDelta * 1000:0.###} ms.");
    }

    if (media.AudioStreamCount != 1)
    {
        throw new InvalidDataException("Concat timing smoke did not retain exactly one mixed audio stream.");
    }

    var audioPacketTimes = await ReadPacketTimestampsAsync(
            ffprobe,
            outputPath,
            "a:0",
            "dts_time",
            cancellationToken)
        .ConfigureAwait(false);
    AssertMonotonicTimestamps(audioPacketTimes, "audio DTS");
    var avDurationDelta = Math.Abs(media.VideoDuration - media.AudioDuration);
    if (!double.IsFinite(avDurationDelta) || avDurationDelta > 0.05)
    {
        throw new InvalidDataException(
            $"Concat A/V duration delta was {avDurationDelta * 1000:0.###} ms; expected at most 50 ms.");
    }

    Console.WriteLine($"PASS concat timing: {outputPath}");
    Console.WriteLine(
        $"Concat: 1920x1080 at {media.AverageFrameRate:0.###} FPS, {media.FrameCount} frames, " +
        $"maximum frame delta {maxVideoFrameDelta * 1000:0.###} ms, " +
        $"A/V delta {avDurationDelta * 1000:0.###} ms, monotonic audio DTS.");
}

static async Task RunTrimSmokeAsync(
    FfmpegSetupService setup,
    string ffmpeg,
    string artifactRoot,
    bool includeAudio,
    string? existingSourcePath,
    ClipTrimExecutionMode executionMode,
    CancellationToken cancellationToken)
{
    var ffprobe = setup.FindProbeExecutable()
        ?? throw new InvalidOperationException("The verified FFprobe tool is unavailable for trim smoke.");
    var runDirectory = Path.Combine(
        artifactRoot,
        $"trim-smoke-{executionMode.ToString().ToLowerInvariant()}-" +
        $"{(includeAudio ? "audio" : "silent")}-" +
        $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}");
    Directory.CreateDirectory(runDirectory);
    var sourcePath = Path.Combine(runDirectory, "Clip_2026-07-13_16-00-00.mp4");
    if (!string.IsNullOrWhiteSpace(existingSourcePath))
    {
        var sourceOverride = Path.GetFullPath(existingSourcePath);
        if (!File.Exists(sourceOverride))
        {
            throw new FileNotFoundException("The requested existing trim-smoke source was not found.", sourceOverride);
        }

        File.Copy(sourceOverride, sourcePath, overwrite: false);
    }
    else
    {
        await GenerateSyntheticTrimSourceAsync(
                ffmpeg,
                sourcePath,
                includeAudio,
                cancellationToken)
            .ConfigureAwait(false);
    }

    var sourceHash = ComputeSha256(sourcePath);
    var sourceDuration = await ReadDurationAsync(ffprobe, sourcePath, cancellationToken)
        .ConfigureAwait(false);
    var sourceMedia = await ReadMediaInfoAsync(ffprobe, sourcePath, cancellationToken)
        .ConfigureAwait(false);
    if (!ClipTrimService.TryNormalizeRange(
            TimeSpan.FromMilliseconds(1113),
            TimeSpan.FromMilliseconds(6287),
            TimeSpan.FromSeconds(sourceDuration),
            sourceMedia.NominalFrameRate,
            out var expectedRange,
            out var rangeError))
    {
        throw new InvalidDataException($"Synthetic trim range was invalid: {rangeError}");
    }

    var sourceInfo = new FileInfo(sourcePath);
    if (!ClipLibraryService.TryGetCurrentFileIdentity(sourcePath, out var sourceIdentity))
    {
        throw new InvalidDataException("Synthetic trim source has no stable Windows file identity.");
    }

    var source = new ClipLibraryItem(
        sourceInfo.Name,
        sourceInfo.FullName,
        new DateTimeOffset(DateTime.SpecifyKind(sourceInfo.LastWriteTimeUtc, DateTimeKind.Utc)),
        sourceInfo.Length,
        TimeSpan.FromSeconds(sourceDuration))
    {
        FileIdentity = sourceIdentity,
        Kind = ClipKind.Original
    };
    if (!ClipLibraryService.TryClassifyClipFileName(source.FileName, out var sourceKind) ||
        sourceKind != ClipKind.Original)
    {
        throw new InvalidDataException("Synthetic trim source was not classified as Original.");
    }

    var baselineHelpers = SnapshotMediaHelperProcessIds(ffmpeg, ffprobe);
    var trim = await new ClipTrimService(setup).TrimAsync(
            runDirectory,
            source,
            TimeSpan.FromMilliseconds(1113),
            TimeSpan.FromMilliseconds(6287),
            executionMode,
            cancellationToken)
        .ConfigureAwait(false);
    if (!trim.Succeeded || trim.OutputPath is null || !File.Exists(trim.OutputPath))
    {
        throw new InvalidDataException($"Real trim export failed ({trim.Status}): {trim.Message}");
    }

    var outputPath = Path.GetFullPath(trim.OutputPath);
    if (outputPath.Equals(sourcePath, StringComparison.OrdinalIgnoreCase) ||
        !Path.GetDirectoryName(outputPath)!.Equals(runDirectory, StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidDataException("Trim export did not create a separate top-level output.");
    }

    if (!File.Exists(sourcePath) ||
        !CryptographicOperations.FixedTimeEquals(sourceHash, ComputeSha256(sourcePath)) ||
        !ClipLibraryService.TryGetCurrentClipPath(runDirectory, source, out _))
    {
        throw new InvalidDataException("Trim export changed or replaced the original source.");
    }

    if (!ClipLibraryService.TryClassifyClipFileName(
            Path.GetFileName(outputPath),
            out var outputKind) || outputKind != ClipKind.Trimmed)
    {
        throw new InvalidDataException("Trim output does not use the strict Trimmed naming grammar.");
    }

    var outputDuration = await ReadDurationAsync(ffprobe, outputPath, cancellationToken)
        .ConfigureAwait(false);
    var outputMedia = await ReadMediaInfoAsync(ffprobe, outputPath, cancellationToken)
        .ConfigureAwait(false);
    var durationTolerance = Math.Max(0.2, 2 / sourceMedia.AverageFrameRate + 0.1);
    if (Math.Abs(outputDuration - expectedRange.Duration.TotalSeconds) > durationTolerance)
    {
        throw new InvalidDataException(
            $"Trim duration was {outputDuration:0.###}s; expected {expectedRange.Duration.TotalSeconds:0.###}s.");
    }

    if (outputMedia.Width != sourceMedia.Width || outputMedia.Height != sourceMedia.Height)
    {
        throw new InvalidDataException(
            $"Trim dimensions were {outputMedia.Width}x{outputMedia.Height}; " +
            $"expected {sourceMedia.Width}x{sourceMedia.Height}.");
    }

    if (Math.Abs(outputMedia.NominalFrameRate - sourceMedia.NominalFrameRate) > 0.1)
    {
        throw new InvalidDataException(
            $"Trim nominal frame rate was {outputMedia.NominalFrameRate:0.###}; " +
            $"expected {sourceMedia.NominalFrameRate:0.###}.");
    }

    var expectedFrames = expectedRange.Duration.TotalSeconds * sourceMedia.AverageFrameRate;
    var frameCountTolerance = Math.Max(2, Math.Ceiling(expectedFrames * 0.02));
    if (Math.Abs(outputMedia.FrameCount - expectedFrames) > frameCountTolerance)
    {
        throw new InvalidDataException(
            $"Trim contained {outputMedia.FrameCount} frames; expected approximately " +
            $"{expectedFrames:0.###} (tolerance {frameCountTolerance:0}).");
    }

    var expectedAudioStreams = includeAudio ? 1 : 0;
    if (outputMedia.AudioStreamCount != expectedAudioStreams)
    {
        throw new InvalidDataException(
            $"Trim contained {outputMedia.AudioStreamCount} audio stream(s); expected {expectedAudioStreams}.");
    }

    var partials = Directory.EnumerateFiles(
            runDirectory,
            ".clipforge-trim-*.partial.mp4",
            SearchOption.TopDirectoryOnly)
        .ToArray();
    if (partials.Length != 0)
    {
        throw new InvalidDataException($"Trim left {partials.Length} partial output(s) behind.");
    }

    var orphanHelpers = SnapshotMediaHelperProcessIds(ffmpeg, ffprobe)
        .Except(baselineHelpers)
        .ToArray();
    if (orphanHelpers.Length != 0)
    {
        throw new InvalidDataException(
            $"Trim left media helper process ID(s) running: {string.Join(", ", orphanHelpers)}.");
    }

    Console.WriteLine($"PASS trim: {outputPath}");
    Console.WriteLine(
        $"Trim: {expectedRange.Start.TotalSeconds:0.###}-{expectedRange.End.TotalSeconds:0.###}s, " +
        $"duration {outputDuration:0.###}s, frames {outputMedia.FrameCount}, " +
        $"video {outputMedia.Width}x{outputMedia.Height} at nominal {outputMedia.NominalFrameRate:0.###} FPS " +
        $"(average {outputMedia.AverageFrameRate:0.###}), " +
        $"audio streams {outputMedia.AudioStreamCount}, original retained, no partial/orphan helper.");
}

static byte[] ComputeSha256(string path)
{
    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
    return SHA256.HashData(stream);
}

static HashSet<int> SnapshotMediaHelperProcessIds(params string[] executablePaths)
{
    var targets = executablePaths
        .Select(Path.GetFullPath)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);
    var processNames = targets
        .Select(Path.GetFileNameWithoutExtension)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();
    var result = new HashSet<int>();
    foreach (var processName in processNames)
    {
        foreach (var process in Process.GetProcessesByName(processName))
        {
            using (process)
            {
                try
                {
                    if (process.MainModule?.FileName is { } processPath &&
                        targets.Contains(Path.GetFullPath(processPath)))
                    {
                        result.Add(process.Id);
                    }
                }
                catch (Exception exception) when (
                    exception is InvalidOperationException or System.ComponentModel.Win32Exception or
                        NotSupportedException)
                {
                    // A process can exit between enumeration and path inspection.
                }
            }
        }
    }

    return result;
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
                 "-show_entries", "stream=codec_type,width,height,avg_frame_rate,r_frame_rate,nb_read_frames,duration",
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
    JsonElement? audioStream = null;
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
            audioStream ??= stream;
        }
    }

    if (videoStream is not { } video)
    {
        throw new InvalidDataException("Saved clip did not contain a video stream.");
    }

    var averageFrameRate = ParseFraction(video.GetProperty("avg_frame_rate").GetString());
    var nominalFrameRate = ParseFraction(video.GetProperty("r_frame_rate").GetString());
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
        nominalFrameRate,
        frameCount,
        audioStreamCount,
        ReadOptionalDuration(video),
        audioStream is { } audio ? ReadOptionalDuration(audio) : double.NaN);
}

static async Task<FrameMotionStats> ReadFrameMotionStatsAsync(
    string ffmpeg,
    string mediaPath,
    int framesPerSecond,
    CancellationToken cancellationToken)
{
    const int analysisWidth = 64;
    const int analysisHeight = 36;
    const int frameBytes = analysisWidth * analysisHeight;
    var startInfo = new ProcessStartInfo
    {
        FileName = ffmpeg,
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true
    };
    foreach (var argument in new[]
             {
                 "-hide_banner",
                 "-loglevel", "error",
                 "-nostdin",
                 "-f", "mov",
                 "-protocol_whitelist", "file",
                 "-i", mediaPath,
                 "-map", "0:v:0",
                 "-vf", $"scale={analysisWidth}:{analysisHeight}:flags=fast_bilinear,format=gray",
                 "-an",
                 "-c:v", "rawvideo",
                 "-threads", "1",
                 "-fps_mode", "passthrough",
                 "-pix_fmt", "gray",
                 "-f", "rawvideo",
                 "pipe:1"
             })
    {
        startInfo.ArgumentList.Add(argument);
    }

    using var process = Process.Start(startInfo)
        ?? throw new InvalidOperationException("Could not start FFmpeg for decoded-frame hash validation.");
    var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
    var frame = new byte[frameBytes];
    byte[]? previousHash = null;
    var totalFrames = 0;
    var duplicateTransitions = 0;
    var currentIdenticalRunFrames = 0;
    var maximumIdenticalRunFrames = 0;
    while (true)
    {
        var offset = 0;
        while (offset < frame.Length)
        {
            var read = await process.StandardOutput.BaseStream.ReadAsync(
                    frame.AsMemory(offset, frame.Length - offset),
                    cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            offset += read;
        }

        if (offset == 0)
        {
            break;
        }

        if (offset != frame.Length)
        {
            throw new InvalidDataException(
                $"Decoded-frame validation ended with a partial {offset}/{frame.Length}-byte frame.");
        }

        var hash = SHA256.HashData(frame);
        totalFrames++;
        if (previousHash is not null &&
            CryptographicOperations.FixedTimeEquals(previousHash, hash))
        {
            duplicateTransitions++;
            currentIdenticalRunFrames++;
        }
        else
        {
            currentIdenticalRunFrames = 1;
        }

        maximumIdenticalRunFrames = Math.Max(maximumIdenticalRunFrames, currentIdenticalRunFrames);
        previousHash = hash;
    }

    var error = await errorTask.ConfigureAwait(false);
    await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
    if (process.ExitCode != 0)
    {
        throw new InvalidDataException($"Decoded-frame hash validation failed: {error.Trim()}");
    }

    if (totalFrames < 2)
    {
        throw new InvalidDataException(
            $"Decoded-frame hash validation returned only {totalFrames} frame(s).");
    }

    var duplicateRatio = duplicateTransitions / (double)(totalFrames - 1);
    var maximumIdenticalDurationMilliseconds =
        Math.Max(0, maximumIdenticalRunFrames - 1) * 1000d / framesPerSecond;
    return new FrameMotionStats(
        totalFrames,
        duplicateTransitions,
        maximumIdenticalRunFrames,
        duplicateRatio,
        maximumIdenticalDurationMilliseconds);
}

static void AssertMovingSurfaceCadence(
    FrameMotionStats motion,
    int framesPerSecond,
    string label)
{
    const double maximumFreezeMilliseconds = 150;
    const double maximumDuplicateRatio = 0.70;
    if (motion.MaximumIdenticalDurationMilliseconds > maximumFreezeMilliseconds ||
        motion.DuplicateRatio > maximumDuplicateRatio)
    {
        throw new InvalidDataException(
            $"{label} decoded-frame hashes indicate duplicate/frozen output: " +
            $"{motion.DuplicateTransitions}/{motion.TotalFrames - 1} adjacent transitions were identical " +
            $"({motion.DuplicateRatio:P1}), and the longest identical run spanned " +
            $"{motion.MaximumIdenticalDurationMilliseconds:0.###}ms at {framesPerSecond} FPS. " +
            "This check is valid only while a continuously moving surface is visible.");
    }
}

static double ReadOptionalDuration(JsonElement stream) =>
    stream.TryGetProperty("duration", out var durationElement) &&
    double.TryParse(
        durationElement.GetString(),
        NumberStyles.Float,
        CultureInfo.InvariantCulture,
        out var duration) &&
    duration > 0
        ? duration
        : double.NaN;

static async Task<IReadOnlyList<double>> ReadPacketTimestampsAsync(
    string ffprobe,
    string mediaPath,
    string streamSelector,
    string entry,
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
                 "-select_streams", streamSelector,
                 "-show_entries", $"packet={entry}",
                 "-of", "default=noprint_wrappers=1:nokey=1",
                 mediaPath
             })
    {
        startInfo.ArgumentList.Add(argument);
    }

    using var process = Process.Start(startInfo)
        ?? throw new InvalidOperationException("Could not start ffprobe for packet-timing validation.");
    var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
    var error = await process.StandardError.ReadToEndAsync(cancellationToken);
    await process.WaitForExitAsync(cancellationToken);
    if (process.ExitCode != 0)
    {
        throw new InvalidDataException($"ffprobe packet-timing validation failed: {error.Trim()}");
    }

    var timestamps = output
        .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(value => double.TryParse(
            value,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out var timestamp)
            ? timestamp
            : double.NaN)
        .Where(double.IsFinite)
        .ToArray();
    if (timestamps.Length < 2)
    {
        throw new InvalidDataException(
            $"ffprobe returned too few {streamSelector} packet timestamps for timing validation.");
    }

    return timestamps;
}

static double ReadMaximumPositiveDelta(IReadOnlyList<double> timestamps)
{
    var maximum = 0d;
    for (var index = 1; index < timestamps.Count; index++)
    {
        maximum = Math.Max(maximum, timestamps[index] - timestamps[index - 1]);
    }

    return maximum;
}

static void AssertMonotonicTimestamps(IReadOnlyList<double> timestamps, string label)
{
    for (var index = 1; index < timestamps.Count; index++)
    {
        if (timestamps[index] + 0.000001 < timestamps[index - 1])
        {
            throw new InvalidDataException(
                $"Saved clip {label} moved backwards at packet {index}: " +
                $"{timestamps[index - 1]:0.######} to {timestamps[index]:0.######}.");
        }
    }
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

internal sealed record SegmentSnapshot(
    int Id,
    string Path,
    long Length,
    DateTimeOffset LastWriteTimeUtc);

internal sealed record ReplacementSegmentRollover(
    IReadOnlyList<SegmentSnapshot> Segments,
    DateTimeOffset ObservedUtc);

internal sealed record RenewalSmokeReport(
    int RenewalIndex,
    int PreviousProcessId,
    int ReplacementProcessId,
    DateTimeOffset RefreshRequestedUtc,
    DateTimeOffset RefreshCompletedUtc,
    double RefreshApiWindowMilliseconds,
    int PreviousTailSegmentId,
    DateTimeOffset PreviousTailLastWriteTimeUtc,
    int RetainedCompletedSegmentCount,
    int LastRetainedSegmentId,
    int ReplacementFirstSegmentId,
    string ReplacementCompletedSegmentPath,
    long ReplacementCompletedSegmentBytes,
    DateTimeOffset ReplacementCompletedSegmentLastWriteTimeUtc,
    int SuccessorSegmentId,
    DateTimeOffset SuccessorObservedUtc,
    int HostHandleCount,
    long HostPrivateMemoryBytes,
    int ReplacementHandleCount,
    long ReplacementWorkingSetBytes);

internal sealed record RenewalSmokeArtifact(
    int SchemaVersion,
    DateTimeOffset GeneratedUtc,
    string RunLabel,
    int RenewalCount,
    IReadOnlyList<RenewalSmokeReport> Renewals,
    HostResourceSample? SettledHostResources);

internal sealed record HostResourceSample(
    int HandleCount,
    long PrivateMemoryBytes);

internal sealed record MatrixSource(string Id, int Width, int Height);

internal sealed record ResolutionMatrixCase(
    MatrixSource Source,
    ResolutionOption Resolution,
    CaptureOutputSize Output);

internal sealed record FrameMotionStats(
    int TotalFrames,
    int DuplicateTransitions,
    int MaximumIdenticalRunFrames,
    double DuplicateRatio,
    double MaximumIdenticalDurationMilliseconds);

internal sealed record MediaInfo(
    int Width,
    int Height,
    double AverageFrameRate,
    double NominalFrameRate,
    long FrameCount,
    int AudioStreamCount,
    double VideoDuration,
    double AudioDuration);
