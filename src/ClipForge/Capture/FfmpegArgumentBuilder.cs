using System.Globalization;
using ClipForge.Models;

namespace ClipForge.Capture;

internal static class FfmpegArgumentBuilder
{
    internal const int SegmentSeconds = 2;

    public static IReadOnlyList<string> BuildCaptureArguments(
        CaptureConfiguration configuration,
        IReadOnlyList<AudioInputSpecification> audioInputs,
        string segmentDirectory)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(audioInputs);
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
            "-loglevel", "warning",
            "-thread_queue_size", "1024",
            "-f", "gdigrab",
            "-draw_mouse", "1",
            "-framerate", Invariant(configuration.FramesPerSecond),
            "-offset_x", Invariant(display.Left),
            "-offset_y", Invariant(display.Top),
            "-video_size", $"{display.Width}x{display.Height}",
            "-i", "desktop"
        };

        foreach (var audioInput in audioInputs)
        {
            arguments.AddRange(
            [
                "-thread_queue_size", "1024",
                "-f", audioInput.FfmpegSampleFormat,
                "-ar", Invariant(audioInput.SampleRate),
                "-ac", Invariant(audioInput.Channels),
                "-i", audioInput.PipePath
            ]);
        }

        arguments.AddRange(["-map", "0:v:0", "-vf", BuildVideoFilter(configuration.Resolution)]);

        if (audioInputs.Count == 0)
        {
            arguments.Add("-an");
        }
        else
        {
            arguments.AddRange(["-filter_complex", BuildAudioFilter(audioInputs.Count), "-map", "[mixed_audio]"]);
        }

        var keyFrameInterval = checked(configuration.FramesPerSecond * SegmentSeconds);
        arguments.AddRange(
        [
            "-c:v", "libx264",
            "-preset", "veryfast",
            "-tune", "zerolatency",
            "-crf", "23",
            "-pix_fmt", "yuv420p",
            "-fps_mode", "cfr",
            "-r", Invariant(configuration.FramesPerSecond),
            "-g", Invariant(keyFrameInterval),
            "-keyint_min", Invariant(keyFrameInterval),
            "-sc_threshold", "0",
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
