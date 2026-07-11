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
        return Forms.Screen.AllScreens
            .Select((screen, index) => new DisplayOption(
                screen.DeviceName,
                screen.Primary ? $"Display {index + 1} (Primary)" : $"Display {index + 1}",
                screen.Bounds.Left,
                screen.Bounds.Top,
                screen.Bounds.Width,
                screen.Bounds.Height,
                screen.Primary))
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
}
