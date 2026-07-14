using System.Globalization;

namespace ClipForge.Capture;

internal sealed record CaptureProgressSample(
    long Frame,
    long DuplicatedFrames,
    long DroppedFrames,
    long OutputTimeMicroseconds,
    long Timestamp);

/// <summary>
/// Parses FFmpeg's machine-readable -progress stream. Records are emitted only
/// at a progress boundary so the health monitor never observes mixed counters
/// from two reports.
/// </summary>
internal sealed class CaptureProgressParser
{
    private long? _frame;
    private long? _duplicatedFrames;
    private long? _droppedFrames;
    private long? _outputTimeMicroseconds;

    public bool TryParse(
        string? line,
        long timestamp,
        out CaptureProgressSample? sample)
    {
        sample = null;
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        var separator = line.IndexOf('=');
        if (separator <= 0 || separator == line.Length - 1)
        {
            return false;
        }

        var key = line[..separator].Trim();
        var value = line[(separator + 1)..].Trim();
        if (key.Equals("progress", StringComparison.Ordinal))
        {
            if (_frame is { } frame &&
                _duplicatedFrames is { } duplicatedFrames &&
                _outputTimeMicroseconds is { } outputTimeMicroseconds)
            {
                sample = new CaptureProgressSample(
                    frame,
                    duplicatedFrames,
                    _droppedFrames ?? 0,
                    outputTimeMicroseconds,
                    timestamp);
            }

            ResetRecord();
            return sample is not null;
        }

        if (!long.TryParse(
                value,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var parsed) || parsed < 0)
        {
            return false;
        }

        switch (key)
        {
            case "frame":
                _frame = parsed;
                break;
            case "dup_frames":
                _duplicatedFrames = parsed;
                break;
            case "drop_frames":
                _droppedFrames = parsed;
                break;
            case "out_time_us":
                _outputTimeMicroseconds = parsed;
                break;
        }

        return false;
    }

    private void ResetRecord()
    {
        _frame = null;
        _duplicatedFrames = null;
        _droppedFrames = null;
        _outputTimeMicroseconds = null;
    }
}
