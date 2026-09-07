using System.Runtime.InteropServices;
using ClipForge.Capture;
using ClipForge.Models;

namespace ClipForge.Services;

internal sealed record DxgiOutputSnapshot(
    int AdapterIndex,
    int OutputIndex,
    long AdapterLuid,
    string DeviceName,
    int Left,
    int Top,
    int Right,
    int Bottom,
    bool AttachedToDesktop,
    int Rotation);

/// <summary>
/// Resolves adapter-local Desktop Duplication outputs without assuming that
/// WinForms screen order matches DXGI adapter/output enumeration order.
/// Discovery owns only short-lived DXGI interfaces; it creates no capture,
/// rendering device, display-mode change, or persistent native resource.
/// </summary>
internal static class DxgiDisplayDiscovery
{
    private const int DxgiErrorNotFound = unchecked((int)0x887A0002);
    private const int MaximumAdapters = 32;
    private const int MaximumOutputsPerAdapter = 32;
    internal const int IdentityRotation = 1;
    private static readonly Guid Factory1InterfaceId =
        new("770aae78-f26f-4dba-a829-253c83d1b387");

    internal static DxgiCaptureTarget? ResolveTarget(
        DisplayOption display,
        IReadOnlyList<DxgiOutputSnapshot> outputs)
    {
        ArgumentNullException.ThrowIfNull(display);
        ArgumentNullException.ThrowIfNull(outputs);
        if (string.IsNullOrWhiteSpace(display.DeviceName) ||
            display.Width < 2 || display.Height < 2)
        {
            return null;
        }

        DxgiOutputSnapshot? match = null;
        foreach (var output in outputs)
        {
            if (!output.AttachedToDesktop ||
                !string.Equals(output.DeviceName, display.DeviceName,
                    StringComparison.OrdinalIgnoreCase) ||
                output.Left != display.Left || output.Top != display.Top ||
                output.Right != (long)display.Left + display.Width ||
                output.Bottom != (long)display.Top + display.Height)
            {
                continue;
            }

            // Mirrored, stale, or inconsistent topology must never silently
            // select another physical output with a matching ordinal.
            if (match is not null)
            {
                return null;
            }

            match = output;
        }

        if (match is null || match.AdapterIndex < 0 || match.OutputIndex < 0 ||
            match.Rotation != IdentityRotation)
        {
            // DDA surfaces retain output rotation. Until the complete video
            // and cursor graph supports it, rotated outputs use another path.
            return null;
        }

        return new DxgiCaptureTarget(match.AdapterIndex, match.OutputIndex,
            match.AdapterLuid, match.DeviceName, match.Rotation);
    }

    internal static IReadOnlyList<DxgiOutputSnapshot> GetOutputs()
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        try
        {
            return EnumerateOutputs();
        }
        catch (Exception exception) when (exception is
            ExternalException or ArgumentException or InvalidOperationException or
            DllNotFoundException or EntryPointNotFoundException or
            TypeLoadException or MarshalDirectiveException or BadImageFormatException)
        {
            // Device removal, remote-session restrictions, and absent DXGI
            // make this backend unavailable; existing display discovery stays
            // usable. Never guess an adapter/output index after a failure.
            return [];
        }
    }

    private static IReadOnlyList<DxgiOutputSnapshot> EnumerateOutputs()
    {
        var outputs = new List<DxgiOutputSnapshot>();
        var factoryInterfaceId = Factory1InterfaceId;
        IntPtr factory = IntPtr.Zero;
        try
        {
            var result = CreateDXGIFactory1(ref factoryInterfaceId, out factory);
            if (result < 0 || factory == IntPtr.Zero)
            {
                return [];
            }

            // Slots are the Windows SDK dxgi.h COM vtable ABI, including the
            // three IUnknown and four IDXGIObject methods inherited first:
            // Factory1.EnumAdapters1=12, IsCurrent=13;
            // Adapter.EnumOutputs=7, Adapter1.GetDesc1=10; Output.GetDesc=7.
            var enumerateAdapters = Method<EnumerateInterface>(factory, 12);
            for (var adapterIndex = 0; adapterIndex < MaximumAdapters; adapterIndex++)
            {
                IntPtr adapter = IntPtr.Zero;
                try
                {
                    result = enumerateAdapters(factory, (uint)adapterIndex, out adapter);
                    if (result == DxgiErrorNotFound)
                    {
                        return Method<IsCurrent>(factory, 13)(factory) != 0
                            ? outputs : [];
                    }

                    if (result < 0 || adapter == IntPtr.Zero ||
                        Method<GetAdapterDescription>(adapter, 10)(adapter, out var description) < 0)
                    {
                        return [];
                    }

                    var adapterLuid = unchecked(
                        ((long)description.AdapterLuid.HighPart << 32) |
                        description.AdapterLuid.LowPart);
                    var enumerateOutputs = Method<EnumerateInterface>(adapter, 7);
                    var completedOutputEnumeration = false;
                    for (var outputIndex = 0; outputIndex < MaximumOutputsPerAdapter; outputIndex++)
                    {
                        IntPtr output = IntPtr.Zero;
                        try
                        {
                            result = enumerateOutputs(adapter, (uint)outputIndex, out output);
                            if (result == DxgiErrorNotFound)
                            {
                                completedOutputEnumeration = true;
                                break;
                            }

                            if (result < 0 || output == IntPtr.Zero ||
                                Method<GetOutputDescription>(output, 7)(output, out var outputDescription) < 0)
                            {
                                return [];
                            }

                            outputs.Add(new DxgiOutputSnapshot(
                                adapterIndex, outputIndex, adapterLuid,
                                outputDescription.DeviceName,
                                outputDescription.DesktopCoordinates.Left,
                                outputDescription.DesktopCoordinates.Top,
                                outputDescription.DesktopCoordinates.Right,
                                outputDescription.DesktopCoordinates.Bottom,
                                outputDescription.AttachedToDesktop != 0,
                                outputDescription.Rotation));
                        }
                        finally
                        {
                            Release(output);
                        }
                    }

                    if (!completedOutputEnumeration)
                    {
                        return [];
                    }
                }
                finally
                {
                    Release(adapter);
                }
            }

            // A truncated snapshot cannot prove that a later output does not
            // make a match ambiguous. Exhausting either bound fails closed.
            return [];
        }
        finally
        {
            Release(factory);
        }
    }

    private static T Method<T>(IntPtr instance, int slot) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(
            Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), checked(slot * IntPtr.Size)));

    private static void Release(IntPtr instance)
    {
        if (instance != IntPtr.Zero)
        {
            _ = Marshal.Release(instance);
        }
    }

    [DllImport("dxgi.dll", ExactSpelling = true)]
    private static extern int CreateDXGIFactory1(ref Guid interfaceId, out IntPtr factory);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int EnumerateInterface(IntPtr instance, uint index, out IntPtr result);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int IsCurrent(IntPtr instance);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetAdapterDescription(IntPtr instance, out AdapterDescription description);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetOutputDescription(IntPtr instance, out OutputDescription description);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeLuid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRectangle
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct AdapterDescription
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Description;
        public uint VendorId;
        public uint DeviceId;
        public uint SubSystemId;
        public uint Revision;
        public UIntPtr DedicatedVideoMemory;
        public UIntPtr DedicatedSystemMemory;
        public UIntPtr SharedSystemMemory;
        public NativeLuid AdapterLuid;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OutputDescription
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
        public NativeRectangle DesktopCoordinates;
        public int AttachedToDesktop;
        public int Rotation;
        public IntPtr Monitor;
    }
}
