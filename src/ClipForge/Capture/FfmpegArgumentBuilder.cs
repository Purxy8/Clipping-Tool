using System.Globalization;
using System.Text;
using ClipForge.Models;

namespace ClipForge.Capture;

internal static class FfmpegArgumentBuilder
{
    internal const int SegmentSeconds = 2;
    // Recorder recovery does not need Instant Replay's two-second trim
    // granularity. Ten-second closed MKVs reduce a 24-hour session from 43,200
    // files to 8,640 while limiting unclean-exit fallback loss to the currently
    // open ten-second segment. This does not affect the live MP4's exact Stop.
    internal const int RecordingRecoverySegmentSeconds = 10;
    // Keep Recorder's publishable MP4 permanently fragmented. A flat MP4 either
    // has to rewrite the complete media payload at Stop or reserve tens of MiB
    // of zero-filled index space up front. Thirty-second fragments keep a
    // representative 12-hour/60 FPS/AAC recording at only 1,441 fragments:
    // Windows Media Foundation opened and sought it in under one second, while
    // FFmpeg still closes it with metadata-only work and can repair it after an
    // unclean exit. Closed Matroska recovery segments remain authoritative.
    internal const int DirectRecordingFragmentDurationMicroseconds = 30_000_000;
    internal const int DirectRecordingFifoQueuePackets = 512;
    internal const int VideoInputQueuePackets = 2;
    internal const int ScaledVideoInputQueuePackets = 4;
    internal const int CompatibilityVideoInputQueuePackets = 8;
    internal const int AudioInputQueuePackets = 64;
    internal const int CaptureProbeSeconds = 3;

    public static IReadOnlyList<string> BuildCaptureArguments(
        CaptureConfiguration configuration,
        IReadOnlyList<AudioInputSpecification> audioInputs,
        string segmentDirectory) =>
        BuildCaptureArguments(
            configuration,
            audioInputs,
            VideoEncodingStrategy.SoftwareGdi,
            segmentDirectory);

    public static IReadOnlyList<string> BuildCaptureArguments(
        CaptureConfiguration configuration,
        IReadOnlyList<AudioInputSpecification> audioInputs,
        VideoEncodingStrategy encodingStrategy,
        string segmentDirectory,
        int segmentStartNumber = 0,
        CapturePerformanceProfile performanceProfile = CapturePerformanceProfile.LowImpact,
        string? directRecordingPath = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(audioInputs);
        ArgumentNullException.ThrowIfNull(encodingStrategy);
        ArgumentException.ThrowIfNullOrWhiteSpace(segmentDirectory);
        if (segmentStartNumber < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(segmentStartNumber));
        }

        if (directRecordingPath is not null &&
            string.IsNullOrWhiteSpace(directRecordingPath))
        {
            throw new ArgumentException(
                "The direct recording path cannot be empty.",
                nameof(directRecordingPath));
        }

        if (configuration.FramesPerSecond is < 1 or > 240)
        {
            throw new ArgumentOutOfRangeException(
                nameof(configuration),
                "The frame rate must be between 1 and 240 frames per second.");
        }

        var display = configuration.Display;
        if (display.Width < 2 || display.Height < 2)
        {
            throw new ArgumentException("The selected display has invalid dimensions.", nameof(configuration));
        }

        var arguments = new List<string>
        {
            "-hide_banner",
            "-loglevel", "warning",
            "-nostats",
            "-stats_period", "0.25",
            "-progress", "pipe:1"
        };

        if (UsesDirectWindowsGraphicsHardwarePath(encodingStrategy))
        {
            // gfxcapture already owns a two-frame D3D11 pool. Keep FFmpeg's
            // simple and complex filter executors single-threaded so a live
            // hardware capture cannot create extra worker pools that compete
            // with the foreground game and DWM for long-running sessions.
            arguments.AddRange([
                "-filter_threads", "1",
                "-filter_complex_threads", "1"
            ]);
        }

        AddVideoInput(arguments, configuration, encodingStrategy, performanceProfile);

        foreach (var audioInput in audioInputs)
        {
            arguments.AddRange(
            [
                "-thread_queue_size", Invariant(AudioInputQueuePackets),
                "-f", audioInput.FfmpegSampleFormat,
                "-ar", Invariant(audioInput.SampleRate),
                "-ac", Invariant(audioInput.Channels),
                "-i", audioInput.PipePath
            ]);
        }

        arguments.AddRange(["-map", "0:v:0"]);
        if (encodingStrategy.CaptureBackend == DesktopCaptureBackend.Gdi)
        {
            var encoderPixelFormat = encodingStrategy.Encoder == VideoEncoderKind.SoftwareX264
                ? "yuv420p"
                : "nv12";
            arguments.AddRange([
                "-vf",
                $"{BuildVideoFilter(configuration)},format={encoderPixelFormat},setpts=PTS-STARTPTS"
            ]);
        }
        else
        {
            // Each WGC process owns a fresh clock. Normalize that generation at
            // capture time so a delayed first D3D frame cannot retain a positive
            // source offset while audio begins at zero.
            arguments.AddRange(["-vf", "setpts=PTS-STARTPTS"]);
        }

        if (audioInputs.Count == 0)
        {
            arguments.Add("-an");
        }
        else
        {
            arguments.AddRange(["-filter_complex", BuildAudioFilter(audioInputs.Count), "-map", "[mixed_audio]"]);
        }

        var keyFrameInterval = checked(configuration.FramesPerSecond * SegmentSeconds);
        var outputSegmentSeconds = GetOutputSegmentSeconds(
            configuration.SessionMode);
        AddEncoderArguments(arguments, encodingStrategy);
        if (directRecordingPath is not null)
        {
            // The tee muxer cannot infer the child muxers' global-header
            // requirement before the encoder opens. Both Matroska and MP4 need
            // the codec configuration up front, so make that contract explicit.
            arguments.AddRange(["-flags", "+global_header"]);
        }

        // Normalize encoded packet clocks before either the Replay segment
        // muxer or Recorder tee sees them. AAC encoder priming and a WGC frame
        // held while its pool starts can otherwise stretch the opening video
        // packet even though the remainder reports perfect CFR. Capture video
        // has no B-frames, so packet order is presentation order.
        var cfrPacketTimestamps =
            $"ts=N/({Invariant(configuration.FramesPerSecond)}*TB):" +
            $"duration=1/({Invariant(configuration.FramesPerSecond)}*TB)";
        var audioPacketTimestamps =
            "ts=N*1024/(SR*TB):duration=1024/(SR*TB)";
        arguments.AddRange(["-bsf:v", $"setts={cfrPacketTimestamps}"]);
        if (audioInputs.Count > 0)
        {
            arguments.AddRange(["-bsf:a", $"setts={audioPacketTimestamps}"]);
        }

        arguments.AddRange(
        [
            "-fps_mode", "cfr",
            "-r", Invariant(configuration.FramesPerSecond),
            "-g", Invariant(keyFrameInterval),
            "-keyint_min", Invariant(keyFrameInterval),
            "-force_key_frames", $"expr:gte(t,n_forced*{SegmentSeconds})"
        ]);

        if (audioInputs.Count > 0)
        {
            arguments.AddRange(["-c:a", "aac", "-b:a", "192k", "-ar", "48000", "-ac", "2"]);
        }

        var segmentTimeDelta = 1d / (2 * configuration.FramesPerSecond);
        var segmentTimeDeltaArgument =
            segmentTimeDelta.ToString("0.########", CultureInfo.InvariantCulture);
        if (directRecordingPath is null)
        {
            arguments.AddRange(
            [
                "-f", "segment",
                "-segment_time", Invariant(outputSegmentSeconds),
                "-segment_time_delta", segmentTimeDeltaArgument,
                "-reset_timestamps", "1",
                "-segment_format", "matroska",
                "-segment_start_number", Invariant(segmentStartNumber),
                "-y",
                Path.Combine(segmentDirectory, "segment-%09d.mkv")
            ]);
        }
        else
        {
            var segmentPattern = EscapeTeeOutputPath(
                Path.Combine(segmentDirectory, "segment-%09d.mkv"),
                "%09d");
            var recordingPath = EscapeTeeLiteralOutputPath(
                directRecordingPath);
            // The publishable Recorder output is one continuous fragmented MP4
            // from Start through Stop. Splitting off a two-second warmup either
            // dropped the beginning of every long recording or required a full
            // 40-GB rewrite to put it back. A bounded FIFO still isolates this
            // optional writer from the authoritative recovery MKVs, while the
            // permanent fMP4 closes with metadata-only work and can be renamed
            // atomically on the normal same-volume path.
            var teeOutput =
                $"[f=segment:onfail=abort:segment_time={outputSegmentSeconds}:" +
                $"segment_time_delta={segmentTimeDeltaArgument}:reset_timestamps=1:" +
                $"segment_format=matroska:segment_start_number={segmentStartNumber}]" +
                $"{segmentPattern}|" +
                "[f=mp4:onfail=ignore:use_fifo=1:" +
                $"fifo_options=queue_size={DirectRecordingFifoQueuePackets}\\\\:" +
                "drop_pkts_on_overflow=1:" +
                "movflags=+empty_moov+default_base_moof:" +
                $"frag_duration={DirectRecordingFragmentDurationMicroseconds}:" +
                "flush_packets=1]" +
                recordingPath;
            arguments.AddRange(["-y", "-f", "tee", teeOutput]);
        }

        return arguments;
    }

    internal static int GetOutputSegmentSeconds(CaptureSessionMode sessionMode) =>
        sessionMode switch
        {
            CaptureSessionMode.InstantReplay => SegmentSeconds,
            CaptureSessionMode.Recording => RecordingRecoverySegmentSeconds,
            _ => throw new ArgumentOutOfRangeException(nameof(sessionMode))
        };

    private static string EscapeTeeOutputPath(
        string path,
        string muxerFormatToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(muxerFormatToken);
        var tokenIndex = path.LastIndexOf(
            muxerFormatToken,
            StringComparison.Ordinal);
        if (tokenIndex < 0)
        {
            throw new ArgumentException(
                "The tee output path did not contain its muxer format token.",
                nameof(path));
        }

        var escaped = new StringBuilder(path.Length + 8);
        for (var index = 0; index < path.Length; index++)
        {
            if (index == tokenIndex)
            {
                escaped.Append(muxerFormatToken);
                index += muxerFormatToken.Length - 1;
                continue;
            }

            escaped.Append(path[index] switch
            {
                '\\' => '/',
                '\'' => "\\'",
                // The segment muxer treats every percent sign as a filename
                // template introducer. Doubling literal signs preserves valid
                // user directories such as "100%-captures" while the one
                // generated frame-number token above remains active.
                '%' => "%%",
                _ => path[index].ToString()
            });
        }

        return escaped.ToString();
    }

    private static string EscapeTeeLiteralOutputPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var escaped = new StringBuilder(path.Length + 4);
        foreach (var character in path)
        {
            escaped.Append(character switch
            {
                '\\' => '/',
                '\'' => "\\'",
                _ => character.ToString()
            });
        }

        return escaped.ToString();
    }

    internal static string GetDirectRecordingPartPattern(string directRecordingPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directRecordingPath);
        return BuildDirectRecordingPartPath(directRecordingPath, "%01d");
    }

    internal static string GetDirectRecordingPartPath(
        string directRecordingPath,
        int partNumber)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directRecordingPath);
        if (partNumber < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(partNumber));
        }

        return BuildDirectRecordingPartPath(
            directRecordingPath,
            partNumber.ToString(CultureInfo.InvariantCulture));
    }

    private static string BuildDirectRecordingPartPath(
        string directRecordingPath,
        string partName)
    {
        var extension = Path.GetExtension(directRecordingPath);
        var partExtension = $".part-{partName}{extension}";
        return Path.ChangeExtension(directRecordingPath, partExtension);
    }

    public static IReadOnlyList<string> BuildEncoderProbeArguments(
        VideoEncodingStrategy encodingStrategy,
        int width,
        int height,
        int framesPerSecond)
    {
        ArgumentNullException.ThrowIfNull(encodingStrategy);
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        if (framesPerSecond is < 1 or > 240)
        {
            throw new ArgumentOutOfRangeException(nameof(framesPerSecond));
        }

        width -= width % 2;
        height -= height % 2;
        var arguments = new List<string>
        {
            "-hide_banner",
            "-loglevel", "error",
            "-nostdin",
            "-f", "lavfi",
            "-i", $"color=c=black:s={width}x{height}:r={framesPerSecond}",
            "-frames:v", "2",
            "-an"
        };
        AddEncoderArguments(arguments, encodingStrategy);
        arguments.AddRange(["-f", "null", "NUL"]);
        return arguments;
    }

    public static IReadOnlyList<string> BuildGraphicsCaptureProbeArguments(
        CaptureConfiguration configuration,
        VideoEncodingStrategy encodingStrategy,
        CapturePerformanceProfile performanceProfile =
            CapturePerformanceProfile.LowImpact)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(encodingStrategy);
        if (encodingStrategy.CaptureBackend != DesktopCaptureBackend.WindowsGraphicsCapture)
        {
            throw new ArgumentException(
                "A graphics-capture probe requires the Windows Graphics Capture backend.",
                nameof(encodingStrategy));
        }

        var arguments = new List<string>
        {
            "-hide_banner",
            "-loglevel", "error",
            "-nostdin",
            "-nostats",
            "-stats_period", "0.25",
            "-progress", "pipe:1"
        };
        if (UsesDirectWindowsGraphicsHardwarePath(encodingStrategy))
        {
            arguments.AddRange([
                "-filter_threads", "1",
                "-filter_complex_threads", "1"
            ]);
        }

        AddVideoInput(
            arguments,
            configuration,
            encodingStrategy,
            performanceProfile);
        var probeFrames = checked(
            configuration.FramesPerSecond * CaptureProbeSeconds);
        // Exercise the same timestamp-normalization graph as live capture. A
        // capability probe must not approve a simpler D3D11-to-encoder path than
        // the one that will run continuously.
        arguments.AddRange(["-vf", "setpts=PTS-STARTPTS"]);
        // Live replay is constant-frame-rate. Without this production output
        // contract, a quiet desktop or an application that repaints below the
        // requested rate makes WGC's change-driven input appear slow. Exercise
        // the complete three-second graph for Source as well as scaled presets.
        arguments.AddRange([
            "-fps_mode", "cfr",
            "-r", Invariant(configuration.FramesPerSecond)
        ]);

        arguments.AddRange([
            "-frames:v", Invariant(probeFrames),
            "-an"
        ]);
        AddEncoderArguments(arguments, encodingStrategy);
        arguments.AddRange(["-f", "null", "NUL"]);
        return arguments;
    }

    public static IReadOnlyList<string> BuildGdiCaptureProbeArguments(
        CaptureConfiguration configuration,
        VideoEncodingStrategy encodingStrategy)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(encodingStrategy);
        if (encodingStrategy.CaptureBackend != DesktopCaptureBackend.Gdi)
        {
            throw new ArgumentException(
                "A GDI capture probe requires the GDI capture backend.",
                nameof(encodingStrategy));
        }

        if (configuration.FramesPerSecond is < 1 or > 240)
        {
            throw new ArgumentOutOfRangeException(
                nameof(configuration),
                "The frame rate must be between 1 and 240 frames per second.");
        }

        var arguments = new List<string>
        {
            "-hide_banner",
            "-loglevel", "error",
            "-nostdin",
            "-nostats",
            "-stats_period", "0.25",
            "-progress", "pipe:1"
        };
        AddVideoInput(
            arguments,
            configuration,
            encodingStrategy);

        var encoderPixelFormat =
            encodingStrategy.Encoder == VideoEncoderKind.SoftwareX264
                ? "yuv420p"
                : "nv12";
        arguments.AddRange([
            "-vf",
            $"{BuildVideoFilter(configuration)},format={encoderPixelFormat},setpts=PTS-STARTPTS"
        ]);

        AddEncoderArguments(arguments, encodingStrategy);
        var keyFrameInterval = checked(
            configuration.FramesPerSecond * SegmentSeconds);
        var probeFrames = checked(
            configuration.FramesPerSecond * CaptureProbeSeconds);
        arguments.AddRange([
            "-fps_mode", "cfr",
            "-r", Invariant(configuration.FramesPerSecond),
            "-g", Invariant(keyFrameInterval),
            "-keyint_min", Invariant(keyFrameInterval),
            "-force_key_frames", $"expr:gte(t,n_forced*{SegmentSeconds})",
            "-frames:v", Invariant(probeFrames),
            "-an",
            "-f", "null", "NUL"
        ]);
        return arguments;
    }

    public static IReadOnlyList<string> BuildConcatArguments(
        string manifestPath,
        string outputPath,
        TimeSpan trimFromStart,
        TimeSpan requestedDuration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        if (trimFromStart < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(trimFromStart));
        }

        if (requestedDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(requestedDuration));
        }

        var arguments = new List<string>
        {
            "-hide_banner",
            "-loglevel", "warning",
            "-nostdin",
            "-f", "concat",
            "-safe", "0",
            "-i", manifestPath
        };

        if (trimFromStart > TimeSpan.Zero)
        {
            arguments.AddRange(["-ss", FormatTimestamp(trimFromStart)]);
        }

        arguments.AddRange(
        [
            "-t", FormatTimestamp(requestedDuration),
            "-map", "0:v:0",
            "-map", "0:a?"
        ]);

        if (trimFromStart > TimeSpan.Zero)
        {
            // Starting inside a two-second GOP is not safe with stream copy. This
            // slower path is only used for non-preset durations; normal UI presets
            // align to whole segments and remain a fast remux.
            arguments.AddRange(
            [
                "-c:v", "libx264",
                "-preset", "veryfast",
                "-crf", "23",
                "-pix_fmt", "yuv420p",
                "-c:a", "aac",
                "-b:a", "192k"
            ]);
        }
        else
        {
            arguments.AddRange(["-c", "copy"]);
        }

        arguments.AddRange(
        [
            "-avoid_negative_ts", "make_zero",
            "-y",
            outputPath
        ]);

        return arguments;
    }

    public static IReadOnlyList<string> BuildRecordingConcatArguments(
        string manifestPath,
        string outputPath,
        TimeSpan? maximumDuration = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        if (maximumDuration is { } invalidDuration &&
            invalidDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumDuration));
        }

        List<string> arguments =
        [
            "-hide_banner",
            "-loglevel", "warning",
            "-nostdin",
            "-f", "concat",
            "-safe", "0",
            "-i", manifestPath
        ];
        if (maximumDuration is { } duration)
        {
            // The last graceful recovery file may contain the bounded delay
            // between the user's click and FFmpeg consuming q. Cap the muxed
            // output at the click-time timeline instead of publishing those
            // post-click packets.
            arguments.AddRange(["-t", FormatTimestamp(duration)]);
        }

        arguments.AddRange(
        [
            "-map", "0:v:0",
            "-map", "0:a?",
            "-c", "copy",
            "-avoid_negative_ts", "make_zero",
            "-movflags", "+empty_moov+default_base_moof",
            "-frag_duration", Invariant(DirectRecordingFragmentDurationMicroseconds),
            "-f", "mp4",
            "-y",
            outputPath
        ]);
        return arguments;
    }

    public static IReadOnlyList<string> BuildShortRecordingConcatArguments(
        string manifestPath,
        string outputPath,
        int framesPerSecond,
        bool hasAudio)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        if (framesPerSecond is < 1 or > 240)
        {
            throw new ArgumentOutOfRangeException(nameof(framesPerSecond));
        }

        // WGC can hold its first frame slightly longer while the frame pool is
        // starting. Both capture encoders disable B-frames, so packet order is
        // presentation order and the H.264 setts bitstream filter can normalize
        // this tiny startup clip to the requested CFR without decoding or
        // re-encoding a frame.
        var cfrPacketTimestamps =
            $"ts=N/({Invariant(framesPerSecond)}*TB):" +
            $"duration=1/({Invariant(framesPerSecond)}*TB)";
        List<string> arguments =
        [
            "-hide_banner",
            "-loglevel", "warning",
            "-nostdin",
            "-f", "concat",
            "-safe", "0",
            "-i", manifestPath,
            "-map", "0:v:0",
            "-map", "0:a?",
            "-c", "copy",
            "-bsf:v", $"setts={cfrPacketTimestamps}"
        ];
        if (hasAudio)
        {
            // AAC frames contain 1024 samples. Rebuild the packet cadence across
            // the legacy two-part seam so the MP4 muxer never has to clamp an
            // overlapping DTS into a 21-microsecond packet (an audible click).
            arguments.AddRange([
                "-bsf:a",
                "setts=ts=N*1024/(SR*TB):duration=1024/(SR*TB)"
            ]);
        }

        arguments.AddRange(
        [
            "-avoid_negative_ts", "make_zero",
            "-movflags", "+empty_moov+default_base_moof",
            "-frag_duration", Invariant(DirectRecordingFragmentDurationMicroseconds),
            "-f", "mp4",
            "-y",
            outputPath
        ]);
        return arguments;
    }

    public static IReadOnlyList<string> BuildRecordingRecoveryArguments(
        string inputPath,
        string outputPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        return
        [
            "-hide_banner",
            "-loglevel", "warning",
            "-nostdin",
            "-fflags", "+discardcorrupt",
            "-protocol_whitelist", "file",
            "-i", inputPath,
            "-map", "0:v:0",
            "-map", "0:a?",
            "-c", "copy",
            "-shortest",
            "-avoid_negative_ts", "make_zero",
            "-movflags", "+empty_moov+default_base_moof",
            "-frag_duration", Invariant(DirectRecordingFragmentDurationMicroseconds),
            "-f", "mp4",
            "-y",
            outputPath
        ];
    }

    /// <summary>
    /// Builds a fast stream-copy trim when both normalized bounds match ClipForge's fixed GOP
    /// boundaries, or an exact-seek transcode for an arbitrary range. FFmpeg's accurate-seek path
    /// is enabled by default when input-side -ss is combined with video re-encoding: it seeks to
    /// the preceding keyframe, decodes/discards up to the requested source frame, and starts the
    /// new stream there.
    /// </summary>
    internal static IReadOnlyList<string> BuildTrimArguments(
        string inputPath,
        string outputPath,
        TimeSpan start,
        TimeSpan duration,
        bool includeAudio,
        int framesPerSecond,
        VideoEncodingStrategy encodingStrategy,
        bool replayCoexisting = false,
        bool fastStreamCopyVerified = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentNullException.ThrowIfNull(encodingStrategy);
        if (start < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(start));
        }

        if (duration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration));
        }

        if (framesPerSecond is < 1 or > 240)
        {
            throw new ArgumentOutOfRangeException(nameof(framesPerSecond));
        }

        // Arithmetic alignment only makes a range a candidate. ClipTrimService
        // separately verifies the source packet at the requested start is a real
        // keyframe before enabling packet copy.
        var useFastStreamCopy =
            fastStreamCopyVerified &&
            CanUseFastStreamCopyTrim(start, duration);
        var useSoftwareReplayThrottle =
            replayCoexisting &&
            !useFastStreamCopy &&
            !encodingStrategy.IsHardwareEncoder;
        var arguments = new List<string>
        {
            "-hide_banner",
            "-loglevel", "warning",
            "-nostdin",
        };

        if (useSoftwareReplayThrottle)
        {
            // A replay-time trim intentionally progresses at real-time speed on
            // one decoder thread. This prevents a bursty second media workload
            // from starving the live capture graph when hardware encoding is not
            // available. A validated hardware encoder and packet-only stream copy
            // do not need this software fallback throttle.
            arguments.AddRange([
                "-filter_threads", "1",
                "-threads", "1",
                "-readrate", "1"
            ]);
        }

        arguments.AddRange([
            "-protocol_whitelist", "file",
            "-f", "mov",
            "-ss", FormatPreciseTimestamp(start),
            "-i", inputPath,
            "-t", FormatPreciseTimestamp(duration),
            "-map", "0:v:0"
        ]);

        if (includeAudio)
        {
            arguments.AddRange(["-map", "0:a:0?"]);
        }
        else
        {
            arguments.Add("-an");
        }

        arguments.AddRange(
        [
            "-map_metadata", "-1",
            "-map_chapters", "-1",
            "-sn",
            "-dn"
        ]);

        if (useFastStreamCopy)
        {
            arguments.AddRange(["-c", "copy"]);
        }
        else
        {
            AddEncoderArguments(
                arguments,
                encodingStrategy,
                softwareThreadLimit: useSoftwareReplayThrottle ? 1 : 2);
            arguments.AddRange(
            [
                "-pix_fmt", "yuv420p",
                "-g", Invariant(checked(framesPerSecond * SegmentSeconds)),
                "-keyint_min", Invariant(checked(framesPerSecond * SegmentSeconds))
            ]);

            if (includeAudio)
            {
                arguments.AddRange(["-c:a", "aac", "-b:a", "192k", "-ar", "48000", "-ac", "2"]);
            }
        }

        arguments.AddRange(
        [
            "-avoid_negative_ts", "make_zero"
        ]);
        if (!useFastStreamCopy)
        {
            arguments.AddRange(["-movflags", "+faststart"]);
        }

        arguments.AddRange(["-f", "mp4", "-n", outputPath]);
        return arguments;
    }

    internal static bool CanUseFastStreamCopyTrim(TimeSpan start, TimeSpan duration)
    {
        if (start < TimeSpan.Zero ||
            duration <= TimeSpan.Zero ||
            duration.Ticks > TimeSpan.MaxValue.Ticks - start.Ticks)
        {
            return false;
        }

        var segmentTicks = TimeSpan.FromSeconds(SegmentSeconds).Ticks;
        return start.Ticks % segmentTicks == 0 &&
               (start.Ticks + duration.Ticks) % segmentTicks == 0;
    }

    private static string BuildVideoFilter(CaptureConfiguration configuration)
    {
        var output = CaptureGeometry.ResolveOutputSize(configuration);
        if (!output.RequiresScaling)
        {
            return configuration.Display.Width % 2 == 0 && configuration.Display.Height % 2 == 0
                ? "null"
                : "scale=trunc(iw/2)*2:trunc(ih/2)*2:flags=fast_bilinear";
        }

        // GDI supplies native-size BGRA frames in system memory. Downscale only
        // when the preset is genuinely smaller. The geometry is already
        // aspect-correct and even, so no per-frame padding canvas is needed.
        return $"scale={output.Width}:{output.Height}:flags=fast_bilinear";
    }

    private static void AddVideoInput(
        List<string> arguments,
        CaptureConfiguration configuration,
        VideoEncodingStrategy encodingStrategy,
        CapturePerformanceProfile performanceProfile = CapturePerformanceProfile.LowImpact)
    {
        var display = configuration.Display;

        if (encodingStrategy.CaptureBackend == DesktopCaptureBackend.WindowsGraphicsCapture)
        {
            var output = CaptureGeometry.ResolveOutputSize(configuration);
            var queuePackets = output.RequiresScaling ||
                               performanceProfile == CapturePerformanceProfile.Resilient
                ? ScaledVideoInputQueuePackets
                : VideoInputQueuePackets;
            arguments.AddRange([
                // Source/native begins with the two-frame low-impact budget that
                // prevents long-session desktop lag. Fixed downscale and a Source
                // session promoted after measured starvation get two extra frames
                // to absorb short fullscreen GPU stalls without starving CFR.
                "-thread_queue_size", Invariant(queuePackets),
                "-f", "lavfi",
                "-i", BuildGraphicsCaptureFilter(configuration, encodingStrategy)
            ]);
            return;
        }

        arguments.AddRange([
            "-thread_queue_size", Invariant(CompatibilityVideoInputQueuePackets),
            "-f", "gdigrab",
            "-draw_mouse", configuration.CaptureCursor ? "1" : "0",
            "-framerate", Invariant(configuration.FramesPerSecond),
            "-offset_x", Invariant(display.Left),
            "-offset_y", Invariant(display.Top),
            "-video_size", $"{display.Width}x{display.Height}",
            "-i", "desktop"
        ]);
    }

    private static string BuildGraphicsCaptureFilter(
        CaptureConfiguration configuration,
        VideoEncodingStrategy encodingStrategy)
    {
        var output = CaptureGeometry.ResolveOutputSize(configuration);
        var inputFrameRate = ResolveGraphicsCaptureInputFrameRate(
            configuration.FramesPerSecond,
            configuration.Display.RefreshRateHz);
        var filter = $"gfxcapture=monitor_idx={configuration.Display.MonitorIndex}" +
                     $":capture_cursor={(configuration.CaptureCursor ? "1" : "0")}" +
                     $":max_framerate={Invariant(inputFrameRate)}" +
                     ":output_fmt=bgra";

        if (output.RequiresScaling)
        {
            // Long-session evidence showed gfxcapture's internal fixed-size
            // surface pool collapsing to 0.5-6 unique FPS while its CFR output
            // still reported 60 FPS. Capture native WGC surfaces on NVIDIA,
            // cross the API boundary through bounded system memory, then scale
            // on CUDA. This graph is runtime-probed and kept the production
            // encoder at real time on the affected adapter/driver.
            if (encodingStrategy.Encoder == VideoEncoderKind.NvidiaNvenc &&
                !encodingStrategy.RequiresSystemMemoryTransfer)
            {
                return filter +
                    ":width=-2:height=-2:resize_mode=crop:scale_mode=point" +
                    ",hwdownload,format=bgra,hwupload_cuda" +
                    $",scale_cuda=w={output.Width}:h={output.Height}" +
                    ":format=bgra:interp_algo=nearest";
            }

            if (encodingStrategy.RequiresSystemMemoryTransfer)
            {
                // The independently probed compatibility candidate must remain
                // usable when CUDA interop is unavailable. Native capture plus
                // fast software scaling costs more CPU but avoids the failing
                // fixed-size WGC pool and feeds a normal system-memory format.
                var outputPixelFormat = encodingStrategy.Encoder ==
                    VideoEncoderKind.SoftwareX264
                        ? "yuv420p"
                        : "nv12";
                return filter +
                    ":width=-2:height=-2:resize_mode=crop:scale_mode=point" +
                    ",hwdownload,format=bgra" +
                    $",scale={output.Width}:{output.Height}:flags=fast_bilinear" +
                    $",format={outputPixelFormat}";
            }

            // QSV/AMF can consume the scaled D3D11 surface directly. Their
            // exact graph remains capability-probed; health monitoring moves
            // away from it if measured cadence later collapses.
            filter += $":width={output.Width}:height={output.Height}" +
                      ":resize_mode=scale:scale_mode=point";
        }
        else
        {
            // Source/native and a fixed preset that already matches the monitor
            // should not activate the D3D11 resizer. Encoding at the existing
            // size avoids needless GPU copy/scaling work over a foreground game.
            // H.264 encoders require even dimensions. Negative gfxcapture sizes
            // round the native monitor size down to the requested multiple.
            filter += ":width=-2:height=-2:resize_mode=crop:scale_mode=point";
        }

        if (!encodingStrategy.RequiresSystemMemoryTransfer)
        {
            return filter;
        }

        // A transfer is the compatibility path for hybrid/multi-GPU systems,
        // where capture and encoding can be backed by different D3D11 devices.
        return encodingStrategy.Encoder switch
        {
            VideoEncoderKind.IntelQuickSync => filter + ",hwdownload,format=bgra,format=nv12",
            VideoEncoderKind.SoftwareX264 => filter + ",hwdownload,format=bgra,format=yuv420p",
            _ => filter + ",hwdownload,format=bgra"
        };
    }

    /// <summary>
    /// WGC implements max_framerate as a minimum update interval. Asking a
    /// 165-Hz desktop for exactly 60 updates admits every third refresh (about
    /// 55 unique frames), which CFR then has to duplicate. The smallest rate
    /// just above an integral refresh divisor avoids that alias while keeping
    /// capture work well below the full high-refresh presentation rate.
    /// </summary>
    internal static int ResolveGraphicsCaptureInputFrameRate(
        int outputFramesPerSecond,
        int displayRefreshRateHz)
    {
        if (outputFramesPerSecond is < 1 or > 240)
        {
            throw new ArgumentOutOfRangeException(nameof(outputFramesPerSecond));
        }

        var maximumInputRate = Math.Min(
            1000,
            checked(outputFramesPerSecond * 2));
        if (displayRefreshRateHz is < 24 or > 1000)
        {
            // Keep the proven low-overhead path when a remote/virtual driver
            // cannot report VREFRESH. Verified high-refresh displays use the
            // divisor-aware adjustment below.
            return outputFramesPerSecond;
        }

        var refreshesPerFrame = Math.Max(
            1,
            displayRefreshRateHz / outputFramesPerSecond);
        var divisorBoundary =
            (double)displayRefreshRateHz / refreshesPerFrame;
        return Math.Clamp(
            checked((int)Math.Ceiling(divisorBoundary) + 1),
            outputFramesPerSecond,
            maximumInputRate);
    }

    private static bool UsesDirectWindowsGraphicsHardwarePath(
        VideoEncodingStrategy encodingStrategy) =>
        encodingStrategy.CaptureBackend == DesktopCaptureBackend.WindowsGraphicsCapture &&
        encodingStrategy.IsHardwareEncoder &&
        !encodingStrategy.RequiresSystemMemoryTransfer;

    private static void AddEncoderArguments(
        List<string> arguments,
        VideoEncodingStrategy encodingStrategy,
        int? softwareThreadLimit = null)
    {
        arguments.AddRange(["-c:v", encodingStrategy.FfmpegEncoder]);

        switch (encodingStrategy.Encoder)
        {
            case VideoEncoderKind.NvidiaNvenc:
                arguments.AddRange([
                    // NVIDIA recommends P2-P3 for real-time screen capture. P2
                    // intentionally favors game headroom over P4's extra quality.
                    "-preset", "p2",
                    "-tune", "ll",
                    "-rc", "vbr",
                    "-cq", "23",
                    "-b:v", "0",
                    "-multipass", "disabled",
                    "-rc-lookahead", "0",
                    "-surfaces", "4",
                    "-bf", "0",
                    "-forced-idr", "1",
                    "-zerolatency", "1"
                ]);
                break;

            case VideoEncoderKind.IntelQuickSync:
                arguments.AddRange([
                    "-preset", "veryfast",
                    "-global_quality", "23",
                    "-look_ahead", "0",
                    "-async_depth", "2",
                    "-scenario", "gamestreaming",
                    "-bf", "0",
                    "-forced_idr", "1"
                ]);
                break;

            case VideoEncoderKind.AmdAmf:
                arguments.AddRange([
                    "-usage", "lowlatency",
                    "-quality", "speed",
                    "-rc", "cqp",
                    "-qp_i", "23",
                    "-qp_p", "23",
                    "-async_depth", "2",
                    "-preanalysis", "false",
                    "-bf", "0",
                    "-forced_idr", "true"
                ]);
                break;

            default:
                var softwareThreadCount = softwareThreadLimit is { } requestedLimit
                    ? Math.Clamp(
                        GetSoftwareEncoderThreadCount(Environment.ProcessorCount),
                        1,
                        Math.Clamp(requestedLimit, 1, 4))
                    : GetSoftwareEncoderThreadCount(Environment.ProcessorCount);
                arguments.AddRange([
                    "-preset", "ultrafast",
                    "-tune", "zerolatency",
                    "-crf", "24",
                    "-pix_fmt", "yuv420p",
                    "-threads", Invariant(softwareThreadCount),
                    "-x264-params", "rc-lookahead=0:bframes=0:scenecut=0"
                ]);
                break;
        }
    }

    internal static int GetSoftwareEncoderThreadCount(int processorCount) =>
        Math.Clamp(Math.Max(1, processorCount / 2), 1, 4);

    private static string BuildAudioFilter(int inputCount)
    {
        var parts = new List<string>(inputCount + 1);
        var labels = new List<string>(inputCount);

        for (var index = 0; index < inputCount; index++)
        {
            var inputIndex = index + 1;
            var label = $"audio_{index}";
            labels.Add($"[{label}]");
            parts.Add($"[{inputIndex}:a]aresample=48000:async=1:first_pts=0,volume=0.70[{label}]");
        }

        if (inputCount == 1)
        {
            parts.Add($"{labels[0]}anull[mixed_audio]");
        }
        else
        {
            parts.Add($"{string.Concat(labels)}amix=inputs={inputCount}:duration=longest:dropout_transition=2:normalize=0[mixed_audio]");
        }

        return string.Join(';', parts);
    }

    private static string FormatTimestamp(TimeSpan value) =>
        value.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);

    private static string FormatPreciseTimestamp(TimeSpan value) =>
        value.TotalSeconds.ToString("0.#########", CultureInfo.InvariantCulture);

    private static string Invariant(int value) => value.ToString(CultureInfo.InvariantCulture);
}
