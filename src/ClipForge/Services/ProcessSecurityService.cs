using System.Runtime.InteropServices;

namespace ClipForge.Services;

/// <summary>
/// Applies process-wide Windows loader policy before the UI and capture engine
/// start. This removes the current working directory and PATH from implicit DLL
/// resolution while retaining the application and System32 directories.
/// </summary>
internal static class ProcessSecurityService
{
    private const uint LoadLibrarySearchDefaultDirs = 0x00001000;

    public static void Apply()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        if (!SetDefaultDllDirectories(LoadLibrarySearchDefaultDirs))
        {
            throw new System.ComponentModel.Win32Exception(
                Marshal.GetLastWin32Error(),
                "Windows could not enable the secure DLL search policy.");
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetDefaultDllDirectories(uint directoryFlags);
}
