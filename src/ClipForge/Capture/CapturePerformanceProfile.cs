namespace ClipForge.Capture;

/// <summary>
/// Controls how aggressively a live WGC process competes for CPU/GPU time.
/// Native capture starts in the low-impact profile so ClipForge does not make
/// the foreground game lag. A measured capture-pressure event can promote only
/// that replay session to the resilient profile.
/// </summary>
internal enum CapturePerformanceProfile
{
    LowImpact,
    Resilient
}
