namespace ClipForge.Capture;

internal enum CaptureRecoveryReason
{
    SourceStarvation,
    CaptureHang
}

internal sealed class CaptureRecoveryRequestedEventArgs(
    CaptureRecoveryReason reason,
    string diagnostic,
    int? processId) : EventArgs
{
    public CaptureRecoveryReason Reason { get; } = reason;

    public string Diagnostic { get; } = diagnostic;

    public int? ProcessId { get; } = processId;
}
