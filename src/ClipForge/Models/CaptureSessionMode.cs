namespace ClipForge.Models;

/// <summary>
/// Identifies the single capture owner that ClipForge is currently running.
/// Instant Replay and Recorder deliberately share one capture engine so they
/// can never compete for the desktop, audio endpoints, encoder, or overlay.
/// </summary>
public enum CaptureSessionMode
{
    InstantReplay,
    Recording
}
