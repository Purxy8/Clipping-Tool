namespace ClipForge.Capture;

internal readonly record struct CaptureForegroundContext(
    bool IsFullscreenOnCapturedDisplay,
    bool HasRecentInput);

internal sealed record CaptureStarvationAssessment(
    double DuplicateRatio,
    double UniqueFramesPerSecond,
    TimeSpan Window);

/// <summary>
/// Detects severe source starvation from FFmpeg's cumulative CFR counters. It
/// deliberately requires a fullscreen foreground surface, so a static desktop
/// cannot be mistaken for a failed game capture.
/// </summary>
internal sealed class CaptureStarvationWatchdog
{
    private const double RequiredFullscreenSampleRatio = 0.75;
    private const double RequiredRecentInputSampleRatio = 0.75;
    private static readonly TimeSpan ConfirmationWindow = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan MinimumInputEvidenceSpan = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan FullscreenEligibilityLossResetWindow = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan MaximumHistory = TimeSpan.FromSeconds(24);
    private readonly int _targetFramesPerSecond;
    private readonly Queue<Observation> _observations = new();
    private bool _triggered;
    private long _fullscreenEligibilityLostAt = -1;
    private long _lastTimestamp = -1;

    public CaptureStarvationWatchdog(int targetFramesPerSecond)
    {
        if (targetFramesPerSecond is < 1 or > 240)
        {
            throw new ArgumentOutOfRangeException(nameof(targetFramesPerSecond));
        }

        _targetFramesPerSecond = targetFramesPerSecond;
    }

    public CaptureStarvationAssessment? Observe(
        CaptureProgressSample sample,
        CaptureForegroundContext context)
    {
        ArgumentNullException.ThrowIfNull(sample);
        if (_triggered || sample.Timestamp <= _lastTimestamp)
        {
            return null;
        }

        if (_observations.TryPeek(out var previous) &&
            (sample.Frame < previous.Sample.Frame ||
             sample.DuplicatedFrames < previous.Sample.DuplicatedFrames ||
             sample.OutputTimeMicroseconds < previous.Sample.OutputTimeMicroseconds))
        {
            _observations.Clear();
            _fullscreenEligibilityLostAt = -1;
        }

        _lastTimestamp = sample.Timestamp;
        if (context.IsFullscreenOnCapturedDisplay)
        {
            _fullscreenEligibilityLostAt = -1;
        }
        else
        {
            if (_fullscreenEligibilityLostAt < 0)
            {
                _fullscreenEligibilityLostAt = sample.Timestamp;
            }

            // Foreground probes can miss a sample while a game changes display
            // mode, opens a menu, or briefly loses focus. Keep those samples in
            // the ratio below, but discard the candidate after a sustained loss
            // so an old fullscreen period cannot trigger after a real alt-tab.
            if (Stopwatch.GetElapsedTime(
                    _fullscreenEligibilityLostAt,
                    sample.Timestamp) >= FullscreenEligibilityLossResetWindow)
            {
                _observations.Clear();
                return null;
            }
        }

        _observations.Enqueue(new Observation(sample, context));
        TrimHistory(sample.Timestamp);

        // Never request recovery while the user is currently outside the game.
        // A transient miss still remains in the sampled fullscreen ratio.
        if (!context.IsFullscreenOnCapturedDisplay)
        {
            return null;
        }

        var baseline = FindBaseline(sample.Timestamp, ConfirmationWindow);
        if (baseline is null)
        {
            return null;
        }

        var elapsed = Stopwatch.GetElapsedTime(baseline.Sample.Timestamp, sample.Timestamp);
        if (elapsed < ConfirmationWindow)
        {
            return null;
        }

        var window = _observations
            .Where(observation => observation.Sample.Timestamp >= baseline.Sample.Timestamp)
            .ToArray();
        if (window.Length < 3 ||
            window.Count(observation => observation.Context.IsFullscreenOnCapturedDisplay) /
            (double)window.Length < RequiredFullscreenSampleRatio)
        {
            return null;
        }

        var inputObservations = window
            .Where(observation => observation.Context.HasRecentInput)
            .ToArray();
        if (!context.HasRecentInput ||
            inputObservations.Length < 2 ||
            inputObservations.Length / (double)window.Length < RequiredRecentInputSampleRatio ||
            Stopwatch.GetElapsedTime(
                inputObservations[0].Sample.Timestamp,
                inputObservations[^1].Sample.Timestamp) < MinimumInputEvidenceSpan)
        {
            return null;
        }

        // HasRecentInput remains true for several seconds after a single input.
        // Requiring broad coverage and evidence separated by six seconds proves
        // continuing activity across the confirmation window. Consequently a
        // paused cutscene or other fully idle/static fullscreen scene cannot be
        // restarted solely because CFR duplicated its unchanged image.

        var frameDelta = sample.Frame - baseline.Sample.Frame;
        var duplicateDelta = sample.DuplicatedFrames - baseline.Sample.DuplicatedFrames;
        var elapsedSeconds = elapsed.TotalSeconds;
        if (frameDelta <= 0 || duplicateDelta < 0 ||
            frameDelta < _targetFramesPerSecond * elapsedSeconds * 0.70)
        {
            return null;
        }

        var duplicateRatio = duplicateDelta / (double)frameDelta;
        var uniqueFramesPerSecond = Math.Max(0, frameDelta - duplicateDelta) / elapsedSeconds;
        if (duplicateRatio < 0.90 ||
            uniqueFramesPerSecond > Math.Max(3, _targetFramesPerSecond * 0.10))
        {
            return null;
        }

        _triggered = true;
        return new CaptureStarvationAssessment(
            duplicateRatio,
            uniqueFramesPerSecond,
            elapsed);
    }

    private Observation? FindBaseline(long latestTimestamp, TimeSpan requiredWindow)
    {
        Observation? result = null;
        foreach (var observation in _observations)
        {
            var age = Stopwatch.GetElapsedTime(observation.Sample.Timestamp, latestTimestamp);
            if (age < requiredWindow)
            {
                break;
            }

            result = observation;
        }

        return result;
    }

    private void TrimHistory(long latestTimestamp)
    {
        while (_observations.TryPeek(out var oldest) &&
               Stopwatch.GetElapsedTime(oldest.Sample.Timestamp, latestTimestamp) > MaximumHistory)
        {
            _ = _observations.Dequeue();
        }
    }

    private sealed record Observation(
        CaptureProgressSample Sample,
        CaptureForegroundContext Context);
}
