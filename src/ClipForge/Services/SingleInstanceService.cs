using System.Diagnostics;
using System.Security.Principal;

namespace ClipForge.Services;

/// <summary>
/// Ensures that one ClipForge process owns capture and global hotkeys in the
/// current Windows user session. A second launch can only request activation;
/// no command text or user-controlled data crosses the process boundary.
/// </summary>
public sealed class SingleInstanceService : IDisposable
{
    private const int SignalAttempts = 8;
    private static readonly TimeSpan SignalRetryDelay = TimeSpan.FromMilliseconds(50);

    private readonly Mutex _mutex;
    private readonly string _activationEventName;
    private readonly EventWaitHandle? _activationEvent;
    private readonly EventWaitHandle? _stopEvent;
    private Task? _listenerTask;
    private int _disposed;

    private SingleInstanceService(
        Mutex mutex,
        string activationEventName,
        bool isPrimary,
        EventWaitHandle? activationEvent,
        EventWaitHandle? stopEvent)
    {
        _mutex = mutex;
        _activationEventName = activationEventName;
        IsPrimary = isPrimary;
        _activationEvent = activationEvent;
        _stopEvent = stopEvent;
    }

    public event EventHandler? ActivationRequested;

    public bool IsPrimary { get; }

    public static SingleInstanceService Acquire()
    {
        var scope = BuildCurrentScope();
        var mutex = new Mutex(initiallyOwned: false, $"Local\\ClipForge.Instance.{scope}", out var createdNew);
        if (!createdNew)
        {
            return new SingleInstanceService(
                mutex,
                $"Local\\ClipForge.Activate.{scope}",
                isPrimary: false,
                activationEvent: null,
                stopEvent: null);
        }

        try
        {
            var activationEvent = new EventWaitHandle(
                initialState: false,
                EventResetMode.AutoReset,
                $"Local\\ClipForge.Activate.{scope}");
            var stopEvent = new EventWaitHandle(false, EventResetMode.ManualReset);
            return new SingleInstanceService(
                mutex,
                $"Local\\ClipForge.Activate.{scope}",
                isPrimary: true,
                activationEvent,
                stopEvent);
        }
        catch
        {
            mutex.Dispose();
            throw;
        }
    }

    public void StartListening()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!IsPrimary || _activationEvent is null || _stopEvent is null)
        {
            throw new InvalidOperationException("Only the primary ClipForge instance can listen for activation.");
        }

        _listenerTask ??= Task.Run(ListenForActivation);
    }

    public bool TrySignalPrimary()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (IsPrimary)
        {
            return false;
        }

        for (var attempt = 0; attempt < SignalAttempts; attempt++)
        {
            try
            {
                using var activationEvent = EventWaitHandle.OpenExisting(_activationEventName);
                return activationEvent.Set();
            }
            catch (WaitHandleCannotBeOpenedException) when (attempt + 1 < SignalAttempts)
            {
                Thread.Sleep(SignalRetryDelay);
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        return false;
    }

    /// <summary>
    /// Detects a pre-single-instance ClipForge process in this session so the
    /// first upgraded launch can fail clearly instead of fighting for hotkeys.
    /// </summary>
    public static bool HasLegacyClipForgeProcessInCurrentSession()
    {
        using var current = Process.GetCurrentProcess();
        foreach (var process in Process.GetProcessesByName("ClipForge"))
        {
            using (process)
            {
                try
                {
                    if (process.Id != current.Id && process.SessionId == current.SessionId)
                    {
                        return true;
                    }
                }
                catch (InvalidOperationException)
                {
                    // The candidate exited while it was being inspected.
                }
            }
        }

        return false;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _stopEvent?.Set();
        if (_listenerTask is not null)
        {
            try
            {
                _listenerTask.GetAwaiter().GetResult();
            }
            catch (ObjectDisposedException)
            {
                // Process shutdown already released a wait handle.
            }
        }

        _activationEvent?.Dispose();
        _stopEvent?.Dispose();
        _mutex.Dispose();
    }

    private void ListenForActivation()
    {
        var waitHandles = new WaitHandle[] { _activationEvent!, _stopEvent! };
        while (WaitHandle.WaitAny(waitHandles) == 0)
        {
            try
            {
                ActivationRequested?.Invoke(this, EventArgs.Empty);
            }
            catch
            {
                // A UI subscriber must not terminate the activation listener.
            }
        }
    }

    private static string BuildCurrentScope()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var userSid = identity.User?.Value
            ?? throw new InvalidOperationException("ClipForge could not identify the current Windows user.");
        using var process = Process.GetCurrentProcess();
        return $"{userSid}.Session-{process.SessionId}";
    }
}
