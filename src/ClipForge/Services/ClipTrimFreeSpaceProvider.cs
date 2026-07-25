using System.Runtime.InteropServices;
using System.Security;

namespace ClipForge.Services;

internal interface IClipTrimFreeSpaceProvider
{
    bool TryGetAvailableFreeBytes(string directoryPath, out ulong availableFreeBytes);
}

internal sealed class WindowsClipTrimFreeSpaceProvider : IClipTrimFreeSpaceProvider
{
    internal delegate bool NativeFreeSpaceQuery(
        string directoryPath,
        out ulong freeBytesAvailableToCaller,
        out ulong totalNumberOfBytes,
        out ulong totalNumberOfFreeBytes);

    internal static WindowsClipTrimFreeSpaceProvider Instance { get; } =
        new(GetDiskFreeSpaceExW);

    private readonly NativeFreeSpaceQuery _query;

    internal WindowsClipTrimFreeSpaceProvider(NativeFreeSpaceQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        _query = query;
    }

    public bool TryGetAvailableFreeBytes(
        string directoryPath,
        out ulong availableFreeBytes)
    {
        availableFreeBytes = 0;
        try
        {
            var queryPath = ClipLibraryService.ToExtendedLengthPath(directoryPath);
            if (!Path.EndsInDirectorySeparator(queryPath))
            {
                queryPath += Path.DirectorySeparatorChar;
            }

            return _query(
                queryPath,
                out availableFreeBytes,
                out _,
                out _);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or
                NotSupportedException or SecurityException)
        {
            availableFreeBytes = 0;
            return false;
        }
    }

    [DllImport(
        "kernel32.dll",
        CharSet = CharSet.Unicode,
        ExactSpelling = true,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceExW(
        string lpDirectoryName,
        out ulong lpFreeBytesAvailableToCaller,
        out ulong lpTotalNumberOfBytes,
        out ulong lpTotalNumberOfFreeBytes);
}
