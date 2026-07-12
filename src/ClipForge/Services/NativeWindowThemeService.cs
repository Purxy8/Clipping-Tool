using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ClipForge.Services;

/// <summary>
/// Applies supported DWM colors to a window's native non-client frame without
/// replacing the system title bar or its accessibility behavior.
/// </summary>
internal sealed class NativeWindowThemeService : IDisposable
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaUseImmersiveDarkModeLegacy = 19;
    private const int DwmwaCaptionColor = 35;
    private const int DwmwaTextColor = 36;
    private const uint DwmColorDefault = 0xFFFFFFFF;

    private readonly Window _window;
    private bool _disposed;

    public NativeWindowThemeService(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        _window = window;
        _window.SourceInitialized += Window_SourceInitialized;
        SystemParameters.StaticPropertyChanged += SystemParameters_StaticPropertyChanged;

        // Also supports attachment after WPF has already created the HWND.
        ApplyCurrentTheme();
    }

    /// <summary>
    /// Creates a Win32 COLORREF value in 0x00BBGGRR byte order.
    /// </summary>
    internal static uint ToColorRef(byte red, byte green, byte blue) =>
        red | ((uint)green << 8) | ((uint)blue << 16);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _window.SourceInitialized -= Window_SourceInitialized;
        SystemParameters.StaticPropertyChanged -= SystemParameters_StaticPropertyChanged;
    }

    private void Window_SourceInitialized(object? sender, EventArgs e) => ApplyCurrentTheme();

    private void SystemParameters_StaticPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_disposed ||
            !string.Equals(e.PropertyName, nameof(SystemParameters.HighContrast), StringComparison.Ordinal))
        {
            return;
        }

        if (_window.Dispatcher.CheckAccess())
        {
            ApplyCurrentTheme();
            return;
        }

        if (!_window.Dispatcher.HasShutdownStarted && !_window.Dispatcher.HasShutdownFinished)
        {
            _ = _window.Dispatcher.BeginInvoke(ApplyCurrentTheme);
        }
    }

    private void ApplyCurrentTheme()
    {
        if (_disposed)
        {
            return;
        }

        var windowHandle = new WindowInteropHelper(_window).Handle;
        if (windowHandle == IntPtr.Zero)
        {
            return;
        }

        try
        {
            var useDarkFrame = SystemParameters.HighContrast ? 0 : 1;
            var darkModeResult = DwmSetWindowAttribute(
                windowHandle,
                DwmwaUseImmersiveDarkMode,
                ref useDarkFrame,
                sizeof(int));
            if (darkModeResult != 0)
            {
                _ = DwmSetWindowAttribute(
                    windowHandle,
                    DwmwaUseImmersiveDarkModeLegacy,
                    ref useDarkFrame,
                    sizeof(int));
            }

            // Explicit caption and text colors are supported on Windows 11.
            // Older supported Windows versions retain their system title-bar
            // colors if the best-effort immersive dark request is unavailable.
            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
            {
                return;
            }

            var captionColor = SystemParameters.HighContrast
                ? DwmColorDefault
                : ToColorRef(0, 0, 0);
            var textColor = SystemParameters.HighContrast
                ? DwmColorDefault
                : ToColorRef(255, 255, 255);

            _ = DwmSetWindowAttributeColor(
                windowHandle,
                DwmwaCaptionColor,
                ref captionColor,
                sizeof(uint));
            _ = DwmSetWindowAttributeColor(
                windowHandle,
                DwmwaTextColor,
                ref textColor,
                sizeof(uint));
        }
        catch (DllNotFoundException)
        {
            // Retain the native system frame if DWM is unavailable.
        }
        catch (EntryPointNotFoundException)
        {
            // Retain the native system frame on an unsupported Windows build.
        }
    }

    [DllImport("dwmapi.dll", ExactSpelling = true, PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(
        IntPtr windowHandle,
        int attribute,
        ref int attributeValue,
        int attributeSize);

    [DllImport(
        "dwmapi.dll",
        EntryPoint = "DwmSetWindowAttribute",
        ExactSpelling = true,
        PreserveSig = true)]
    private static extern int DwmSetWindowAttributeColor(
        IntPtr windowHandle,
        int attribute,
        ref uint attributeValue,
        int attributeSize);
}
