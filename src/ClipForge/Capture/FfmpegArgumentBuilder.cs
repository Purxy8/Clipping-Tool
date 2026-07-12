using System.Globalization;
using ClipForge.Models;

namespace ClipForge.Capture;

internal static class FfmpegArgumentBuilder
{
    internal const int SegmentSeconds = 2;
    internal const int VideoInputQueuePackets = 8;
    internal const int AudioInputQueuePackets = 64;

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
        string segmentDirectory)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(audioInputs);
        ArgumentNullException.ThrowIfNull(encodingStrategy);
        ArgumentException.ThrowIfNullOrWhiteSpace(segmentDirectory);

        if (configuration.FramesPerSecond is < 1 or > 240)
        {
            throw new ArgumentOutOfRangeException(
                nameof(configuration),
                "The frame rate must be between 1 and 240 frames per second.");
        }

        var display = configuration.Display;
        if (display.Width <= 0 || display.Height <= 0)
        {
            throw new ArgumentException("The selected display has invalid dimensions.", nameof(configuration));
        }

        var arguments = new List<string>
        {
            "-hide_banner",
            "-loglevel", "warning"
        };

        AddVideoInput(arguments, configuration, encodingStrategy);

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
                $"{BuildVideoFilter(configuration.Resolution)},format={encoderPixelFormat}"
            ]);
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
        AddEncoderArguments(arguments, encodingStrategy);
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
        arguments.AddRange(
        [
            "-f", "segment",
            "-segment_time", Invariant(SegmentSeconds),
            "-segment_time_delta", segmentTimeDelta.ToString("0.########", CultureInfo.InvariantCulture),
            "-reset_timestamps", "1",
            "-segment_format", "matroska",
            "-segment_start_number", "0",
            "-y",
            Path.Combine(segmentDirectory, "segment-%09d.mkv")
        ]);

        return arguments;
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
        VideoEncodingStrategy encodingStrategy)
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
            "-nostdin"
        };
        AddVideoInput(arguments, configuration, encodingStrategy);
        arguments.AddRange(["-frames:v", "2", "-an"]);
        AddEncoderArguments(arguments, encodingStrategy);
        arguments.AddRange(["-f", "null", "NUL"]);
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
            "-movflags", "+faststart",
            "-y",
            outputPath
        ]);

        return arguments;
    }

    private static string BuildVideoFilter(ResolutionOption resolution)
    {
        if (resolution.Width is null || resolution.Height is null)
        {
            return "scale=trunc(iw/2)*2:trunc(ih/2)*2";
        }

        var width = resolution.Width.Value;
        var height = resolution.Height.Value;
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentException("The selected resolution has invalid dimensions.", nameof(resolution));
        }

        return $"scale={width}:{height}:force_original_aspect_ratio=decrease:force_divisible_by=2," +
               $"pad={width}:{height}:(ow-iw)/2:(oh-ih)/2:color=black";
    }

    private static void AddVideoInput(
        List<string> arguments,
        CaptureConfiguration configuration,
        VideoEncodingStrategy encodingStrategy)
    {
        var display = configuration.Display;
        arguments.AddRange([
            "-thread_queue_size", Invariant(VideoInputQueuePackets)
        ]);

        if (encodingStrategy.CaptureBackend == DesktopCaptureBackend.WindowsGraphicsCapture)
        {
            arguments.AddRange([
                "-f", "lavfi",
                "-i", BuildGraphicsCaptureFilter(configuration, encodingStrategy)
            ]);
            return;
        }

        arguments.AddRange([
            "-f", "gdigrab",
            "-draw_mouse", "1",
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
        var filter = $"gfxcapture=monitor_idx={configuration.Display.MonitorIndex}" +
                     $":capture_cursor=1:max_framerate={Invariant(configuration.FramesPerSecond)}" +
                     ":output_fmt=bgra";

        if (configuration.Resolution.Width is { } width &&
            configuration.Resolution.Height is { } height)
        {
            filter += $":width={width}:height={height}:resize_mode=scale_aspect";
        }
        else
        {
            // H.264 encoders require even dimensions. Negative gfxcapture sizes
            // round the native monitor size down to the requested multiple.
            filter += ":width=-2:height=-2:resize_mode=scale_aspect";
        }

        filter += $",fps={Invariant(configuration.FramesPerSecond)}";

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

    private static void AddEncoderArguments(
        List<string> arguments,
        VideoEncodingStrategy encodingStrategy)
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
                arguments.AddRange([
                    "-preset", "ultrafast",
                    "-tune", "zerolatency",
                    "-crf", "24",
                    "-pix_fmt", "yuv420p",
                    "-threads", Invariant(GetSoftwareEncoderThreadCount(Environment.ProcessorCount)),
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

    private static string Invariant(int value) => value.ToString(CultureInfo.InvariantCulture);
}
