using System.Runtime.InteropServices;
using ClipForge.Models;
using NAudio.CoreAudioApi;
using Forms = System.Windows.Forms;

namespace ClipForge.Services;

/// <summary>
/// Enumerates displays and active Windows audio endpoints used by the capture UI.
/// </summary>
public sealed class DeviceDiscoveryService
{
    public IReadOnlyList<DisplayOption> GetDisplays()
    {
        var dxgiOutputs = DxgiDisplayDiscovery.GetOutputs();
        return Forms.Screen.AllScreens
            .Select((screen, index) => new DisplayOption(
                screen.DeviceName,
                screen.Primary ? $"Display {index + 1} (Primary)" : $"Display {index + 1}",
                screen.Bounds.Left,
                screen.Bounds.Top,
                screen.Bounds.Width,
                screen.Bounds.Height,
                screen.Primary,
                index,
                TryGetDisplayRefreshRate(screen.DeviceName)))
            .Select(display => display with
            {
                DesktopDuplicationTarget =
                    DxgiDisplayDiscovery.ResolveTarget(display, dxgiOutputs)
            })
            .OrderByDescending(display => display.IsPrimary)
            .ThenBy(display => display.Left)
            .ThenBy(display => display.Top)
            .ToArray();
    }

    public IReadOnlyList<AudioDeviceOption> GetOutputDevices() =>
        GetAudioDevices(DataFlow.Render);

    public IReadOnlyList<AudioDeviceOption> GetMicrophones() =>
        GetAudioDevices(DataFlow.Capture);

    private static IReadOnlyList<AudioDeviceOption> GetAudioDevices(DataFlow dataFlow)
    {
        using var enumerator = new MMDeviceEnumerator();
        var defaultDeviceId = TryGetDefaultDeviceId(enumerator, dataFlow);
        var devices = new List<AudioDeviceOption>();

        var endpoints = enumerator.EnumerateAudioEndPoints(dataFlow, DeviceState.Active);
        foreach (var endpoint in endpoints)
        {
            using (endpoint)
            {
                devices.Add(new AudioDeviceOption(
                    endpoint.ID,
                    endpoint.FriendlyName,
                    string.Equals(endpoint.ID, defaultDeviceId, StringComparison.OrdinalIgnoreCase)));
            }
        }

        return devices
            .OrderByDescending(device => device.IsDefault)
            .ThenBy(device => device.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    private static string? TryGetDefaultDeviceId(MMDeviceEnumerator enumerator, DataFlow dataFlow)
    {
        try
        {
            using var defaultDevice = enumerator.GetDefaultAudioEndpoint(dataFlow, Role.Multimedia);
            return defaultDevice.ID;
        }
        catch (COMException)
        {
            // Windows throws when no active default endpoint exists.
            return null;
        }
    }

    internal static int TryGetDisplayRefreshRate(string? deviceName)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(deviceName))
        {
            return 0;
        }

        IntPtr deviceContext = IntPtr.Zero;
        try
        {
            deviceContext = CreateDC("DISPLAY", deviceName, null, IntPtr.Zero);
            if (deviceContext == IntPtr.Zero)
            {
                return 0;
            }

            var refreshRate = GetDeviceCaps(deviceContext, VerticalRefreshRate);
            return refreshRate is >= 24 and <= 1000
                ? refreshRate
                : 0;
        }
        catch (Exception exception) when (
            exception is DllNotFoundException or EntryPointNotFoundException or
                TypeLoadException or MarshalDirectiveException)
        {
            return 0;
        }
        finally
        {
            if (deviceContext != IntPtr.Zero)
            {
                _ = DeleteDC(deviceContext);
            }
        }
    }

    private const int VerticalRefreshRate = 116;

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateDC(
        string driver,
        string deviceName,
        string? output,
        IntPtr initializationData);

    [DllImport("gdi32.dll")]
    private static extern int GetDeviceCaps(IntPtr deviceContext, int index);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(IntPtr deviceContext);
}
