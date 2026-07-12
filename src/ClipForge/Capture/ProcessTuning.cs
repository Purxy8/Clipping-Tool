using System.ComponentModel;

namespace ClipForge.Capture;

internal static class ProcessTuning
{
    internal const ProcessPriorityClass CapturePriority = ProcessPriorityClass.BelowNormal;

    public static bool TryApplyLowImpactPriority(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);

        try
        {
            process.PriorityClass = CapturePriority;
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
