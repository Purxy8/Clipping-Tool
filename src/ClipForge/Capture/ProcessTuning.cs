using System.ComponentModel;
using System.Runtime.InteropServices;

namespace ClipForge.Capture;

internal static class ProcessTuning
{
    internal const ProcessPriorityClass CapturePriority = ProcessPriorityClass.BelowNormal;
    internal const ProcessPriorityClass HardwareCapturePriority = ProcessPriorityClass.BelowNormal;
    internal const ProcessPriorityClass AuxiliaryMediaPriority = ProcessPriorityClass.Idle;
    internal const GraphicsSchedulingPriorityClass CaptureGraphicsPriority =
        GraphicsSchedulingPriorityClass.BelowNormal;

    public static bool TryApplyLowImpactPriority(Process process)
        => TryApplyPriority(process, CapturePriority);

    public static bool TryApplyCapturePriority(
        Process process,
        VideoEncodingStrategy strategy)
    {
        ArgumentNullException.ThrowIfNull(strategy);
        var priority = GetCaptureCpuPriority(strategy);
        var cpuPriorityApplied = TryApplyPriority(process, priority);
        var graphicsPriorityApplied = TryApplyGraphicsPriority(
            process,
            CaptureGraphicsPriority);
        return cpuPriorityApplied && graphicsPriorityApplied;
    }

    public static bool TryEnsureCapturePriority(
        Process process,
        VideoEncodingStrategy strategy)
    {
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(strategy);

        var expectedCpuPriority = GetCaptureCpuPriority(strategy);
        var cpuPriorityApplied =
            (TryReadPriority(process, out var observedCpuPriority) &&
             observedCpuPriority == expectedCpuPriority) ||
            TryApplyPriority(process, expectedCpuPriority);
        var graphicsPriorityApplied =
            (TryReadGraphicsPriority(process, out var observedGraphicsPriority) &&
             observedGraphicsPriority == CaptureGraphicsPriority) ||
            TryApplyGraphicsPriority(process, CaptureGraphicsPriority);
        return cpuPriorityApplied && graphicsPriorityApplied;
    }

    public static bool TryApplyAuxiliaryMediaPriority(Process process)
        => TryApplyPriority(process, AuxiliaryMediaPriority);

    internal static bool TryReadGraphicsPriority(
        Process process,
        out GraphicsSchedulingPriorityClass priority)
    {
        ArgumentNullException.ThrowIfNull(process);
        priority = GraphicsSchedulingPriorityClass.Normal;

        try
        {
            return NativeMethods.D3DKMTGetProcessSchedulingPriorityClass(
                process.Handle,
                out priority) >= 0;
        }
        catch (Exception exception) when (IsPriorityInteropFailure(exception))
        {
            return false;
        }
    }

    private static ProcessPriorityClass GetCaptureCpuPriority(VideoEncodingStrategy strategy) =>
        strategy.CaptureBackend == DesktopCaptureBackend.WindowsGraphicsCapture &&
        strategy.IsHardwareEncoder &&
        !strategy.RequiresSystemMemoryTransfer
            ? HardwareCapturePriority
            : CapturePriority;

    private static bool TryReadPriority(
        Process process,
        out ProcessPriorityClass priority)
    {
        priority = ProcessPriorityClass.Normal;
        try
        {
            // System.Diagnostics.Process caches some properties. Refresh before
            // comparing so the periodic repair observes priority changes made by
            // Windows, a driver, or another process handle.
            process.Refresh();
            priority = process.PriorityClass;
            return true;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            return false;
        }
    }

    private static bool TryApplyPriority(
        Process process,
        ProcessPriorityClass priority)
    {
        ArgumentNullException.ThrowIfNull(process);

        try
        {
            process.PriorityClass = priority;
            return true;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // Some locked-down Windows environments do not allow changing another
            // process' priority. Capture remains functional in that case.
            return false;
        }
    }

    internal static bool TryApplyGraphicsPriority(
        Process process,
        GraphicsSchedulingPriorityClass priority)
    {
        ArgumentNullException.ThrowIfNull(process);

        try
        {
            // WGC, D3D11 scaling and hardware encoding all submit work from the
            // FFmpeg process. Keeping that work below the foreground game and
            // DWM prevents the recorder from competing with pointer presentation
            // on high-refresh and mixed-refresh desktops.
            return NativeMethods.D3DKMTSetProcessSchedulingPriorityClass(
                process.Handle,
                priority) >= 0;
        }
        catch (Exception exception) when (IsPriorityInteropFailure(exception))
        {
            // This API is best-effort on locked-down or unsupported Windows
            // environments. Capture remains functional if Windows rejects it.
            return false;
        }
    }

    private static bool IsPriorityInteropFailure(Exception exception)
        => exception is InvalidOperationException or
            Win32Exception or
            NotSupportedException or
            DllNotFoundException or
            EntryPointNotFoundException or
            SEHException;

    private static class NativeMethods
    {
        [DllImport("gdi32.dll", ExactSpelling = true)]
        internal static extern int D3DKMTSetProcessSchedulingPriorityClass(
            IntPtr process,
            GraphicsSchedulingPriorityClass priority);

        [DllImport("gdi32.dll", ExactSpelling = true)]
        internal static extern int D3DKMTGetProcessSchedulingPriorityClass(
            IntPtr process,
            out GraphicsSchedulingPriorityClass priority);
    }
}

internal enum GraphicsSchedulingPriorityClass
{
    Idle = 0,
    BelowNormal = 1,
    Normal = 2,
    AboveNormal = 3,
    High = 4,
    Realtime = 5
}
