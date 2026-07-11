using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ClipForge.Services;

/// <summary>
/// Registers Ctrl+Shift+F10 as the system-wide shortcut for saving a replay.
/// </summary>
public sealed class GlobalHotkeyService : IDisposable
{
    private const int HotkeyId = 0x4C46;
    private const int WmHotkey = 0x0312;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModNoRepeat = 0x4000;
    private const uint VkF10 = 0x79;

    private Window? _window;
    private HwndSource? _source;
    private bool _disposed;

    public event EventHandler? Pressed;

    public bool IsRegistered { get; private set; }

    public void Register(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        ThrowIfDisposed();
        EnsureNotAttached();

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
            RegisterSource(source);
        }
        catch
        {
            DetachWindow();
            throw;
        }
    }

    public void Register(HwndSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        ThrowIfDisposed();
        EnsureNotAttached();
        RegisterSource(source);
    }

    public void Unregister()
    {
        DetachWindow();

        if (_source is null)
        {
            IsRegistered = false;
            return;
        }

        if (IsRegistered)
        {
            _ = UnregisterHotKey(_source.Handle, HotkeyId);
        }

        _source.RemoveHook(WindowMessageHook);
        _source = null;
        IsRegistered = false;
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
        if (_window is null || IsRegistered)
        {
            return;
        }

        var handle = new WindowInteropHelper(_window).Handle;
        var source = HwndSource.FromHwnd(handle)
            ?? throw new InvalidOperationException("The window does not have an HwndSource.");
        RegisterSource(source);
    }

    private void OnWindowClosed(object? sender, EventArgs e) => Unregister();

    private IntPtr WindowMessageHook(
        IntPtr hwnd,
        int message,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        if (message == WmHotkey && wParam.ToInt32() == HotkeyId)
        {
            handled = true;
            Pressed?.Invoke(this, EventArgs.Empty);
        }

        return IntPtr.Zero;
    }

    private void EnsureNotAttached()
    {
        if (_window is not null || _source is not null || IsRegistered)
        {
            throw new InvalidOperationException("A global hotkey is already registered by this service.");
        }
    }

    private void RegisterSource(HwndSource source)
    {
        source.AddHook(WindowMessageHook);

        if (!RegisterHotKey(source.Handle, HotkeyId, ModControl | ModShift | ModNoRepeat, VkF10))
        {
            source.RemoveHook(WindowMessageHook);
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Could not register Ctrl+Shift+F10. Another application may already use it.");
        }

        _source = source;
        IsRegistered = true;
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

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr windowHandle, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr windowHandle, int id);
}
