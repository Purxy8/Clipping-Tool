using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using ClipForge.Models;

namespace ClipForge.Services;

public enum GlobalHotkeyAction
{
    SaveClip,
    ToggleOverlay
}

public sealed class GlobalHotkeyPressedEventArgs(
    GlobalHotkeyAction action,
    HotkeyGesture gesture) : EventArgs
{
    public GlobalHotkeyAction Action { get; } = action;

    public HotkeyGesture Gesture { get; } = gesture;
}

public sealed class GlobalHotkeyRegistrationFailedEventArgs(Exception error) : EventArgs
{
    public Exception Error { get; } = error;
}

/// <summary>
/// Describes a failure returned by Win32 while registering one of ClipForge's hotkeys.
/// </summary>
public sealed class GlobalHotkeyRegistrationException : Win32Exception
{
    private const int ErrorHotkeyAlreadyRegistered = 1409;

    public GlobalHotkeyRegistrationException(
        GlobalHotkeyAction action,
        HotkeyGesture gesture,
        int nativeErrorCode)
        : base(nativeErrorCode, CreateMessage(action, gesture, nativeErrorCode))
    {
        Action = action;
        Gesture = gesture;
    }

    public GlobalHotkeyAction Action { get; }

    public HotkeyGesture Gesture { get; }

    public bool IsConflict => NativeErrorCode == ErrorHotkeyAlreadyRegistered;

    private static string CreateMessage(
        GlobalHotkeyAction action,
        HotkeyGesture gesture,
        int nativeErrorCode)
    {
        var actionName = action == GlobalHotkeyAction.SaveClip ? "Save Clip" : "Toggle Overlay";
        var reason = nativeErrorCode == ErrorHotkeyAlreadyRegistered
            ? "Another application is already using this shortcut."
            : new Win32Exception(nativeErrorCode).Message;
        return $"Could not register {gesture.DisplayText} for {actionName}. {reason}";
    }
}

/// <summary>
/// Owns the two system-wide ClipForge shortcuts and routes WM_HOTKEY without keyboard hooks.
/// </summary>
public sealed class GlobalHotkeyService : IDisposable
{
    private const int FirstHotkeyId = 0x4C46;
    private const int LastHotkeyId = FirstHotkeyId + 0xFF;
    private const int WmHotkey = 0x0312;
    private const uint ModNoRepeat = 0x4000;

    private readonly Dictionary<int, RegisteredHotkey> _registrations = [];
    private readonly HashSet<int> _ownedRegistrationIds = [];

    private Window? _window;
    private HwndSource? _source;
    private HotkeyConfiguration _configuration = HotkeyConfiguration.Default;
    private int _nextHotkeyId = FirstHotkeyId;
    private bool _disposed;

    /// <summary>
    /// Compatibility alias for the original v1 event. It fires only for Save Clip.
    /// </summary>
    public event EventHandler? Pressed;

    public event EventHandler? SaveClipPressed;

    public event EventHandler? ToggleOverlayPressed;

    public event EventHandler<GlobalHotkeyPressedEventArgs>? HotkeyPressed;

    /// <summary>
    /// Surfaces deferred registration failures when Register(Window, ...) is called before HWND creation.
    /// Synchronous registration and re-registration failures are thrown to the caller instead.
    /// </summary>
    public event EventHandler<GlobalHotkeyRegistrationFailedEventArgs>? RegistrationFailed;

    public bool IsRegistered => IsSaveClipRegistered && IsToggleOverlayRegistered;

    public bool IsSaveClipRegistered =>
        _registrations.Values.Any(registration => registration.Action == GlobalHotkeyAction.SaveClip);

    public bool IsToggleOverlayRegistered =>
        _registrations.Values.Any(registration => registration.Action == GlobalHotkeyAction.ToggleOverlay);

    public HotkeyGesture SaveClipHotkey => _configuration.SaveClip;

    public HotkeyGesture ToggleOverlayHotkey => _configuration.ToggleOverlay;

    public Exception? LastRegistrationError { get; private set; }

    public void Register(Window window) =>
        Register(window, HotkeyGesture.DefaultSaveClip, HotkeyGesture.DefaultToggleOverlay);

    public void Register(
        Window window,
        HotkeyGesture saveClipHotkey,
        HotkeyGesture toggleOverlayHotkey)
    {
        ArgumentNullException.ThrowIfNull(window);
        ThrowIfDisposed();
        EnsureNotAttached();
        window.Dispatcher.VerifyAccess();

        var configuration = ValidateConfiguration(saveClipHotkey, toggleOverlayHotkey);
        _configuration = configuration;
        _window = window;
        window.SourceInitialized += OnWindowSourceInitialized;
        window.Closed += OnWindowClosed;

        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        try
        {
            var source = HwndSource.FromHwnd(handle)
                ?? throw new InvalidOperationException("The window does not have an HwndSource.");
            AttachSource(source, configuration);
        }
        catch (Exception exception)
        {
            LastRegistrationError = exception;
            Unregister();
            throw;
        }
    }

    public void Register(HwndSource source) =>
        Register(source, HotkeyGesture.DefaultSaveClip, HotkeyGesture.DefaultToggleOverlay);

    public void Register(
        HwndSource source,
        HotkeyGesture saveClipHotkey,
        HotkeyGesture toggleOverlayHotkey)
    {
        ArgumentNullException.ThrowIfNull(source);
        ThrowIfDisposed();
        EnsureNotAttached();
        source.Dispatcher.VerifyAccess();

        var configuration = ValidateConfiguration(saveClipHotkey, toggleOverlayHotkey);
        _configuration = configuration;

        try
        {
            AttachSource(source, configuration);
        }
        catch (Exception exception)
        {
            LastRegistrationError = exception;
            Unregister();
            throw;
        }
    }

    /// <summary>
    /// Replaces both bindings as one operation. If either new gesture conflicts, the previous pair remains active.
    /// </summary>
    public void ReRegister(
        HotkeyGesture saveClipHotkey,
        HotkeyGesture toggleOverlayHotkey)
    {
        ThrowIfDisposed();
        var configuration = ValidateConfiguration(saveClipHotkey, toggleOverlayHotkey);

        if (_source is null)
        {
            if (_window is null)
            {
                throw new InvalidOperationException("Register the global hotkey service before changing its hotkeys.");
            }

            _window.Dispatcher.VerifyAccess();
            _configuration = configuration;
            LastRegistrationError = null;
            return;
        }

        _source.Dispatcher.VerifyAccess();

        try
        {
            ApplyConfigurationAtomically(configuration);
        }
        catch (Exception exception)
        {
            LastRegistrationError = exception;
            throw;
        }
    }

    public bool TryReRegister(
        HotkeyGesture saveClipHotkey,
        HotkeyGesture toggleOverlayHotkey,
        out GlobalHotkeyRegistrationException? error)
    {
        try
        {
            ReRegister(saveClipHotkey, toggleOverlayHotkey);
            error = null;
            return true;
        }
        catch (GlobalHotkeyRegistrationException exception)
        {
            error = exception;
            return false;
        }
    }

    public void Unregister()
    {
        DetachWindow();

        if (_source is not null)
        {
            foreach (var id in _ownedRegistrationIds.ToArray())
            {
                UnregisterOwnedHotkey(id);
            }

            try
            {
                _source.RemoveHook(WindowMessageHook);
            }
            catch (ObjectDisposedException)
            {
                // Destroying the HWND also releases its registered hotkeys.
            }
        }

        _registrations.Clear();
        _ownedRegistrationIds.Clear();
        _source = null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Unregister();
        _disposed = true;
    }

    private void OnWindowSourceInitialized(object? sender, EventArgs e)
    {
        if (_window is null || _source is not null)
        {
            return;
        }

        try
        {
            var handle = new WindowInteropHelper(_window).Handle;
            var source = HwndSource.FromHwnd(handle)
                ?? throw new InvalidOperationException("The window does not have an HwndSource.");
            AttachSource(source, _configuration);
        }
        catch (Exception exception)
        {
            LastRegistrationError = exception;
            Unregister();
            RegistrationFailed?.Invoke(this, new GlobalHotkeyRegistrationFailedEventArgs(exception));
        }
    }

    private void OnWindowClosed(object? sender, EventArgs e) => Unregister();

    private IntPtr WindowMessageHook(
        IntPtr hwnd,
        int message,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        if (message != WmHotkey)
        {
            return IntPtr.Zero;
        }

        var id = wParam.ToInt32();
        if (_registrations.TryGetValue(id, out var registration))
        {
            handled = true;
            RaisePressed(registration);
        }
        else if (_ownedRegistrationIds.Contains(id))
        {
            // A retired registration that Win32 has not released should not leak into the window.
            handled = true;
        }

        return IntPtr.Zero;
    }

    private void RaisePressed(RegisteredHotkey registration)
    {
        var eventArgs = new GlobalHotkeyPressedEventArgs(registration.Action, registration.Gesture);

        if (registration.Action == GlobalHotkeyAction.SaveClip)
        {
            SaveClipPressed?.Invoke(this, EventArgs.Empty);
            Pressed?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            ToggleOverlayPressed?.Invoke(this, EventArgs.Empty);
        }

        HotkeyPressed?.Invoke(this, eventArgs);
    }

    private void EnsureNotAttached()
    {
        if (_window is not null || _source is not null || _registrations.Count != 0)
        {
            throw new InvalidOperationException("This global hotkey service is already attached to a window.");
        }
    }

    private void AttachSource(HwndSource source, HotkeyConfiguration configuration)
    {
        if (source.Handle == IntPtr.Zero)
        {
            throw new InvalidOperationException("The window source does not have a valid handle.");
        }

        source.AddHook(WindowMessageHook);
        _source = source;

        try
        {
            ApplyConfigurationAtomically(configuration);
        }
        catch
        {
            foreach (var id in _ownedRegistrationIds.ToArray())
            {
                UnregisterOwnedHotkey(id);
            }

            _registrations.Clear();
            source.RemoveHook(WindowMessageHook);
            _source = null;
            throw;
        }
    }

    private void ApplyConfigurationAtomically(HotkeyConfiguration configuration)
    {
        if (_source is null)
        {
            throw new InvalidOperationException("The global hotkey service is not attached to a window source.");
        }

        var desiredRegistrations = new[]
        {
            new RegisteredHotkey(GlobalHotkeyAction.SaveClip, configuration.SaveClip),
            new RegisteredHotkey(GlobalHotkeyAction.ToggleOverlay, configuration.ToggleOverlay)
        };
        var nextRegistrations = new Dictionary<int, RegisteredHotkey>(2);
        var stagedIds = new List<int>(2);

        try
        {
            foreach (var desired in desiredRegistrations)
            {
                if (TryFindRegistration(desired.Gesture, out var existingId))
                {
                    nextRegistrations.Add(existingId, desired);
                    continue;
                }

                var id = AllocateHotkeyId();
                var modifiers = checked((uint)desired.Gesture.Modifiers) | ModNoRepeat;
                var virtualKey = desired.Gesture.GetVirtualKey();

                if (!RegisterHotKey(_source.Handle, id, modifiers, virtualKey))
                {
                    throw new GlobalHotkeyRegistrationException(
                        desired.Action,
                        desired.Gesture,
                        Marshal.GetLastWin32Error());
                }

                _ownedRegistrationIds.Add(id);
                stagedIds.Add(id);
                nextRegistrations.Add(id, desired);
            }
        }
        catch
        {
            foreach (var id in stagedIds)
            {
                UnregisterOwnedHotkey(id);
            }

            throw;
        }

        // New registrations are live before old registrations are retired. This guarantees that a
        // failed registration cannot leave the service with only one action or no working hotkeys.
        foreach (var obsoleteId in _registrations.Keys.Except(nextRegistrations.Keys).ToArray())
        {
            UnregisterOwnedHotkey(obsoleteId);
        }

        _registrations.Clear();
        foreach (var registration in nextRegistrations)
        {
            _registrations.Add(registration.Key, registration.Value);
        }

        _configuration = configuration;
        LastRegistrationError = null;
    }

    private bool TryFindRegistration(HotkeyGesture gesture, out int id)
    {
        foreach (var registration in _registrations)
        {
            if (registration.Value.Gesture == gesture)
            {
                id = registration.Key;
                return true;
            }
        }

        id = default;
        return false;
    }

    private int AllocateHotkeyId()
    {
        var candidate = _nextHotkeyId;

        for (var attempt = 0; attempt <= LastHotkeyId - FirstHotkeyId; attempt++)
        {
            _nextHotkeyId = candidate == LastHotkeyId ? FirstHotkeyId : candidate + 1;

            if (!_ownedRegistrationIds.Contains(candidate))
            {
                return candidate;
            }

            candidate = _nextHotkeyId;
        }

        throw new InvalidOperationException("ClipForge exhausted its global hotkey identifiers.");
    }

    private void UnregisterOwnedHotkey(int id)
    {
        if (_source is null || !_ownedRegistrationIds.Contains(id))
        {
            return;
        }

        if (UnregisterHotKey(_source.Handle, id))
        {
            _ownedRegistrationIds.Remove(id);
        }
    }

    private void DetachWindow()
    {
        if (_window is null)
        {
            return;
        }

        _window.SourceInitialized -= OnWindowSourceInitialized;
        _window.Closed -= OnWindowClosed;
        _window = null;
    }

    private static HotkeyConfiguration ValidateConfiguration(
        HotkeyGesture saveClipHotkey,
        HotkeyGesture toggleOverlayHotkey)
    {
        ArgumentNullException.ThrowIfNull(saveClipHotkey);
        ArgumentNullException.ThrowIfNull(toggleOverlayHotkey);

        if (!saveClipHotkey.TryValidate(out var saveError))
        {
            throw new ArgumentException($"The Save Clip hotkey is invalid. {saveError}", nameof(saveClipHotkey));
        }

        if (!toggleOverlayHotkey.TryValidate(out var overlayError))
        {
            throw new ArgumentException(
                $"The Toggle Overlay hotkey is invalid. {overlayError}",
                nameof(toggleOverlayHotkey));
        }

        if (saveClipHotkey == toggleOverlayHotkey)
        {
            throw new ArgumentException(
                "Save Clip and Toggle Overlay must use different hotkeys.",
                nameof(toggleOverlayHotkey));
        }

        return new HotkeyConfiguration(saveClipHotkey, toggleOverlayHotkey);
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(
        IntPtr windowHandle,
        int id,
        uint modifiers,
        uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr windowHandle, int id);

    private sealed record RegisteredHotkey(GlobalHotkeyAction Action, HotkeyGesture Gesture);

    private sealed record HotkeyConfiguration(HotkeyGesture SaveClip, HotkeyGesture ToggleOverlay)
    {
        public static HotkeyConfiguration Default { get; } = new(
            HotkeyGesture.DefaultSaveClip,
            HotkeyGesture.DefaultToggleOverlay);
    }
}
