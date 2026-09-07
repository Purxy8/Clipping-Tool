using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using ClipForge.Capture;
using ClipForge.Models;
using ClipForge.Services;

internal static class RecorderFastPathSmoke
{
    private const int FramesPerSecond = 60;
    private const int Width = 640;
    private const int Height = 360;
    private const int RecoverySegmentSeconds =
        FfmpegArgumentBuilder.RecordingRecoverySegmentSeconds;
    private static readonly TimeSpan MaximumTrailerCloseTime = TimeSpan.FromSeconds(3);

    internal static async Task RunAsync(
        FfmpegSetupService setup,
        string ffmpeg,
        string artifactRoot,
        int requestedSeconds,
        bool includeAudio,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(setup);
        ArgumentException.ThrowIfNullOrWhiteSpace(ffmpeg);
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactRoot);
        if (requestedSeconds is < 4 or > 60)
        {
            throw new ArgumentOutOfRangeException(
                nameof(requestedSeconds),
                "--recorder-fast-path-smoke requires --save-seconds between 4 and 60.");
        }

        var ffprobe = setup.FindProbeExecutable()
            ?? throw new InvalidOperationException(
                "The verified FFprobe tool is unavailable for the Recorder fast-path smoke.");
        var runDirectory = Path.Combine(
            artifactRoot,
            $"100%-recorder-fast-path-{(includeAudio ? "audio" : "silent")}-" +
            $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}");
        var segmentDirectory = Path.Combine(runDirectory, "Sam's segment output");
        var directRecordingPath = Path.Combine(
            runDirectory,
            "Sam's live recording.partial.mp4");
        Directory.CreateDirectory(segmentDirectory);

        var arguments = BuildArguments(
            segmentDirectory,
            directRecordingPath,
            includeAudio);
        Console.WriteLine(
            $"Recorder fast-path smoke: {requestedSeconds}s, 60 FPS, " +
            $"{(includeAudio ? "AAC audio" : "no audio")}.");
        Console.WriteLine($"Artifacts: {runDirectory}");

        var capture = await RunUntilProgressAsync(
                ffmpeg,
                arguments,
                TimeSpan.FromSeconds(requestedSeconds),
                cancellationToken)
            .ConfigureAwait(false);
        if (capture.TrailerCloseTime > MaximumTrailerCloseTime)
        {
            throw new InvalidDataException(
                $"Fragmented MP4 trailer close took {capture.TrailerCloseTime.TotalMilliseconds:0.0}ms; " +
                $"the smoke bound is {MaximumTrailerCloseTime.TotalMilliseconds:0}ms.");
        }

        if (!capture.ProgressEnded ||
            capture.FinalFrame < requestedSeconds * FramesPerSecond * 0.95)
        {
            throw new InvalidDataException(
                $"FFmpeg progress ended={capture.ProgressEnded}, frame={capture.FinalFrame}; " +
                "the tee capture timeline did not reach the requested duration.");
        }

        if (!File.Exists(directRecordingPath) ||
            new FileInfo(directRecordingPath).Length == 0)
        {
            throw new InvalidDataException(
                "The direct fragmented MP4 slave did not produce one non-empty Start-to-Stop file.");
        }

        var recordingPartPaths = Directory
            .EnumerateFiles(runDirectory, "*.part-*.mp4", SearchOption.TopDirectoryOnly)
            .ToArray();
        if (recordingPartPaths.Length != 0)
        {
            throw new InvalidDataException(
                "The continuous direct slave unexpectedly produced split files: " +
                string.Join(", ", recordingPartPaths.Select(Path.GetFileName)));
        }

        var segmentPaths = Directory
            .EnumerateFiles(segmentDirectory, "segment-*.mkv", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var minimumSegmentCount = Math.Max(
            1,
            (int)Math.Ceiling(requestedSeconds / (double)RecoverySegmentSeconds));
        if (segmentPaths.Length < minimumSegmentCount)
        {
            throw new InvalidDataException(
                $"The authoritative segment slave produced {segmentPaths.Length} segment(s); " +
                $"expected at least {minimumSegmentCount}.");
        }

        AssertContiguousSegmentNames(segmentPaths);
        var expectedAudioStreams = includeAudio ? 1 : 0;
        var directMedia = await ReadMediaAsync(
                ffprobe,
                directRecordingPath,
                cancellationToken)
            .ConfigureAwait(false);
        var directVideoPackets = await ReadPacketsAsync(
                ffprobe,
                directRecordingPath,
                "v:0",
                cancellationToken)
            .ConfigureAwait(false);
        ValidateVideoTimeline(
            directRecordingPath,
            directMedia,
            directVideoPackets,
            expectedAudioStreams);
        if (Math.Abs(directMedia.StartTimeSeconds) > 0.001 ||
            Math.Abs(directVideoPackets.FirstPresentationTimeSeconds) > 0.001)
        {
            throw new InvalidDataException(
                $"Continuous MP4 did not start at zero: format=" +
                $"{directMedia.StartTimeSeconds:0.######}s, first packet=" +
                $"{directVideoPackets.FirstPresentationTimeSeconds:0.######}s.");
        }

        var directDuration = directMedia.DurationSeconds;
        if (directDuration < requestedSeconds - 0.25 ||
            directDuration > requestedSeconds + 1.5)
        {
            throw new InvalidDataException(
                $"Direct recording duration was {directDuration:0.###}s; " +
                $"expected {requestedSeconds - 0.25:0.###}s to " +
                $"{requestedSeconds + 1.5:0.###}s from Start through Stop.");
        }

        var keyFrames = directVideoPackets
            .Where(packet => packet.IsKeyFrame)
            .Select(packet => packet.PresentationTimeSeconds)
            .ToArray();
        if (keyFrames.Length < segmentPaths.Length - 1)
        {
            throw new InvalidDataException(
                $"Direct recording exposed {keyFrames.Length} keyframes for " +
                $"{segmentPaths.Length} segment files.");
        }

        for (var index = 1; index < keyFrames.Length; index++)
        {
            var interval = keyFrames[index] - keyFrames[index - 1];
            if (interval is < 1.95 or > 2.05)
            {
                throw new InvalidDataException(
                    $"Direct recording keyframe interval {index} was {interval:0.######}s; expected 2s.");
            }
        }

        PacketTimeline? directAudioPackets = null;
        if (includeAudio)
        {
            directAudioPackets = await ReadPacketsAsync(
                    ffprobe,
                    directRecordingPath,
                    "a:0",
                    cancellationToken)
                .ConfigureAwait(false);
            ValidateAudioTimeline(directRecordingPath, directAudioPackets);
            if (Math.Abs(
                    directVideoPackets.LastEndTimeSeconds -
                    directAudioPackets.LastEndTimeSeconds) > 0.10)
            {
                throw new InvalidDataException(
                    "Direct recording audio/video endpoints differ by more than 100ms.");
            }
        }

        var segmentResults = new List<object>(segmentPaths.Length);
        long segmentVideoFrames = 0;
        var segmentAudioPackets = 0;
        var segmentTimelineSeconds = 0d;
        for (var index = 0; index < segmentPaths.Length; index++)
        {
            var segmentPath = segmentPaths[index];
            if (new FileInfo(segmentPath).Length == 0)
            {
                throw new InvalidDataException(
                    $"Authoritative segment '{Path.GetFileName(segmentPath)}' was empty.");
            }

            var media = await ReadMediaAsync(ffprobe, segmentPath, cancellationToken)
                .ConfigureAwait(false);
            var videoPackets = await ReadPacketsAsync(
                    ffprobe,
                    segmentPath,
                    "v:0",
                    cancellationToken)
                .ConfigureAwait(false);
            ValidateVideoTimeline(
                segmentPath,
                media,
                videoPackets,
                expectedAudioStreams);
            var packetDuration = videoPackets.LastEndTimeSeconds -
                                 videoPackets.FirstPresentationTimeSeconds;
            if (index < segmentPaths.Length - 1 &&
                (packetDuration < RecoverySegmentSeconds - 0.10 ||
                 packetDuration > RecoverySegmentSeconds + 0.10))
            {
                throw new InvalidDataException(
                    $"Completed segment {index} video timeline was {packetDuration:0.######}s; " +
                    $"expected {RecoverySegmentSeconds}s.");
            }

            if (index == segmentPaths.Length - 1 &&
                (packetDuration <= 0 ||
                 packetDuration > RecoverySegmentSeconds + 0.10))
            {
                throw new InvalidDataException(
                    $"Final segment video timeline was {packetDuration:0.######}s; expected a bounded tail.");
            }

            PacketTimeline? audioPackets = null;
            if (includeAudio)
            {
                audioPackets = await ReadPacketsAsync(
                        ffprobe,
                        segmentPath,
                        "a:0",
                        cancellationToken)
                    .ConfigureAwait(false);
                ValidateAudioTimeline(segmentPath, audioPackets);
                segmentAudioPackets += audioPackets.Count;
            }

            segmentVideoFrames = checked(segmentVideoFrames + media.VideoFrameCount);
            segmentTimelineSeconds += packetDuration;
            segmentResults.Add(new
            {
                Path = segmentPath,
                Bytes = new FileInfo(segmentPath).Length,
                media.DurationSeconds,
                VideoPacketDurationSeconds = packetDuration,
                media.VideoFrameCount,
                AudioPacketCount = audioPackets?.Count ?? 0
            });
        }

        if (segmentVideoFrames != directMedia.VideoFrameCount)
        {
            throw new InvalidDataException(
                $"Tee packet partition mismatch: segments contain {segmentVideoFrames} video frames, " +
                $"the continuous direct MP4 contains {directMedia.VideoFrameCount}.");
        }

        if (includeAudio &&
            directAudioPackets is not null &&
            segmentAudioPackets != directAudioPackets.Count)
        {
            throw new InvalidDataException(
                $"Tee packet partition mismatch: segments contain {segmentAudioPackets} audio packets, " +
                $"the continuous direct MP4 contains {directAudioPackets.Count}.");
        }

        var directVideoDuration = directVideoPackets.LastEndTimeSeconds -
                                  directVideoPackets.FirstPresentationTimeSeconds;
        var segmentTimelineTolerance = Math.Max(
            0.10,
            segmentPaths.Length * 2d / FramesPerSecond);
        if (Math.Abs(segmentTimelineSeconds - directVideoDuration) >
            segmentTimelineTolerance)
        {
            throw new InvalidDataException(
                $"Segment timelines total {segmentTimelineSeconds:0.######}s while the direct MP4 " +
                $"contains {directVideoDuration:0.######}s (tolerance " +
                $"{segmentTimelineTolerance:0.######}s).");
        }

        var directBoxTypes = await ReadTopLevelMp4BoxesAsync(
                directRecordingPath,
                cancellationToken)
            .ConfigureAwait(false);
        ValidateFinalFragmentedMp4Boxes(directRecordingPath, directBoxTypes);

        var reportPath = Path.Combine(runDirectory, "recorder-fast-path-report.json");
        await File.WriteAllTextAsync(
                reportPath,
                JsonSerializer.Serialize(
                    new
                    {
                        SchemaVersion = 1,
                        GeneratedUtc = DateTimeOffset.UtcNow,
                        RequestedSeconds = requestedSeconds,
                        IncludeAudio = includeAudio,
                        FramesPerSecond,
                        Width,
                        Height,
                        Arguments = arguments,
                        CaptureWallMilliseconds = capture.CaptureWallTime.TotalMilliseconds,
                        TrailerCloseMilliseconds = capture.TrailerCloseTime.TotalMilliseconds,
                        capture.FinalFrame,
                        capture.FinalOutputTimeMicroseconds,
                        DirectRecording = new
                        {
                            Path = directRecordingPath,
                            Bytes = new FileInfo(directRecordingPath).Length,
                            directMedia.StartTimeSeconds,
                            directMedia.DurationSeconds,
                            directMedia.VideoFrameCount,
                            VideoPacketDurationSeconds = directVideoDuration,
                            AudioPacketCount = directAudioPackets?.Count ?? 0,
                            TopLevelBoxes = directBoxTypes
                        },
                        Segments = segmentResults
                    },
                    new JsonSerializerOptions { WriteIndented = true }),
                cancellationToken)
            .ConfigureAwait(false);

        Console.WriteLine(
            $"PASS Recorder fast path: {directMedia.VideoFrameCount} retained frames / " +
            $"{directDuration:0.###}s Start-to-Stop, {segmentPaths.Length} authoritative MKVs, " +
            $"one bounded-fragment MP4, trailer close " +
            $"{capture.TrailerCloseTime.TotalMilliseconds:0.0}ms, " +
            $"{(includeAudio ? "audio verified" : "silent output verified")}.");
        Console.WriteLine($"Report: {reportPath}");
    }

    private static IReadOnlyList<string> BuildArguments(
        string segmentDirectory,
        string directRecordingPath,
        bool includeAudio)
    {
        var display = new DisplayOption(
            "synthetic-recorder-fast-path",
            "Synthetic Recorder fast path",
            0,
            0,
            Width,
            Height,
            IsPrimary: true);
        var configuration = new CaptureConfiguration(
            display,
            ResolutionOption.All.Single(option => option.Id == "source"),
            FramesPerSecond,
            TimeSpan.Zero,
            CaptureCursor: false,
            CaptureSystemAudio: includeAudio,
            OutputAudioDevice: includeAudio
                ? new AudioDeviceOption("synthetic-audio", "Synthetic audio")
                : null,
            CaptureMicrophone: false,
            MicrophoneDevice: null,
            SaveDirectory: Path.GetDirectoryName(directRecordingPath)!)
        {
            SessionMode = CaptureSessionMode.Recording
        };
        AudioInputSpecification[] audioInputs = includeAudio
            ? [new("synthetic-audio", "s16le", 48_000, 1)]
            : [];
        var productionArguments = FfmpegArgumentBuilder.BuildCaptureArguments(
            configuration,
            audioInputs,
            VideoEncodingStrategy.SoftwareGdi,
            segmentDirectory,
            directRecordingPath: directRecordingPath);
        var outputStart = -1;
        for (var index = 0; index < productionArguments.Count - 1; index++)
        {
            if (productionArguments[index] == "-map" &&
                productionArguments[index + 1] == "0:v:0")
            {
                outputStart = index;
                break;
            }
        }

        if (outputStart < 0)
        {
            throw new InvalidDataException(
                "Production capture arguments did not expose the expected video output map.");
        }

        List<string> arguments =
        [
            "-hide_banner",
            "-loglevel", "warning",
            "-nostats",
            "-stats_period", "0.10",
            "-progress", "pipe:1",
            "-re",
            "-f", "lavfi",
            "-i", $"testsrc2=size={Width}x{Height}:rate={FramesPerSecond}"
        ];
        if (includeAudio)
        {
            arguments.AddRange(
            [
                "-re",
                "-f", "lavfi",
                "-i", "sine=frequency=880:sample_rate=48000"
            ]);
        }

        arguments.AddRange(productionArguments.Skip(outputStart));
        return arguments;
    }

    private static async Task<CaptureRunResult> RunUntilProgressAsync(
        string ffmpeg,
        IReadOnlyList<string> arguments,
        TimeSpan targetDuration,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpeg,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException(
                "Windows could not start FFmpeg for the Recorder fast-path smoke.");
        }

        process.StandardInput.AutoFlush = true;
        var progress = new CaptureProgressState(targetDuration);
        var progressTask = PumpProgressAsync(
            process.StandardOutput,
            progress,
            cancellationToken);
        var diagnosticsTask = process.StandardError.ReadToEndAsync(cancellationToken);
        var captureTimer = Stopwatch.StartNew();
        try
        {
            using var targetTimeout = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            targetTimeout.CancelAfter(targetDuration + TimeSpan.FromSeconds(15));
            var exitedBeforeTarget = process.WaitForExitAsync(targetTimeout.Token);
            var completed = await Task.WhenAny(
                    progress.TargetReached.Task,
                    exitedBeforeTarget)
                .ConfigureAwait(false);
            if (completed == exitedBeforeTarget)
            {
                await exitedBeforeTarget.ConfigureAwait(false);
                var diagnostics = await diagnosticsTask.ConfigureAwait(false);
                throw new InvalidDataException(
                    $"FFmpeg exited before the target timeline with code {process.ExitCode}: " +
                    diagnostics.Trim());
            }

            await progress.TargetReached.Task.WaitAsync(targetTimeout.Token)
                .ConfigureAwait(false);
            captureTimer.Stop();

            var trailerTimer = Stopwatch.StartNew();
            await process.StandardInput.WriteLineAsync("q")
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            await process.StandardInput.FlushAsync(cancellationToken)
                .ConfigureAwait(false);
            using var closeTimeout = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            closeTimeout.CancelAfter(MaximumTrailerCloseTime);
            try
            {
                await process.WaitForExitAsync(closeTimeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                TryKill(process);
                throw new TimeoutException(
                    $"FFmpeg did not close the fragmented MP4 trailer within " +
                    $"{MaximumTrailerCloseTime.TotalSeconds:0}s.");
            }

            trailerTimer.Stop();
            await progressTask.ConfigureAwait(false);
            var standardError = await diagnosticsTask.ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                throw new InvalidDataException(
                    $"Recorder tee capture exited with code {process.ExitCode}: " +
                    standardError.Trim());
            }

            if (standardError.Contains(
                    "FIFO queue full",
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "The failure-isolated direct MP4 FIFO overflowed during the smoke; " +
                    "the fast path must be invalidated even though FFmpeg exited successfully.");
            }

            return new CaptureRunResult(
                captureTimer.Elapsed,
                trailerTimer.Elapsed,
                progress.FinalFrame,
                progress.FinalOutputTimeMicroseconds,
                progress.ProgressEnded);
        }
        finally
        {
            TryKill(process);
        }
    }

    private static async Task PumpProgressAsync(
        StreamReader reader,
        CaptureProgressState state,
        CancellationToken cancellationToken)
    {
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            var separator = line.IndexOf('=');
            if (separator <= 0 || separator == line.Length - 1)
            {
                continue;
            }

            var key = line[..separator];
            var value = line[(separator + 1)..];
            if (key.Equals("frame", StringComparison.Ordinal) &&
                long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var frame))
            {
                state.FinalFrame = frame;
            }
            else if (key.Equals("out_time_us", StringComparison.Ordinal) &&
                     long.TryParse(
                         value,
                         NumberStyles.None,
                         CultureInfo.InvariantCulture,
                         out var outputTimeMicroseconds))
            {
                state.FinalOutputTimeMicroseconds = outputTimeMicroseconds;
                if (outputTimeMicroseconds >= state.TargetMicroseconds)
                {
                    state.TargetReached.TrySetResult();
                }
            }
            else if (key.Equals("progress", StringComparison.Ordinal) &&
                     value.Equals("end", StringComparison.Ordinal))
            {
                state.ProgressEnded = true;
            }
        }
    }

    private static async Task<FastPathMediaInfo> ReadMediaAsync(
        string ffprobe,
        string mediaPath,
        CancellationToken cancellationToken)
    {
        var output = await RunProbeAsync(
                ffprobe,
                [
                    "-v", "error",
                    "-count_frames",
                    "-show_entries",
                    "format=start_time,duration:" +
                    "stream=codec_type,codec_name,width,height,avg_frame_rate,nb_read_frames",
                    "-of", "json",
                    mediaPath
                ],
                cancellationToken)
            .ConfigureAwait(false);
        using var document = JsonDocument.Parse(output);
        var format = document.RootElement.GetProperty("format");
        var startTimeSeconds = ParseRequiredDouble(format, "start_time", mediaPath);
        var durationSeconds = ParseRequiredDouble(format, "duration", mediaPath);
        JsonElement? video = null;
        var audioStreams = 0;
        foreach (var stream in document.RootElement.GetProperty("streams").EnumerateArray())
        {
            var codecType = stream.GetProperty("codec_type").GetString();
            if (codecType == "video" && video is null)
            {
                video = stream;
            }
            else if (codecType == "audio")
            {
                audioStreams++;
            }
        }

        if (video is not { } videoStream ||
            !long.TryParse(
                videoStream.GetProperty("nb_read_frames").GetString(),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var frameCount) ||
            frameCount <= 0)
        {
            throw new InvalidDataException(
                $"FFprobe did not return a valid video frame count for '{mediaPath}'.");
        }

        return new FastPathMediaInfo(
            startTimeSeconds,
            durationSeconds,
            videoStream.GetProperty("width").GetInt32(),
            videoStream.GetProperty("height").GetInt32(),
            ParseFraction(videoStream.GetProperty("avg_frame_rate").GetString(), mediaPath),
            frameCount,
            audioStreams);
    }

    private static async Task<PacketTimeline> ReadPacketsAsync(
        string ffprobe,
        string mediaPath,
        string streamSelector,
        CancellationToken cancellationToken)
    {
        var output = await RunProbeAsync(
                ffprobe,
                [
                    "-v", "error",
                    "-select_streams", streamSelector,
                    "-show_packets",
                    "-show_entries", "packet=pts_time,dts_time,duration_time,flags",
                    "-of", "json",
                    mediaPath
                ],
                cancellationToken)
            .ConfigureAwait(false);
        using var document = JsonDocument.Parse(output);
        var packets = new List<PacketSample>();
        foreach (var packet in document.RootElement.GetProperty("packets").EnumerateArray())
        {
            var presentationTime = ParseRequiredDouble(packet, "pts_time", mediaPath);
            var decodeTime = ParseRequiredDouble(packet, "dts_time", mediaPath);
            var duration = packet.TryGetProperty("duration_time", out var durationElement) &&
                           double.TryParse(
                               durationElement.GetString(),
                               NumberStyles.Float,
                               CultureInfo.InvariantCulture,
                               out var parsedDuration)
                ? Math.Max(0, parsedDuration)
                : 0;
            var flags = packet.TryGetProperty("flags", out var flagsElement)
                ? flagsElement.GetString()
                : null;
            packets.Add(new PacketSample(
                presentationTime,
                decodeTime,
                duration,
                flags?.Contains('K') == true));
        }

        if (packets.Count < 2)
        {
            throw new InvalidDataException(
                $"FFprobe returned only {packets.Count} {streamSelector} packet(s) for '{mediaPath}'.");
        }

        var maximumPresentationGapAfterStartup = 0d;
        for (var index = 1; index < packets.Count; index++)
        {
            if (packets[index].PresentationTimeSeconds + 0.000001 <
                    packets[index - 1].PresentationTimeSeconds ||
                packets[index].DecodeTimeSeconds + 0.000001 <
                    packets[index - 1].DecodeTimeSeconds)
            {
                throw new InvalidDataException(
                    $"{streamSelector} timestamps moved backwards at packet {index} in '{mediaPath}'.");
            }

            if (index > 1)
            {
                maximumPresentationGapAfterStartup = Math.Max(
                    maximumPresentationGapAfterStartup,
                    packets[index].PresentationTimeSeconds -
                    packets[index - 1].PresentationTimeSeconds);
            }
        }

        return new PacketTimeline(
            packets,
            packets[1].PresentationTimeSeconds - packets[0].PresentationTimeSeconds,
            maximumPresentationGapAfterStartup,
            packets[0].PresentationTimeSeconds,
            packets[^1].PresentationTimeSeconds + packets[^1].DurationSeconds);
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
                $"FFprobe failed for '{arguments[^1]}': {error.Trim()}");
        }

        return output;
    }

    private static void ValidateVideoTimeline(
        string path,
        FastPathMediaInfo media,
        PacketTimeline packets,
        int expectedAudioStreams)
    {
        var averageFrameRateTolerance = media.DurationSeconds <= 2.15
            ? 1.0
            : 0.5;
        if (media.Width != Width ||
            media.Height != Height ||
            Math.Abs(media.StartTimeSeconds) > 0.05 ||
            Math.Abs(media.AverageFrameRate - FramesPerSecond) > averageFrameRateTolerance ||
            media.VideoFrameCount != packets.Count ||
            media.AudioStreamCount != expectedAudioStreams)
        {
            throw new InvalidDataException(
                $"Media validation failed for '{path}': start {media.StartTimeSeconds:0.######}s, " +
                $"{media.Width}x{media.Height}, " +
                $"{media.AverageFrameRate:0.###} FPS, {media.VideoFrameCount}/{packets.Count} frames, " +
                $"{media.AudioStreamCount} audio stream(s).");
        }

        var maximumAllowedGap = 1.7 / FramesPerSecond;
        // AAC priming can make the MP4 muxer hold the first video packet for
        // one audio frame (about 21ms) before the stable 60 FPS cadence begins.
        // Bound that one startup compensation separately so the smoke still
        // rejects recurring stalls or the multi-second freezes it targets.
        if (packets.InitialPresentationGapSeconds > 0.05 ||
            packets.MaximumPresentationGapAfterStartupSeconds > maximumAllowedGap)
        {
            throw new InvalidDataException(
                $"Video timeline in '{path}' contains a " +
                $"{packets.InitialPresentationGapSeconds * 1000:0.###}ms startup gap and " +
                $"{packets.MaximumPresentationGapAfterStartupSeconds * 1000:0.###}ms " +
                $"maximum steady-state gap; the steady 60 FPS bound is " +
                $"{maximumAllowedGap * 1000:0.###}ms.");
        }
    }

    private static void ValidateAudioTimeline(string path, PacketTimeline packets)
    {
        if (packets.InitialPresentationGapSeconds > 0.05 ||
            packets.MaximumPresentationGapAfterStartupSeconds > 0.05)
        {
            throw new InvalidDataException(
                $"Audio timeline in '{path}' contains a " +
                $"{Math.Max(packets.InitialPresentationGapSeconds, packets.MaximumPresentationGapAfterStartupSeconds) * 1000:0.###}ms gap.");
        }
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
                size = checked((long)BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(8, 8)));
                headerBytes = 16;
            }
            else
            {
                size = shortSize == 0 ? stream.Length - boxStart : shortSize;
            }

            if (size < headerBytes || boxStart + size > stream.Length)
            {
                throw new InvalidDataException(
                    $"Invalid top-level MP4 box '{type}' with size {size} at offset {boxStart}.");
            }

            boxes.Add(type);
            stream.Position = boxStart + size;
        }

        return boxes;
    }

    private static void ValidateFinalFragmentedMp4Boxes(
        string path,
        IReadOnlyList<string> boxes)
    {
        var ftypIndex = FindBoxIndex(boxes, "ftyp");
        var moovIndex = FindBoxIndex(boxes, "moov");
        var firstFragmentIndex = FindBoxIndex(boxes, "moof");
        var fileTypeCount = boxes.Count(type => type == "ftyp");
        var movieMetadataCount = boxes.Count(type => type == "moov");
        var fragmentCount = boxes.Count(type => type == "moof");
        var mediaDataCount = boxes.Count(type => type == "mdat");
        if (ftypIndex != 0 ||
            fileTypeCount != 1 ||
            movieMetadataCount != 1 ||
            moovIndex <= ftypIndex ||
            firstFragmentIndex <= moovIndex ||
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
                $"Fragmented MP4 '{path}' did not keep a valid ftyp/moov/(moof/mdat)+/mfra layout: " +
                string.Join(", ", boxes));
        }
    }

    private static int FindBoxIndex(IReadOnlyList<string> boxes, string type)
    {
        for (var index = 0; index < boxes.Count; index++)
        {
            if (boxes[index].Equals(type, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return -1;
    }

    private static void AssertContiguousSegmentNames(IReadOnlyList<string> segmentPaths)
    {
        for (var index = 0; index < segmentPaths.Count; index++)
        {
            var expected = $"segment-{index:D9}.mkv";
            var actual = Path.GetFileName(segmentPaths[index]);
            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"Authoritative segment sequence expected '{expected}', found '{actual}'.");
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
        var parts = value?.Split('/');
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

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(1_000);
            }
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // The process already exited or Windows denied a redundant cleanup.
        }
    }

    private sealed class CaptureProgressState(TimeSpan targetDuration)
    {
        internal long TargetMicroseconds { get; } =
            checked((long)Math.Round(targetDuration.TotalMilliseconds * 1000));

        internal TaskCompletionSource TargetReached { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal long FinalFrame { get; set; }

        internal long FinalOutputTimeMicroseconds { get; set; }

        internal bool ProgressEnded { get; set; }
    }

    private sealed record CaptureRunResult(
        TimeSpan CaptureWallTime,
        TimeSpan TrailerCloseTime,
        long FinalFrame,
        long FinalOutputTimeMicroseconds,
        bool ProgressEnded);

    private sealed record FastPathMediaInfo(
        double StartTimeSeconds,
        double DurationSeconds,
        int Width,
        int Height,
        double AverageFrameRate,
        long VideoFrameCount,
        int AudioStreamCount);

    private sealed record PacketSample(
        double PresentationTimeSeconds,
        double DecodeTimeSeconds,
        double DurationSeconds,
        bool IsKeyFrame);

    private sealed record PacketTimeline(
        IReadOnlyList<PacketSample> Packets,
        double InitialPresentationGapSeconds,
        double MaximumPresentationGapAfterStartupSeconds,
        double FirstPresentationTimeSeconds,
        double LastEndTimeSeconds)
    {
        internal int Count => Packets.Count;

        internal IEnumerable<PacketSample> Where(Func<PacketSample, bool> predicate) =>
            Packets.Where(predicate);
    }
}
