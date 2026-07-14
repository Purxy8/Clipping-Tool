namespace ClipForge.Capture;

internal readonly record struct CaptureForegroundContext(
    bool IsFullscreenOnCapturedDisplay,
    bool HasRecentInput);

internal sealed record CaptureStarvationAssessment(
    double DuplicateRatio,
    double UniqueFramesPerSecond,
    TimeSpan Window);

/// <summary>
/// Detects source starvation from FFmpeg's cumulative CFR counters. It
/// deliberately requires a fullscreen foreground surface, so a static desktop
/// cannot be mistaken for a failed game capture. Severe starvation is detected
/// quickly, while moderate starvation is only considered after a sustained
/// window in an aged capture session.
/// </summary>
internal sealed class CaptureStarvationWatchdog
{
    private const double RequiredFullscreenSampleRatio = 0.75;
    private const double RequiredRecentInputSampleRatio = 0.75;
    private static readonly TimeSpan ConfirmationWindow = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan HealthyCadenceConfirmationWindow = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan ModerateConfirmationWindow = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan ModerateMinimumCaptureUptime = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MinimumInputEvidenceSpan = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan HealthyCadenceMinimumInputEvidenceSpan = TimeSpan.FromSeconds(9);
    private static readonly TimeSpan ModerateMinimumInputEvidenceSpan = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan FullscreenEligibilityLossResetWindow = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan MaximumHistory = TimeSpan.FromSeconds(32);
    private readonly int _targetFramesPerSecond;
    private readonly Queue<Observation> _observations = new();
    private bool _triggered;
    private bool _observedHealthyActiveCadence;
    private long _fullscreenEligibilityLostAt = -1;
    private long _lastTimestamp = -1;
    private CaptureProgressSample? _lastSample;

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
        CaptureForegroundContext context,
        TimeSpan? captureUptime = null)
    {
        ArgumentNullException.ThrowIfNull(sample);
        if (_triggered || sample.Timestamp <= _lastTimestamp)
        {
            return null;
        }

        if (_lastSample is { } previous &&
            (sample.Frame < previous.Frame ||
             sample.DuplicatedFrames < previous.DuplicatedFrames ||
             sample.OutputTimeMicroseconds < previous.OutputTimeMicroseconds))
        {
            _observations.Clear();
            _observedHealthyActiveCadence = false;
            _fullscreenEligibilityLostAt = -1;
        }

        _lastTimestamp = sample.Timestamp;
        _lastSample = sample;
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
                _observedHealthyActiveCadence = false;
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

        var assessment = AssessWindow(
            sample,
            context,
            ConfirmationWindow,
            MinimumInputEvidenceSpan,
            minimumDuplicateRatio: 0.90,
            maximumUniqueFrameRateRatio: 0.10);

        if (!_observedHealthyActiveCadence)
        {
            _observedHealthyActiveCadence = HasHealthyActiveCadence(sample, context);
        }

        if (assessment is null &&
            _observedHealthyActiveCadence &&
            captureUptime is not null &&
            captureUptime.Value >= ModerateMinimumCaptureUptime)
        {
            // A WGC source can degrade without reaching the severe threshold:
            // FFmpeg still emits the requested CFR stream, but most frames are
            // duplicates. Requiring healthy active cadence earlier in this same
            // FFmpeg process, an aged session, twenty seconds of evidence, and at
            // most 28% meaningful source cadence distinguishes degradation from
            // a game that has legitimately produced only 15/16 FPS since launch.
            assessment = AssessWindow(
                sample,
                context,
                ModerateConfirmationWindow,
                ModerateMinimumInputEvidenceSpan,
                minimumDuplicateRatio: 0.66,
                maximumUniqueFrameRateRatio: 0.28);
        }

        if (assessment is null)
        {
            return null;
        }

        _triggered = true;
        return assessment;
    }

    private bool HasHealthyActiveCadence(
        CaptureProgressSample sample,
        CaptureForegroundContext context)
    {
        var baseline = FindBaseline(sample.Timestamp, HealthyCadenceConfirmationWindow);
        if (baseline is null)
        {
            return false;
        }

        var elapsed = Stopwatch.GetElapsedTime(baseline.Sample.Timestamp, sample.Timestamp);
        if (elapsed < HealthyCadenceConfirmationWindow)
        {
            return false;
        }

        var window = _observations
            .Where(observation => observation.Sample.Timestamp >= baseline.Sample.Timestamp)
            .ToArray();
        if (window.Length < 3 ||
            window.Count(observation => observation.Context.IsFullscreenOnCapturedDisplay) /
            (double)window.Length < RequiredFullscreenSampleRatio)
        {
            return false;
        }

        var inputObservations = window
            .Where(observation => observation.Context.HasRecentInput)
            .ToArray();
        if (!context.HasRecentInput ||
            inputObservations.Length < 2 ||
            inputObservations.Length / (double)window.Length < RequiredRecentInputSampleRatio ||
            Stopwatch.GetElapsedTime(
                inputObservations[0].Sample.Timestamp,
                inputObservations[^1].Sample.Timestamp) < HealthyCadenceMinimumInputEvidenceSpan)
        {
            return false;
        }

        var frameDelta = sample.Frame - baseline.Sample.Frame;
        var duplicateDelta = sample.DuplicatedFrames - baseline.Sample.DuplicatedFrames;
        var elapsedSeconds = elapsed.TotalSeconds;
        if (frameDelta <= 0 || duplicateDelta < 0 ||
            frameDelta < _targetFramesPerSecond * elapsedSeconds * 0.70)
        {
            return false;
        }

        var uniqueFramesPerSecond = Math.Max(0, frameDelta - duplicateDelta) / elapsedSeconds;
        return uniqueFramesPerSecond >= _targetFramesPerSecond * 0.50;
    }

    private CaptureStarvationAssessment? AssessWindow(
        CaptureProgressSample sample,
        CaptureForegroundContext context,
        TimeSpan confirmationWindow,
        TimeSpan minimumInputEvidenceSpan,
        double minimumDuplicateRatio,
        double maximumUniqueFrameRateRatio)
    {
        var baseline = FindBaseline(sample.Timestamp, confirmationWindow);
        if (baseline is null)
        {
            return null;
        }

        var elapsed = Stopwatch.GetElapsedTime(baseline.Sample.Timestamp, sample.Timestamp);
        if (elapsed < confirmationWindow)
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
                inputObservations[^1].Sample.Timestamp) < minimumInputEvidenceSpan)
        {
            return null;
        }

        // HasRecentInput remains true for several seconds after a single input.
        // Requiring broad coverage and time-separated evidence proves continuing
        // activity across the confirmation window. Consequently a paused cutscene
        // or other fully idle/static fullscreen scene cannot be restarted solely
        // because CFR duplicated its unchanged image.
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
        if (duplicateRatio < minimumDuplicateRatio ||
            uniqueFramesPerSecond > Math.Max(3, _targetFramesPerSecond * maximumUniqueFrameRateRatio))
        {
            return null;
        }

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
