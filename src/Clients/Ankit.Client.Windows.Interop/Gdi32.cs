using System.Runtime.InteropServices;

namespace Ankit.Client.Windows.Interop;

/// <summary>
/// gdi32.dll: copying screen pixels.
/// </summary>
public static partial class Gdi32
{
    private const string Library = "gdi32.dll";

    public const uint SRCCOPY = 0x00CC0020;
    public const uint CAPTUREBLT = 0x40000000;
    public const uint DIB_RGB_COLORS = 0;

    [LibraryImport(Library)]
    public static partial nint CreateCompatibleDC(nint hdc);

    [LibraryImport(Library)]
    public static partial nint CreateCompatibleBitmap(nint hdc, int cx, int cy);

    [LibraryImport(Library)]
    public static partial nint SelectObject(nint hdc, nint h);

    [LibraryImport(Library)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool BitBlt(nint hdc, int x, int y, int cx, int cy, nint hdcSrc, int x1, int y1, uint rop);

    [LibraryImport(Library)]
    public static partial int GetDIBits(nint hdc, nint hbm, uint start, uint cLines, [Out] byte[] lpvBits,
        ref BitmapInfoHeader lpbmi, uint usage);

    [LibraryImport(Library)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeleteObject(nint ho);

    [LibraryImport(Library)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeleteDC(nint hdc);
}
