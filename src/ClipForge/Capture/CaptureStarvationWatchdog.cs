namespace ClipForge.Capture;

internal readonly record struct CaptureForegroundContext(
    bool IsFullscreenOnCapturedDisplay,
    bool HasRecentInput,
    double CapturedDisplayCoverage = 0,
    bool UsedCustomFullscreenFallback = false);

internal enum CaptureStarvationKind
{
    Severe,
    ModerateDegradation,
    SchedulingPressure,
    OutputThroughput,
    ChronicLowCadence,
    ProgressGap
}

internal sealed record CaptureStarvationAssessment(
    double DuplicateRatio,
    double UniqueFramesPerSecond,
    TimeSpan Window,
    CaptureStarvationKind Kind,
    double OutputSpeedRatio = 1,
    bool UsedCustomFullscreenFallback = false);

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
    private static readonly TimeSpan ChronicLowCadenceConfirmationWindow =
        TimeSpan.FromSeconds(12);
    private static readonly TimeSpan BurstPressureWindow = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan ModerateConfirmationWindow = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan ModerateMinimumCaptureUptime = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MinimumInputEvidenceSpan = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan HealthyCadenceMinimumInputEvidenceSpan = TimeSpan.FromSeconds(9);
    private static readonly TimeSpan ChronicLowCadenceMinimumInputEvidenceSpan =
        TimeSpan.FromSeconds(9);
    private static readonly TimeSpan ModerateMinimumInputEvidenceSpan = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan FullscreenEligibilityLossResetWindow = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan MaximumQueuedProgressReceiptInterval =
        TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan MaximumHistory = TimeSpan.FromSeconds(32);
    // ReplayBufferService retains at most 256 parsed progress records. Permit
    // that entire bounded queue to drain before judging a parent-process pause
    // as a recorder freeze.
    private const int MaximumQueuedProgressCatchUpSamples = 256;
    private readonly int _targetFramesPerSecond;
    private readonly Queue<Observation> _observations = new();
    private bool _triggered;
    private bool _observedHealthyActiveCadence;
    private bool _observedHealthyActiveCadenceEver;
    private PendingProgressGap? _pendingProgressGap;
    private long _fullscreenEligibilityLostAt = -1;
    private long _lastTimestamp = -1;
    private CaptureProgressSample? _lastSample;
    private CaptureForegroundContext? _lastContext;

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
        TimeSpan? captureUptime = null,
        bool allowSchedulingPressure = false,
        bool allowOutputThroughput = false,
        bool allowChronicLowCadence = false,
        bool allowSourceCadence = true)
    {
        ArgumentNullException.ThrowIfNull(sample);
        if (_triggered || sample.Timestamp <= _lastTimestamp)
        {
            return null;
        }

        var previousSample = _lastSample;
        var previousContext = _lastContext;
        if (previousSample is { } previous &&
            (sample.Frame < previous.Frame ||
             sample.DuplicatedFrames < previous.DuplicatedFrames ||
             sample.OutputTimeMicroseconds < previous.OutputTimeMicroseconds))
        {
            _observations.Clear();
            _observedHealthyActiveCadence = false;
            _observedHealthyActiveCadenceEver = false;
            _pendingProgressGap = null;
            _fullscreenEligibilityLostAt = -1;
            previousSample = null;
            previousContext = null;
        }

        _lastTimestamp = sample.Timestamp;
        _lastSample = sample;
        _lastContext = context;
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
                _pendingProgressGap = null;
                return null;
            }
        }

        _observations.Enqueue(new Observation(sample, context));
        TrimHistory(sample.Timestamp);

        // Never request recovery while the user is currently outside the game.
        // A transient miss still remains in the sampled fullscreen ratio.
        if (!context.IsFullscreenOnCapturedDisplay)
        {
            _pendingProgressGap = null;
            return null;
        }

        CaptureStarvationAssessment? assessment = null;
        var deferAssessmentForProgressCatchUp = false;
        if (previousSample is not null &&
            previousContext is not null)
        {
            assessment = AssessProgressGap(
                previousSample,
                previousContext.Value,
                sample,
                context,
                captureUptime,
                out deferAssessmentForProgressCatchUp);
        }

        if (deferAssessmentForProgressCatchUp)
        {
            // The parent process can be paused while FFmpeg continues writing
            // progress into its pipe. The first stale block received on resume
            // looks like a graph freeze; the next queued block catches counters
            // up almost immediately. Give that single sample one observation
            // of grace before any timestamp-based cadence lane can quarantine
            // otherwise healthy media.
            return null;
        }

        if (assessment is null &&
            (allowSchedulingPressure || allowOutputThroughput))
        {
            // Counter throughput is objective recorder evidence. Evaluate it
            // before content-duplicate cadence so a custom/stretched game that
            // both repeats frames and runs below real time follows the bounded
            // fault path instead of being mistaken for ambiguous low-FPS
            // content.
            assessment = AssessOutputThroughputPressure(sample, context);
        }

        if (assessment is null && allowSourceCadence)
        {
            assessment = AssessWindow(
                sample,
                context,
                ConfirmationWindow,
                MinimumInputEvidenceSpan,
                minimumDuplicateRatio: 0.90,
                maximumUniqueFrameRateRatio: 0.10,
                CaptureStarvationKind.Severe);
        }

        if (allowSourceCadence && !_observedHealthyActiveCadence)
        {
            _observedHealthyActiveCadence = HasHealthyActiveCadence(sample, context);
            _observedHealthyActiveCadenceEver |=
                _observedHealthyActiveCadence;
        }

        if (assessment is null && allowSchedulingPressure)
        {
            // Burst stalls and below-real-time output are recorder evidence,
            // unlike a stable game/content frame-rate cap. ReplayBufferService
            // promotes native LowImpact capture on the first result and maps a
            // repeat in Resilient/scaled capture to bounded recovery.
            assessment = AssessBurstPressure(sample, context);
        }

        if (assessment is null &&
            allowChronicLowCadence &&
            !_observedHealthyActiveCadenceEver)
        {
            // This lane is enabled only for the initial native LowImpact
            // profile. Stable 16-30 FPS from process start then causes one
            // non-looping promotion to Resilient instead of being ignored
            // forever. Scaled and already-promoted capture never use it.
            assessment = AssessWindow(
                sample,
                context,
                ChronicLowCadenceConfirmationWindow,
                ChronicLowCadenceMinimumInputEvidenceSpan,
                minimumDuplicateRatio: 0.45,
                maximumUniqueFrameRateRatio: 0.55,
                CaptureStarvationKind.ChronicLowCadence);
        }

        if (assessment is null &&
            allowSourceCadence &&
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
                maximumUniqueFrameRateRatio: 0.28,
                CaptureStarvationKind.ModerateDegradation);
        }

        if (assessment is null)
        {
            return null;
        }

        _triggered = true;
        return assessment;
    }

    private CaptureStarvationAssessment? AssessProgressGap(
        CaptureProgressSample previous,
        CaptureForegroundContext previousContext,
        CaptureProgressSample sample,
        CaptureForegroundContext context,
        TimeSpan? captureUptime,
        out bool awaitingConfirmation)
    {
        awaitingConfirmation = false;
        var interval = Stopwatch.GetElapsedTime(previous.Timestamp, sample.Timestamp);
        var frameDelta = sample.Frame - previous.Frame;
        var duplicateDelta =
            sample.DuplicatedFrames - previous.DuplicatedFrames;
        var outputTimeDelta =
            sample.OutputTimeMicroseconds - previous.OutputTimeMicroseconds;
        if (frameDelta < 0 || duplicateDelta < 0 || outputTimeDelta < 0)
        {
            _pendingProgressGap = null;
            return null;
        }

        if (_pendingProgressGap is { } pending)
        {
            if (HasCaughtUpToRealTime(
                    pending.Baseline,
                    sample))
            {
                _pendingProgressGap = null;
                RebaseObservationsAfterProgressBacklog(sample, context);
                awaitingConfirmation = true;
                return null;
            }

            if (pending.CatchUpSamples <
                    MaximumQueuedProgressCatchUpSamples &&
                IsRapidQueuedProgressCatchUp(
                    interval,
                    frameDelta,
                    outputTimeDelta))
            {
                _pendingProgressGap = pending with
                {
                    CatchUpSamples = pending.CatchUpSamples + 1
                };
                awaitingConfirmation = true;
                return null;
            }

            _pendingProgressGap = null;
            return pending.Assessment;
        }

        if (captureUptime is null ||
            captureUptime.Value < ConfirmationWindow ||
            interval < TimeSpan.FromSeconds(2.5) ||
            !previousContext.IsFullscreenOnCapturedDisplay ||
            !context.IsFullscreenOnCapturedDisplay ||
            !previousContext.HasRecentInput ||
            !context.HasRecentInput)
        {
            return null;
        }

        var duplicateRatio = frameDelta > 0
            ? duplicateDelta / (double)frameDelta
            : 1;
        var uniqueFramesPerSecond = frameDelta > 0
            ? Math.Max(0, frameDelta - duplicateDelta) / interval.TotalSeconds
            : 0;
        var outputSpeed = outputTimeDelta > 0
            ? outputTimeDelta / 1_000_000d / interval.TotalSeconds
            : 0;
        var expectedFrames =
            _targetFramesPerSecond * interval.TotalSeconds;
        var outputFrameRateRatio = expectedFrames > 0
            ? frameDelta / expectedFrames
            : 0;
        if (outputFrameRateRatio >= 0.85 &&
            outputSpeed >= 0.85)
        {
            // A delayed/missing progress report is telemetry jitter, not a
            // capture freeze, when the counters prove the graph advanced in
            // real time throughout the interval.
            return null;
        }

        var assessment = new CaptureStarvationAssessment(
            duplicateRatio,
            uniqueFramesPerSecond,
            interval,
            CaptureStarvationKind.ProgressGap,
            outputSpeed,
            previousContext.UsedCustomFullscreenFallback ||
            context.UsedCustomFullscreenFallback);
        _pendingProgressGap = new PendingProgressGap(
            previous,
            assessment,
            CatchUpSamples: 0);
        awaitingConfirmation = true;
        return null;
    }

    private bool HasCaughtUpToRealTime(
        CaptureProgressSample baseline,
        CaptureProgressSample sample)
    {
        var elapsed = Stopwatch.GetElapsedTime(
            baseline.Timestamp,
            sample.Timestamp);
        if (elapsed <= TimeSpan.Zero)
        {
            return false;
        }

        var frameDelta = sample.Frame - baseline.Frame;
        var outputTimeDelta =
            sample.OutputTimeMicroseconds -
            baseline.OutputTimeMicroseconds;
        if (frameDelta < 0 || outputTimeDelta < 0)
        {
            return false;
        }

        var expectedFrames =
            _targetFramesPerSecond * elapsed.TotalSeconds;
        var outputFrameRateRatio = expectedFrames > 0
            ? frameDelta / expectedFrames
            : 0;
        var outputSpeed =
            outputTimeDelta / 1_000_000d / elapsed.TotalSeconds;
        return outputFrameRateRatio >= 0.85 &&
               outputSpeed >= 0.85;
    }

    private bool IsRapidQueuedProgressCatchUp(
        TimeSpan receiptInterval,
        long frameDelta,
        long outputTimeDelta)
    {
        if (receiptInterval <= TimeSpan.Zero ||
            receiptInterval > MaximumQueuedProgressReceiptInterval ||
            frameDelta <= 0 ||
            outputTimeDelta <= 0)
        {
            return false;
        }

        var expectedFrames =
            _targetFramesPerSecond * receiptInterval.TotalSeconds;
        var outputFrameRateRatio = expectedFrames > 0
            ? frameDelta / expectedFrames
            : 0;
        var outputSpeed =
            outputTimeDelta / 1_000_000d /
            receiptInterval.TotalSeconds;
        return outputFrameRateRatio >= 1.5 &&
               outputSpeed >= 1.5;
    }

    private void RebaseObservationsAfterProgressBacklog(
        CaptureProgressSample sample,
        CaptureForegroundContext context)
    {
        _observations.Clear();
        _observations.Enqueue(new Observation(sample, context));
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
        // A baseline used to distinguish later degradation must be clearly
        // above the initial low-cadence promotion band (<=55% of target).
        return uniqueFramesPerSecond >= _targetFramesPerSecond * 0.70;
    }

    private CaptureStarvationAssessment? AssessWindow(
        CaptureProgressSample sample,
        CaptureForegroundContext context,
        TimeSpan confirmationWindow,
        TimeSpan minimumInputEvidenceSpan,
        double minimumDuplicateRatio,
        double maximumUniqueFrameRateRatio,
        CaptureStarvationKind kind)
    {
        if (!TryGetEligibleWindow(
                sample,
                context,
                confirmationWindow,
                minimumInputEvidenceSpan,
                out var baseline,
                out var window,
                out var elapsed))
        {
            return null;
        }

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
            elapsed,
            kind,
            UsedCustomFullscreenFallback:
                window.Any(observation =>
                    observation.Context.UsedCustomFullscreenFallback));
    }

    private CaptureStarvationAssessment? AssessBurstPressure(
        CaptureProgressSample sample,
        CaptureForegroundContext context)
    {
        if (!TryGetEligibleWindow(
                sample,
                context,
                BurstPressureWindow,
                MinimumInputEvidenceSpan,
                out _,
                out var window,
                out var elapsed))
        {
            return null;
        }

        var qualifyingIntervals = 0;
        var stalledDuration = TimeSpan.Zero;
        long totalFrames = 0;
        long totalDuplicates = 0;
        for (var index = 1; index < window.Length; index++)
        {
            var previous = window[index - 1].Sample;
            var current = window[index].Sample;
            var interval = Stopwatch.GetElapsedTime(previous.Timestamp, current.Timestamp);
            var frameDelta = current.Frame - previous.Frame;
            var duplicateDelta = current.DuplicatedFrames - previous.DuplicatedFrames;
            if (interval < TimeSpan.FromMilliseconds(150) ||
                interval > TimeSpan.FromSeconds(2) ||
                frameDelta <= 0 ||
                duplicateDelta < 0)
            {
                continue;
            }

            totalFrames += frameDelta;
            totalDuplicates += duplicateDelta;
            var duplicateRatio = duplicateDelta / (double)frameDelta;
            var uniqueFramesPerSecond =
                Math.Max(0, frameDelta - duplicateDelta) / interval.TotalSeconds;
            if (duplicateRatio >= 0.85 &&
                uniqueFramesPerSecond <= Math.Max(4, _targetFramesPerSecond * 0.20))
            {
                qualifyingIntervals++;
                stalledDuration += interval;
            }
        }

        if (qualifyingIntervals < 2 ||
            stalledDuration < TimeSpan.FromMilliseconds(400) ||
            totalFrames <= 0 ||
            totalDuplicates / (double)totalFrames < 0.10)
        {
            return null;
        }

        var baseline = window[0].Sample;
        var uniqueFrames = Math.Max(
            0,
            sample.Frame - baseline.Frame -
            (sample.DuplicatedFrames - baseline.DuplicatedFrames));
        return new CaptureStarvationAssessment(
            totalDuplicates / (double)totalFrames,
            uniqueFrames / elapsed.TotalSeconds,
            elapsed,
            CaptureStarvationKind.SchedulingPressure,
            UsedCustomFullscreenFallback:
                window.Any(observation =>
                    observation.Context.UsedCustomFullscreenFallback));
    }

    private CaptureStarvationAssessment? AssessOutputThroughputPressure(
        CaptureProgressSample sample,
        CaptureForegroundContext context)
    {
        if (!TryGetEligibleWindow(
                sample,
                context,
                ConfirmationWindow,
                MinimumInputEvidenceSpan,
                out var baseline,
                out var window,
                out var elapsed))
        {
            return null;
        }

        var frameDelta = sample.Frame - baseline.Sample.Frame;
        var duplicateDelta = sample.DuplicatedFrames - baseline.Sample.DuplicatedFrames;
        var mediaTimeDelta =
            sample.OutputTimeMicroseconds - baseline.Sample.OutputTimeMicroseconds;
        if (frameDelta <= 0 || duplicateDelta < 0 || mediaTimeDelta <= 0)
        {
            return null;
        }

        var expectedFrames = _targetFramesPerSecond * elapsed.TotalSeconds;
        var outputFrameRateRatio = frameDelta / expectedFrames;
        var outputSpeedRatio =
            mediaTimeDelta / 1_000_000d / elapsed.TotalSeconds;
        if (outputFrameRateRatio >= 0.85 && outputSpeedRatio >= 0.85)
        {
            return null;
        }

        return new CaptureStarvationAssessment(
            duplicateDelta / (double)frameDelta,
            Math.Max(0, frameDelta - duplicateDelta) / elapsed.TotalSeconds,
            elapsed,
            CaptureStarvationKind.OutputThroughput,
            Math.Min(outputFrameRateRatio, outputSpeedRatio),
            window.Any(observation =>
                observation.Context.UsedCustomFullscreenFallback));
    }

    private bool TryGetEligibleWindow(
        CaptureProgressSample sample,
        CaptureForegroundContext context,
        TimeSpan confirmationWindow,
        TimeSpan minimumInputEvidenceSpan,
        out Observation baseline,
        out Observation[] window,
        out TimeSpan elapsed)
    {
        baseline = default!;
        window = [];
        elapsed = TimeSpan.Zero;
        var candidate = FindBaseline(sample.Timestamp, confirmationWindow);
        if (candidate is null)
        {
            return false;
        }

        baseline = candidate;
        elapsed = Stopwatch.GetElapsedTime(baseline.Sample.Timestamp, sample.Timestamp);
        if (elapsed < confirmationWindow)
        {
            return false;
        }

        var baselineTimestamp = baseline.Sample.Timestamp;
        window = _observations
            .Where(observation => observation.Sample.Timestamp >= baselineTimestamp)
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
                inputObservations[^1].Sample.Timestamp) < minimumInputEvidenceSpan)
        {
            return false;
        }

        // HasRecentInput remains true for several seconds after a single input.
        // Broad, time-separated evidence prevents a paused cutscene or static
        // fullscreen menu from being mistaken for capture starvation.
        return true;
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

    private sealed record PendingProgressGap(
        CaptureProgressSample Baseline,
        CaptureStarvationAssessment Assessment,
        int CatchUpSamples);
}
