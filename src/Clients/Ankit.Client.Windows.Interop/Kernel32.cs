using System.Runtime.InteropServices;

namespace Ankit.Client.Windows.Interop;

/// <summary>
/// kernel32.dll: threads and the global memory the clipboard uses.
/// </summary>
public static partial class Kernel32
{
    private const string Library = "kernel32.dll";

    public const uint GMEM_MOVEABLE = 0x0002;

    [LibraryImport(Library)]
    public static partial uint GetCurrentThreadId();

    [LibraryImport(Library)]
    public static partial nint GlobalLock(nint hMem);

    [LibraryImport(Library)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GlobalUnlock(nint hMem);

    [LibraryImport(Library)]
    public static partial nuint GlobalSize(nint hMem);

    [LibraryImport(Library)]
    public static partial nint GlobalAlloc(uint uFlags, nuint dwBytes);

    [LibraryImport(Library)]
    public static partial nint GlobalFree(nint hMem);
}
