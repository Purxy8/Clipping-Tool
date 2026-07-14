using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ClipForge.Services;

/// <summary>
/// Owns a capture process through a Windows Job Object. Closing the owning
/// ClipForge process closes the job handle, so Windows terminates FFmpeg even
/// when the app is ended abruptly and cannot run its normal shutdown path.
/// </summary>
internal sealed class CaptureProcessJob : IDisposable
{
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;
    private SafeFileHandle? _jobHandle;

    private CaptureProcessJob(SafeFileHandle? jobHandle)
    {
        _jobHandle = jobHandle;
    }

    public static CaptureProcessJob Attach(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);

        // This project targets Windows, but keeping the helper inert elsewhere
        // preserves the behavior of platform-neutral service-level tests.
        if (!OperatingSystem.IsWindows())
        {
            return new CaptureProcessJob(jobHandle: null);
        }

        int processId;
        try
        {
            processId = process.Id;
        }
        catch (InvalidOperationException exception)
        {
            throw new InvalidOperationException(
                "The capture process must be started before lifetime ownership is attached.",
                exception);
        }

        // A programming error must never place ClipForge itself in a
        // kill-on-close job owned by this disposable helper.
        if (processId == Environment.ProcessId)
        {
            throw new InvalidOperationException(
                "ClipForge refused to attach its own process to the capture lifetime job.");
        }

        if (process.HasExited)
        {
            throw new InvalidOperationException(
                "The capture process exited before lifetime ownership could be attached.");
        }

        var jobHandle = CreateJobObjectW(IntPtr.Zero, jobName: null);
        if (jobHandle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            jobHandle.Dispose();
            throw new Win32Exception(
                error,
                "Windows could not create the capture lifetime job.");
        }

        try
        {
            var information = new JobObjectExtendedLimitInformation
            {
                BasicLimitInformation = new JobObjectBasicLimitInformation
                {
                    LimitFlags = JobObjectLimitKillOnJobClose
                }
            };

            if (!SetInformationJobObject(
                    jobHandle,
                    JobObjectInformationClass.ExtendedLimitInformation,
                    ref information,
                    (uint)Marshal.SizeOf<JobObjectExtendedLimitInformation>()))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Windows could not enable kill-on-close for the capture lifetime job.");
            }

            if (!AssignProcessToJobObject(jobHandle, process.Handle))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Windows could not attach the capture engine to ClipForge's lifetime job.");
            }

            return new CaptureProcessJob(jobHandle);
        }
        catch
        {
            jobHandle.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _jobHandle, null)?.Dispose();
    }

    private enum JobObjectInformationClass
    {
        ExtendedLimitInformation = 9
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObjectW(
        IntPtr jobAttributes,
        string? jobName);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(
        SafeFileHandle jobHandle,
        JobObjectInformationClass informationClass,
        ref JobObjectExtendedLimitInformation information,
        uint informationLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(
        SafeFileHandle jobHandle,
        IntPtr processHandle);
}
