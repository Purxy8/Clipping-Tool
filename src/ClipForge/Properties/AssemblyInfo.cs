using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

[assembly: InternalsVisibleTo("ClipForge.Tests")]
[assembly: InternalsVisibleTo("ClipForge.CaptureSmoke")]
[assembly: DefaultDllImportSearchPaths(
    DllImportSearchPath.AssemblyDirectory |
    DllImportSearchPath.System32 |
    DllImportSearchPath.UserDirectories)]
