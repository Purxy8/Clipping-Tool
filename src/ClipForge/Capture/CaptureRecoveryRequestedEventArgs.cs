namespace ClipForge.Capture;

internal enum CaptureRecoveryReason
{
    SourceStarvation,
    CaptureHang,
    ScheduledRefresh
}

/// <summary>
/// Serializes capture-recovery notifications without allowing an exhausted
/// fault-recovery budget to block routine process renewal forever.
/// </summary>
internal sealed class CaptureRecoveryRequestGate
{
    private readonly object _sync = new();
    private long _nextRequestId;
    private long _pendingRequestId;
    private CaptureRecoveryReason? _pendingReason;
    private bool _faultsSuppressed;
    private bool _scheduledRefreshQueued;

    public bool IsPending
    {
        get
        {
            lock (_sync)
            {
                return _pendingRequestId != 0;
            }
        }
    }

    public bool TryBegin(
        CaptureRecoveryReason reason,
        out long requestId,
        out CaptureRecoveryReason? pendingReason)
    {
        lock (_sync)
        {
            if (_pendingRequestId != 0 ||
                reason != CaptureRecoveryReason.ScheduledRefresh && _faultsSuppressed)
            {
                if (_pendingRequestId != 0 &&
                    reason == CaptureRecoveryReason.ScheduledRefresh &&
                    _pendingReason != CaptureRecoveryReason.ScheduledRefresh)
                {
                    _scheduledRefreshQueued = true;
                }

                requestId = 0;
                pendingReason = _pendingReason;
                return false;
            }

            requestId = checked(++_nextRequestId);
            _pendingRequestId = requestId;
            _pendingReason = reason;
            pendingReason = null;
            return true;
        }
    }

    public bool Complete(long requestId, out bool scheduledRefreshQueued)
    {
        lock (_sync)
        {
            if (_pendingRequestId != requestId)
            {
                scheduledRefreshQueued = false;
                return false;
            }

            _pendingRequestId = 0;
            _pendingReason = null;
            scheduledRefreshQueued = _scheduledRefreshQueued;
            _scheduledRefreshQueued = false;
            return true;
        }
    }

    public bool SuppressFaults(long requestId)
    {
        lock (_sync)
        {
            if (_pendingRequestId != requestId)
            {
                return false;
            }

            // A ScheduledRefresh is deliberately still eligible. A single shared
            // pending bit previously remained set when MainWindow rejected a third
            // health recovery, which silently disabled every later 30-minute WGC
            // renewal in that replay session.
            _faultsSuppressed = true;
            return true;
        }
    }

    public void ResetForSession()
    {
        lock (_sync)
        {
            // Never reuse an id after reset: completion from a dispatcher callback
            // belonging to the previous replay session must not release a new one.
            _nextRequestId = checked(_nextRequestId + 1);
            _faultsSuppressed = false;
            _pendingRequestId = 0;
            _pendingReason = null;
            _scheduledRefreshQueued = false;
        }
    }
}

internal sealed class CaptureRecoveryRequestedEventArgs(
    CaptureRecoveryReason reason,
    string diagnostic,
    int? processId,
    long requestId) : EventArgs
{
    public CaptureRecoveryReason Reason { get; } = reason;

    public string Diagnostic { get; } = diagnostic;

    public int? ProcessId { get; } = processId;

    internal long RequestId { get; } = requestId;
}
