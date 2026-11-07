using System.Runtime.InteropServices;

namespace Ankit.Client.Windows.Interop;

/// <summary>
/// user32.dll: hotkeys, windows, input and the clipboard.
/// </summary>
public static unsafe partial class User32
{
    private const string Library = "user32.dll";

    public const uint WM_QUIT = 0x0012;
    public const uint WM_HOTKEY = 0x0312;
    public const uint MOD_NOREPEAT = 0x4000;
    public const uint INPUT_KEYBOARD = 1;
    public const uint KEYEVENTF_KEYUP = 0x0002;
    public const int VK_SHIFT = 0x10;
    public const int VK_CONTROL = 0x11;
    public const int VK_MENU = 0x12;
    public const int VK_LWIN = 0x5B;
    public const int VK_RWIN = 0x5C;
    public const uint CF_UNICODETEXT = 13;

    /// <summary>
    /// DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2: coordinates and screen pixels without DPI virtualization.
    /// </summary>
    public static readonly nint DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = -4;

    [LibraryImport(Library, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);

    [LibraryImport(Library, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnregisterHotKey(nint hWnd, int id);

    [LibraryImport(Library)]
    public static partial int GetMessageW(out Msg lpMsg, nint hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [LibraryImport(Library)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PostThreadMessageW(uint idThread, uint msg, nint wParam, nint lParam);

    [LibraryImport(Library)]
    public static partial nint GetForegroundWindow();

    [LibraryImport(Library)]
    private static partial int GetWindowTextW(nint hWnd, char* lpString, int nMaxCount);

    [LibraryImport(Library)]
    private static partial int GetWindowTextLengthW(nint hWnd);

    [LibraryImport(Library)]
    public static partial uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    [LibraryImport(Library, SetLastError = true)]
    public static partial uint SendInput(uint nInputs, Input[] pInputs, int cbSize);

    [LibraryImport(Library)]
    private static partial short GetAsyncKeyState(int vKey);

    [LibraryImport(Library, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool OpenClipboard(nint hWndNewOwner);

    [LibraryImport(Library)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseClipboard();

    [LibraryImport(Library)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EmptyClipboard();

    [LibraryImport(Library)]
    public static partial nint GetClipboardData(uint uFormat);

    [LibraryImport(Library)]
    public static partial nint SetClipboardData(uint uFormat, nint hMem);

    [LibraryImport(Library)]
    public static partial uint EnumClipboardFormats(uint format);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint RegisterClipboardFormatW(string lpszFormat);

    [LibraryImport(Library)]
    public static partial uint GetClipboardSequenceNumber();

    [LibraryImport(Library)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsClipboardFormatAvailable(uint format);

    [LibraryImport(Library)]
    public static partial nint SetThreadDpiAwarenessContext(nint dpiContext);

    [LibraryImport(Library)]
    public static partial uint GetDpiForWindow(nint hwnd);

    [LibraryImport(Library)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetCursorPos(out Point lpPoint);

    [LibraryImport(Library)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetWindowRect(nint hWnd, out Rect lpRect);

    [LibraryImport(Library)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetGUIThreadInfo(uint idThread, ref GuiThreadInfo pgui);

    [LibraryImport(Library)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ClientToScreen(nint hWnd, ref Point lpPoint);

    [LibraryImport(Library)]
    public static partial nint GetDC(nint hWnd);

    [LibraryImport(Library)]
    public static partial int ReleaseDC(nint hWnd, nint hDC);

    public static bool IsKeyDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    public static string WindowTitle(nint hwnd)
    {
        var length = GetWindowTextLengthW(hwnd);

        if (length <= 0)
        {
            return "";
        }

        var buffer = new char[length + 1];

        fixed (char* pointer = buffer)
        {
            var copied = GetWindowTextW(hwnd, pointer, buffer.Length);
            return new string(buffer, 0, copied);
        }
    }

    public static int WindowProcessId(nint hwnd)
    {
        GetWindowThreadProcessId(hwnd, out var pid);
        return (int)pid;
    }
}
