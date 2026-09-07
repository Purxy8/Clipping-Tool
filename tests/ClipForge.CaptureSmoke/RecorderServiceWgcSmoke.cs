using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using ClipForge.Capture;
using ClipForge.Models;
using ClipForge.Services;

internal static class RecorderServiceWgcSmoke
{
    private const int MinimumCaptureSeconds = 1;
    // Keep the interactive smoke bounded, but allow it to cross Recorder's
    // ten-second recovery boundary and validate one closed checkpoint plus the
    // graceful partial tail through the real service fallback.
    private const int MaximumCaptureSeconds = 30;
    private static readonly TimeSpan MaximumFinalizationTime = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(10);

    internal static async Task RunAsync(
        FfmpegSetupService setup,
        string ffmpeg,
        string artifactRoot,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(setup);
        ArgumentException.ThrowIfNullOrWhiteSpace(ffmpeg);
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactRoot);
        ArgumentNullException.ThrowIfNull(arguments);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "The Recorder service smoke requires Windows Graphics Capture.");
        }

        var captureSeconds = ParseIntegerOption(
            arguments,
            "--save-seconds",
            defaultValue: MinimumCaptureSeconds);
        if (captureSeconds is < MinimumCaptureSeconds or > MaximumCaptureSeconds)
        {
            throw new ArgumentOutOfRangeException(
                nameof(captureSeconds),
                $"--recorder-service-smoke requires --save-seconds between " +
                $"{MinimumCaptureSeconds} and {MaximumCaptureSeconds}.");
        }

        var framesPerSecond = ParseIntegerOption(arguments, "--fps", defaultValue: 60);
        if (framesPerSecond is not (30 or 60))
        {
            throw new ArgumentOutOfRangeException(
                nameof(framesPerSecond),
                "Recorder service smoke FPS must be 30 or 60.");
        }

        var resolutionId = GetOption(arguments, "--resolution") ?? "source";
        var resolution = ResolutionOption.All.FirstOrDefault(option =>
            option.Id.Equals(resolutionId, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException(
                $"Unsupported Recorder service smoke resolution '{resolutionId}'.");
        var includeAudio = arguments.Contains(
            "--audio",
            StringComparer.OrdinalIgnoreCase);
        var forceSegmentFallback = arguments.Contains(
            "--force-segment-fallback",
            StringComparer.OrdinalIgnoreCase);

        var discovery = new DeviceDiscoveryService();
        var displays = discovery.GetDisplays();
        var display = displays.FirstOrDefault(candidate => candidate.IsPrimary) ??
                      displays.FirstOrDefault() ??
                      throw new InvalidOperationException(
                          "No display was available for the Recorder service smoke.");
        AudioDeviceOption? outputDevice = null;
        if (includeAudio)
        {
            var outputDevices = discovery.GetOutputDevices();
            outputDevice = outputDevices.FirstOrDefault(device => device.IsDefault) ??
                           outputDevices.FirstOrDefault() ??
                           throw new InvalidOperationException(
                               "Desktop audio was requested, but no active output endpoint was available.");
        }

        var runDirectory = Path.Combine(
            Path.GetFullPath(artifactRoot),
            $"recorder-service-wgc-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}");
        var saveDirectory = Path.Combine(runDirectory, "clips");
        var bufferRoot = Path.Combine(runDirectory, "service-buffer");
        Directory.CreateDirectory(saveDirectory);
        Directory.CreateDirectory(bufferRoot);
        var availableFreeBytes = ReplayBufferService.TryGetAvailableFreeSpace(saveDirectory);
        if (availableFreeBytes is null ||
            !RecordingStoragePolicy.HasStartCapacity(availableFreeBytes.Value))
        {
            throw new IOException(
                "Recorder service smoke requires the same four-GiB free-space reserve as the app.");
        }

        var unlockedConfiguration = new CaptureConfiguration(
            display,
            resolution,
            framesPerSecond,
            RecordingStoragePolicy.NoReplayRetention,
            CaptureCursor: false,
            CaptureSystemAudio: includeAudio,
            OutputAudioDevice: outputDevice,
            CaptureMicrophone: false,
            MicrophoneDevice: null,
            SaveDirectory: saveDirectory)
        {
            SessionMode = CaptureSessionMode.Recording
        };
        var lockedOutput = CaptureGeometry.ResolveOutputSize(unlockedConfiguration);
        var configuration = unlockedConfiguration with
        {
            LockOutputGeometry = true,
            LockedOutputWidth = lockedOutput.Width,
            LockedOutputHeight = lockedOutput.Height
        };

        Console.WriteLine(
            $"Recorder service smoke: primary {display.Width}x{display.Height} display, " +
            $"{resolution.Id} -> {lockedOutput.Width}x{lockedOutput.Height}, " +
            $"{framesPerSecond} FPS, {(includeAudio ? "desktop audio" : "silent")}, " +
            $"{captureSeconds}s wall capture" +
            $"{(forceSegmentFallback ? ", forced recovery-segment fallback" : string.Empty)}.");
        Console.WriteLine($"Artifacts: {runDirectory}");

        var capability = await new FfmpegCapabilityProbe()
            .SelectAsync(ffmpeg, configuration, cancellationToken)
            .ConfigureAwait(false);
        Console.WriteLine($"Capability selection: {capability.Strategy.Description}");
        Console.WriteLine($"Capability diagnostics: {capability.Diagnostics}");
        if (capability.Strategy.CaptureBackend !=
            DesktopCaptureBackend.WindowsGraphicsCapture)
        {
            throw new InvalidOperationException(
                "The verified capability selection did not produce a Windows Graphics Capture " +
                $"strategy. Diagnostics: {capability.Diagnostics}");
        }

        var ffprobe = setup.FindProbeExecutable()
            ?? throw new InvalidOperationException(
                "The verified FFprobe executable was unavailable.");
        var workingRoot = RecordingStoragePolicy.GetWorkingRoot(saveDirectory);
        var recoveryRoot = RecordingRecoveryJournal.GetRecoveryRoot(bufferRoot);
        var stateFault = new TaskCompletionSource<ReplayStateSnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        ReplayStateSnapshot? latestState = null;
        string? sessionDirectory = null;
        string? recoveryJournalPath = null;
        string? finalPath = null;
        var captureWallTime = TimeSpan.Zero;
        var finalizationTime = TimeSpan.Zero;
        var cleanupTime = TimeSpan.Zero;

        await using var replay = new ReplayBufferService(
            setup,
            bufferRoot,
            capability.Strategy);
        replay.StateChanged += (_, state) =>
        {
            Volatile.Write(ref latestState, state);
            Console.WriteLine(
                $"Recorder: {state.State}, trusted " +
                $"{state.AvailableDuration.TotalSeconds:0.###}s, " +
                $"{state.BufferBytes:N0} bytes.");
            if (state.State == ReplayState.Faulted)
            {
                stateFault.TrySetResult(state);
            }
        };

        try
        {
            await replay.StartAsync(configuration, cancellationToken).ConfigureAwait(false);
            if (!replay.IsRunning ||
                replay.ActiveSessionMode != CaptureSessionMode.Recording ||
                replay.LastCapturePlan?.Strategy.CaptureBackend !=
                    DesktopCaptureBackend.WindowsGraphicsCapture ||
                replay.CaptureProcessId is not { } captureProcessId ||
                captureProcessId <= 0)
            {
                throw new InvalidDataException(
                    "ReplayBufferService did not start a live WGC Recorder process.");
            }

            sessionDirectory = await WaitForSingleSessionDirectoryAsync(
                    workingRoot,
                    TimeSpan.FromSeconds(3),
                    cancellationToken)
                .ConfigureAwait(false);
            if (!RecordingRecoveryJournal.TryGetSessionMarkerId(
                    sessionDirectory,
                    out var recoverySessionId) ||
                recoverySessionId is null)
            {
                throw new InvalidDataException(
                    "Recorder service smoke did not create an authenticated recovery marker.");
            }
            recoveryJournalPath = RecordingRecoveryJournal.GetJournalPath(
                recoveryRoot,
                sessionDirectory,
                recoverySessionId);

            var captureTimer = Stopwatch.StartNew();
            var holdTask = Task.Delay(
                TimeSpan.FromSeconds(captureSeconds),
                cancellationToken);
            var firstCompleted = await Task.WhenAny(holdTask, stateFault.Task)
                .ConfigureAwait(false);
            if (firstCompleted == stateFault.Task)
            {
                var fault = await stateFault.Task.ConfigureAwait(false);
                throw new InvalidOperationException(
                    fault.Message ?? "Recorder faulted during the integration smoke.");
            }

            await holdTask.ConfigureAwait(false);
            captureTimer.Stop();
            captureWallTime = captureTimer.Elapsed;

            if (forceSegmentFallback)
            {
                replay.InvalidateLiveRecordingFastPathForTesting();
            }

            var finalizationTimer = Stopwatch.StartNew();
            using (var finalizationCancellation =
                   CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                finalizationCancellation.CancelAfter(TimeSpan.FromSeconds(10));
                finalPath = await replay.StopAndSaveRecordingAsync(
                        saveDirectory,
                        finalizationCancellation.Token)
                    .ConfigureAwait(false);
            }

            finalizationTimer.Stop();
            finalizationTime = finalizationTimer.Elapsed;
            if (finalizationTime >= MaximumFinalizationTime)
            {
                throw new InvalidDataException(
                    $"Recorder finalization took {finalizationTime.TotalMilliseconds:0.0}ms; " +
                    $"the integration bound is below " +
                    $"{MaximumFinalizationTime.TotalMilliseconds:0}ms.");
            }

            if (replay.IsRunning || replay.HasPendingRecording)
            {
                throw new InvalidDataException(
                    "Recorder remained active or retained a pending session after a successful save.");
            }

            var terminalState = Volatile.Read(ref latestState);
            if (terminalState?.State != ReplayState.Stopped ||
                !string.Equals(
                    terminalState.LastSavedPath,
                    finalPath,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "Recorder did not publish the final stopped/saved state.");
            }

            var media = await ReadAndValidateMediaAsync(
                    ffprobe,
                    finalPath,
                    lockedOutput.Width,
                    lockedOutput.Height,
                    framesPerSecond,
                    captureSeconds,
                    includeAudio,
                    cancellationToken)
                .ConfigureAwait(false);
            var boxes = await ReadTopLevelMp4BoxesAsync(
                    finalPath,
                    cancellationToken)
                .ConfigureAwait(false);
            ValidateFinalFragmentedLayout(finalPath, boxes);

            var cleanupTimer = Stopwatch.StartNew();
            await WaitForRecoveryCleanupAsync(
                    workingRoot,
                    sessionDirectory,
                    recoveryJournalPath,
                    CleanupTimeout,
                    cancellationToken)
                .ConfigureAwait(false);
            cleanupTimer.Stop();
            cleanupTime = cleanupTimer.Elapsed;

            var reportPath = Path.Combine(runDirectory, "recorder-service-wgc-report.json");
            await File.WriteAllTextAsync(
                    reportPath,
                    JsonSerializer.Serialize(
                        new
                        {
                            SchemaVersion = 1,
                            GeneratedUtc = DateTimeOffset.UtcNow,
                            RequestedCaptureSeconds = captureSeconds,
                            Resolution = resolution.Id,
                            FramesPerSecond = framesPerSecond,
                            IncludeAudio = includeAudio,
                            ForceSegmentFallback = forceSegmentFallback,
                            Display = new
                            {
                                display.DeviceName,
                                display.Width,
                                display.Height,
                                display.RefreshRateHz
                            },
                            Output = new
                            {
                                lockedOutput.Width,
                                lockedOutput.Height
                            },
                            Capability = new
                            {
                                capability.Strategy.Description,
                                Backend = capability.Strategy.CaptureBackend.ToString(),
                                Encoder = capability.Strategy.Encoder.ToString(),
                                capability.Diagnostics
                            },
                            CaptureWallMilliseconds = captureWallTime.TotalMilliseconds,
                            FinalizationMilliseconds = finalizationTime.TotalMilliseconds,
                            CleanupMilliseconds = cleanupTime.TotalMilliseconds,
                            SessionDirectory = sessionDirectory,
                            RecoveryJournalPath = recoveryJournalPath,
                            FinalPath = finalPath,
                            FinalBytes = new FileInfo(finalPath).Length,
                            Media = media,
                            TopLevelBoxes = boxes
                        },
                        new JsonSerializerOptions { WriteIndented = true }),
                    cancellationToken)
                .ConfigureAwait(false);

            Console.WriteLine(
                $"PASS Recorder service smoke: {media.DurationSeconds:0.###}s, " +
                $"{media.VideoFrameCount} frames at {media.AverageFrameRate:0.###} FPS, " +
                $"finalized in {finalizationTime.TotalMilliseconds:0.0}ms, " +
                $"work/recovery cleanup in {cleanupTime.TotalMilliseconds:0.0}ms.");
            Console.WriteLine($"Saved: {finalPath}");
            Console.WriteLine($"Report: {reportPath}");
        }
        finally
        {
            if (replay.IsRunning)
            {
                await replay.StopAsync().ConfigureAwait(false);
            }
        }
    }

    private static async Task<string> WaitForSingleSessionDirectoryAsync(
        string workingRoot,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directories = Directory.Exists(workingRoot)
                ? Directory.EnumerateDirectories(
                        workingRoot,
                        "session-*",
                        SearchOption.TopDirectoryOnly)
                    .ToArray()
                : [];
            if (directories.Length == 1)
            {
                return Path.GetFullPath(directories[0]);
            }

            if (directories.Length > 1)
            {
                throw new InvalidDataException(
                    "The isolated Recorder work root contained more than one active session.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken)
                .ConfigureAwait(false);
        }

        throw new TimeoutException(
            "ReplayBufferService did not create its Recorder work directory within three seconds.");
    }

    private static async Task WaitForRecoveryCleanupAsync(
        string workingRoot,
        string sessionDirectory,
        string recoveryJournalPath,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var remainingSessions = Directory.Exists(workingRoot)
                ? Directory.EnumerateDirectories(
                        workingRoot,
                        "session-*",
                        SearchOption.TopDirectoryOnly)
                    .ToArray()
                : [];
            if (!Directory.Exists(sessionDirectory) &&
                !File.Exists(recoveryJournalPath) &&
                remainingSessions.Length == 0)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken)
                .ConfigureAwait(false);
        }

        throw new TimeoutException(
            $"Recorder recovery cleanup exceeded {timeout.TotalSeconds:0}s: " +
            $"sessionExists={Directory.Exists(sessionDirectory)}, " +
            $"journalExists={File.Exists(recoveryJournalPath)}.");
    }

    private static async Task<RecorderMediaInfo> ReadAndValidateMediaAsync(
        string ffprobe,
        string mediaPath,
        int expectedWidth,
        int expectedHeight,
        int expectedFramesPerSecond,
        int requestedCaptureSeconds,
        bool expectedAudio,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(mediaPath) || new FileInfo(mediaPath).Length <= 0)
        {
            throw new InvalidDataException(
                "Recorder did not publish a non-empty final MP4.");
        }

        var output = await RunProbeAsync(
                ffprobe,
                [
                    "-v", "error",
                    "-count_frames",
                    "-show_entries",
                    "format=start_time,duration,size:" +
                    "stream=codec_type,codec_name,width,height,start_time,duration," +
                    "avg_frame_rate,nb_read_frames",
                    "-of", "json",
                    mediaPath
                ],
                cancellationToken)
            .ConfigureAwait(false);
        using var document = JsonDocument.Parse(output);
        var format = document.RootElement.GetProperty("format");
        var formatStart = ParseRequiredDouble(format, "start_time", mediaPath);
        var duration = ParseRequiredDouble(format, "duration", mediaPath);
        JsonElement? video = null;
        JsonElement? audio = null;
        var audioStreamCount = 0;
        foreach (var stream in document.RootElement.GetProperty("streams").EnumerateArray())
        {
            var codecType = stream.GetProperty("codec_type").GetString();
            if (codecType == "video" && video is null)
            {
                video = stream;
            }
            else if (codecType == "audio")
            {
                audioStreamCount++;
                audio ??= stream;
            }
        }

        if (video is not { } videoStream)
        {
            throw new InvalidDataException("Recorder's final MP4 did not contain video.");
        }

        var videoCodec = videoStream.GetProperty("codec_name").GetString();
        var width = videoStream.GetProperty("width").GetInt32();
        var height = videoStream.GetProperty("height").GetInt32();
        var videoStart = ParseRequiredDouble(videoStream, "start_time", mediaPath);
        var videoDuration = ParseRequiredDouble(videoStream, "duration", mediaPath);
        var averageFrameRate = ParseFraction(
            videoStream.GetProperty("avg_frame_rate").GetString(),
            mediaPath);
        if (!long.TryParse(
                videoStream.GetProperty("nb_read_frames").GetString(),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var videoFrameCount) ||
            videoFrameCount <= 0)
        {
            throw new InvalidDataException(
                "FFprobe did not return a valid decoded frame count for Recorder's MP4.");
        }

        var minimumExpectedDuration = requestedCaptureSeconds < 3
            ? Math.Max(0.5, requestedCaptureSeconds - 0.5)
            : requestedCaptureSeconds - 2.25;
        var maximumExpectedDuration = requestedCaptureSeconds < 3
            ? requestedCaptureSeconds + 0.75
            : requestedCaptureSeconds + 2.25;
        if (!string.Equals(videoCodec, "h264", StringComparison.OrdinalIgnoreCase) ||
            width != expectedWidth ||
            height != expectedHeight ||
            Math.Abs(formatStart) > 0.05 ||
            Math.Abs(videoStart) > 0.05 ||
            averageFrameRate < expectedFramesPerSecond * 0.85 ||
            averageFrameRate > expectedFramesPerSecond * 1.15 ||
            duration < minimumExpectedDuration ||
            duration > maximumExpectedDuration ||
            videoFrameCount < Math.Floor(
                duration * expectedFramesPerSecond * 0.85))
        {
            throw new InvalidDataException(
                $"Recorder MP4 validation failed: codec={videoCodec}, " +
                $"{width}x{height}, start={formatStart:0.######}/" +
                $"{videoStart:0.######}s, duration={duration:0.######}s, " +
                $"fps={averageFrameRate:0.###}, frames={videoFrameCount}.");
        }

        var expectedAudioStreams = expectedAudio ? 1 : 0;
        if (audioStreamCount != expectedAudioStreams)
        {
            throw new InvalidDataException(
                $"Recorder MP4 contained {audioStreamCount} audio stream(s); " +
                $"expected {expectedAudioStreams}.");
        }

        double? audioDuration = null;
        if (expectedAudio)
        {
            var audioStream = audio ?? throw new InvalidDataException(
                "Recorder's final MP4 omitted requested desktop audio.");
            if (!string.Equals(
                    audioStream.GetProperty("codec_name").GetString(),
                    "aac",
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "Recorder's final desktop-audio stream was not AAC.");
            }

            audioDuration = ParseRequiredDouble(audioStream, "duration", mediaPath);
            if (Math.Abs(videoDuration - audioDuration.Value) > 0.10)
            {
                throw new InvalidDataException(
                    $"Recorder A/V endpoints differed by " +
                    $"{Math.Abs(videoDuration - audioDuration.Value) * 1000:0.###}ms.");
            }
        }

        var videoPacketTimes = await ReadPacketTimesAsync(
                ffprobe,
                mediaPath,
                "v:0",
                "pts_time",
                cancellationToken)
            .ConfigureAwait(false);
        var maximumFrameGap = ReadMaximumPositiveDelta(videoPacketTimes);
        var maximumAllowedFrameGap = 1.7 / expectedFramesPerSecond;
        if (maximumFrameGap > maximumAllowedFrameGap)
        {
            throw new InvalidDataException(
                $"Recorder MP4 contained a {maximumFrameGap * 1000:0.###}ms video gap; " +
                $"the {expectedFramesPerSecond} FPS bound is " +
                $"{maximumAllowedFrameGap * 1000:0.###}ms.");
        }

        if (expectedAudio)
        {
            var audioPacketTimes = await ReadPacketTimesAsync(
                    ffprobe,
                    mediaPath,
                    "a:0",
                    "dts_time",
                    cancellationToken)
                .ConfigureAwait(false);
            AssertMonotonic(audioPacketTimes, "audio DTS");
        }

        return new RecorderMediaInfo(
            formatStart,
            duration,
            videoCodec ?? string.Empty,
            width,
            height,
            averageFrameRate,
            videoFrameCount,
            videoDuration,
            audioStreamCount,
            audioDuration,
            maximumFrameGap);
    }

    private static async Task<IReadOnlyList<double>> ReadPacketTimesAsync(
        string ffprobe,
        string mediaPath,
        string streamSelector,
        string entry,
        CancellationToken cancellationToken)
    {
        var output = await RunProbeAsync(
                ffprobe,
                [
                    "-v", "error",
                    "-f", "mov",
                    "-protocol_whitelist", "file",
                    "-select_streams", streamSelector,
                    "-show_entries", $"packet={entry}",
                    "-of", "default=noprint_wrappers=1:nokey=1",
                    mediaPath
                ],
                cancellationToken)
            .ConfigureAwait(false);
        var values = output
            .Split(
                ['\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries |
                StringSplitOptions.TrimEntries)
            .Select(value => double.TryParse(
                value,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var parsed)
                ? parsed
                : double.NaN)
            .Where(double.IsFinite)
            .ToArray();
        if (values.Length < 2)
        {
            throw new InvalidDataException(
                $"FFprobe returned too few {streamSelector} packets for Recorder validation.");
        }

        return values;
    }

    private static async Task<string> RunProbeAsync(
        string ffprobe,
        IReadOnlyList<string> arguments,
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
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start FFprobe.");
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var output = await outputTask.ConfigureAwait(false);
        var error = await errorTask.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new InvalidDataException(
                $"FFprobe rejected Recorder's final MP4: {error.Trim()}");
        }

        return output;
    }

    private static async Task<IReadOnlyList<string>> ReadTopLevelMp4BoxesAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var boxes = new List<string>();
        var header = new byte[16];
        while (stream.Position < stream.Length)
        {
            var boxStart = stream.Position;
            await stream.ReadExactlyAsync(header.AsMemory(0, 8), cancellationToken)
                .ConfigureAwait(false);
            var shortSize = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0, 4));
            var type = Encoding.ASCII.GetString(header, 4, 4);
            long size;
            var headerBytes = 8;
            if (shortSize == 1)
            {
                await stream.ReadExactlyAsync(header.AsMemory(8, 8), cancellationToken)
                    .ConfigureAwait(false);
                size = checked(
                    (long)BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(8, 8)));
                headerBytes = 16;
            }
            else
            {
                size = shortSize == 0 ? stream.Length - boxStart : shortSize;
            }

            if (size < headerBytes || boxStart + size > stream.Length)
            {
                throw new InvalidDataException(
                    $"Invalid top-level MP4 box '{type}' at offset {boxStart}.");
            }

            boxes.Add(type);
            stream.Position = boxStart + size;
        }

        return boxes;
    }

    private static void ValidateFinalFragmentedLayout(
        string path,
        IReadOnlyList<string> boxes)
    {
        var fileTypeIndex = IndexOf(boxes, "ftyp");
        var movieMetadataIndex = IndexOf(boxes, "moov");
        var firstFragmentIndex = IndexOf(boxes, "moof");
        var fragmentCount = boxes.Count(type => type == "moof");
        var mediaDataCount = boxes.Count(type => type == "mdat");
        if (fileTypeIndex != 0 ||
            boxes.Count(type => type == "ftyp") != 1 ||
            boxes.Count(type => type == "moov") != 1 ||
            movieMetadataIndex <= fileTypeIndex ||
            firstFragmentIndex <= movieMetadataIndex ||
            fragmentCount == 0 ||
            fragmentCount != mediaDataCount ||
            boxes[^1] != "mfra" ||
            !boxes.Skip(firstFragmentIndex)
                .Take(boxes.Count - firstFragmentIndex - 1)
                .Select((type, index) =>
                    index % 2 == 0 ? type == "moof" : type == "mdat")
                .All(valid => valid))
        {
            throw new InvalidDataException(
                $"Recorder final MP4 '{path}' was not the bounded-fragment fast-path output: " +
                string.Join(", ", boxes));
        }
    }

    private static int IndexOf(IReadOnlyList<string> values, string value)
    {
        for (var index = 0; index < values.Count; index++)
        {
            if (values[index].Equals(value, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return -1;
    }

    private static double ReadMaximumPositiveDelta(IReadOnlyList<double> values)
    {
        var maximum = 0d;
        for (var index = 1; index < values.Count; index++)
        {
            maximum = Math.Max(maximum, values[index] - values[index - 1]);
        }

        return maximum;
    }

    private static void AssertMonotonic(
        IReadOnlyList<double> values,
        string label)
    {
        for (var index = 1; index < values.Count; index++)
        {
            if (values[index] + 0.000001 < values[index - 1])
            {
                throw new InvalidDataException(
                    $"Recorder {label} moved backwards at packet {index}: " +
                    $"{values[index - 1]:0.######} to {values[index]:0.######}.");
            }
        }
    }

    private static double ParseRequiredDouble(
        JsonElement element,
        string propertyName,
        string mediaPath)
    {
        if (!element.TryGetProperty(propertyName, out var value) ||
            !double.TryParse(
                value.GetString(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var parsed) ||
            !double.IsFinite(parsed))
        {
            throw new InvalidDataException(
                $"FFprobe returned an invalid {propertyName} for '{mediaPath}'.");
        }

        return parsed;
    }

    private static double ParseFraction(string? value, string mediaPath)
    {
        var parts = value?.Split('/', StringSplitOptions.TrimEntries);
        if (parts is not { Length: 2 } ||
            !double.TryParse(
                parts[0],
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var numerator) ||
            !double.TryParse(
                parts[1],
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var denominator) ||
            denominator == 0)
        {
            throw new InvalidDataException(
                $"FFprobe returned invalid frame rate '{value}' for '{mediaPath}'.");
        }

        return numerator / denominator;
    }

    private static int ParseIntegerOption(
        IReadOnlyList<string> arguments,
        string name,
        int defaultValue)
    {
        var value = GetOption(arguments, name);
        if (value is null)
        {
            return defaultValue;
        }

        return int.TryParse(
            value,
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out var parsed)
            ? parsed
            : throw new ArgumentException($"{name} must be an integer.");
    }

    private static string? GetOption(
        IReadOnlyList<string> arguments,
        string name)
    {
        for (var index = 0; index < arguments.Count; index++)
        {
            if (arguments[index].Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return index + 1 < arguments.Count
                    ? arguments[index + 1]
                    : null;
            }
        }

        return null;
    }

    private sealed record RecorderMediaInfo(
        double StartTimeSeconds,
        double DurationSeconds,
        string VideoCodec,
        int Width,
        int Height,
        double AverageFrameRate,
        long VideoFrameCount,
        double VideoDurationSeconds,
        int AudioStreamCount,
        double? AudioDurationSeconds,
        double MaximumVideoFrameGapSeconds);
}
