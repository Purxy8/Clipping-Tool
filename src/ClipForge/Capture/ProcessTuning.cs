using System.ComponentModel;

namespace ClipForge.Capture;

internal static class ProcessTuning
{
    internal const ProcessPriorityClass CapturePriority = ProcessPriorityClass.BelowNormal;
    internal const ProcessPriorityClass HardwareCapturePriority = ProcessPriorityClass.Normal;
    internal const ProcessPriorityClass AuxiliaryMediaPriority = ProcessPriorityClass.Idle;

    public static bool TryApplyLowImpactPriority(Process process)
        => TryApplyPriority(process, CapturePriority);

    public static bool TryApplyCapturePriority(
        Process process,
        VideoEncodingStrategy strategy)
    {
        ArgumentNullException.ThrowIfNull(strategy);
        var priority = strategy.CaptureBackend == DesktopCaptureBackend.WindowsGraphicsCapture &&
                       strategy.IsHardwareEncoder &&
                       !strategy.RequiresSystemMemoryTransfer
            ? HardwareCapturePriority
            : CapturePriority;
        return TryApplyPriority(process, priority);
    }

    public static bool TryApplyAuxiliaryMediaPriority(Process process)
        => TryApplyPriority(process, AuxiliaryMediaPriority);

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
}
