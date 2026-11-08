module internal Ankit.Client.Windows.Win32

// Explicit layout for the INPUT union
#nowarn "9"

open System
open System.Runtime.InteropServices

[<Struct; StructLayout(LayoutKind.Sequential)>]
type Point =
    val X: int
    val Y: int

[<Struct; StructLayout(LayoutKind.Sequential)>]
type Msg =
    val Hwnd: nativeint
    val Message: uint32
    val WParam: nativeint
    val LParam: nativeint
    val Time: uint32
    val Pt: Point
    val Private: uint32

[<Struct; StructLayout(LayoutKind.Sequential)>]
type KeyboardInput =
    val mutable Vk: uint16
    val mutable Scan: uint16
    val mutable Flags: uint32
    val mutable Time: uint32
    val mutable ExtraInfo: nativeint

[<Struct; StructLayout(LayoutKind.Sequential)>]
type MouseInput =
    val Dx: int
    val Dy: int
    val MouseData: uint32
    val Flags: uint32
    val Time: uint32
    val ExtraInfo: nativeint

/// Sized like the largest member, so the INPUT size matches on x86 and x64
[<Struct; StructLayout(LayoutKind.Explicit)>]
type InputUnion =
    [<FieldOffset(0)>]
    val mutable Keyboard: KeyboardInput

    [<FieldOffset(0)>]
    val mutable Mouse: MouseInput

[<Struct; StructLayout(LayoutKind.Sequential)>]
type Input =
    val mutable Type: uint32
    val mutable Union: InputUnion

[<Literal>]
let WM_HOTKEY = 0x0312u

[<Literal>]
let MOD_ALT = 0x0001u

[<Literal>]
let MOD_CONTROL = 0x0002u

[<Literal>]
let MOD_SHIFT = 0x0004u

[<Literal>]
let MOD_WIN = 0x0008u

[<Literal>]
let MOD_NOREPEAT = 0x4000u

[<Literal>]
let INPUT_KEYBOARD = 1u

[<Literal>]
let KEYEVENTF_KEYUP = 0x0002u

[<Literal>]
let VK_SHIFT = 0x10

[<Literal>]
let VK_CONTROL = 0x11

[<Literal>]
let VK_MENU = 0x12

[<Literal>]
let VK_LWIN = 0x5B

[<Literal>]
let VK_RWIN = 0x5C

[<Literal>]
let CF_UNICODETEXT = 13u

[<Literal>]
let GMEM_MOVEABLE = 0x0002u

[<DllImport("user32.dll", SetLastError = true)>]
extern bool RegisterHotKey(nativeint hWnd, int id, uint32 fsModifiers, uint32 vk)

[<DllImport("user32.dll", SetLastError = true)>]
extern bool UnregisterHotKey(nativeint hWnd, int id)

[<DllImport("user32.dll")>]
extern int GetMessageW(Msg& lpMsg, nativeint hWnd, uint32 wMsgFilterMin, uint32 wMsgFilterMax)

[<DllImport("user32.dll")>]
extern bool PostThreadMessageW(uint32 idThread, uint32 msg, nativeint wParam, nativeint lParam)

[<DllImport("kernel32.dll")>]
extern uint32 GetCurrentThreadId()

[<DllImport("user32.dll")>]
extern nativeint GetForegroundWindow()

[<DllImport("user32.dll", CharSet = CharSet.Unicode)>]
extern int GetWindowTextW(nativeint hWnd, char[] lpString, int nMaxCount)

[<DllImport("user32.dll")>]
extern int GetWindowTextLengthW(nativeint hWnd)

[<DllImport("user32.dll")>]
extern uint32 GetWindowThreadProcessId(nativeint hWnd, uint32& lpdwProcessId)

[<DllImport("user32.dll", SetLastError = true)>]
extern uint32 SendInput(uint32 nInputs, Input[] pInputs, int cbSize)

[<DllImport("user32.dll")>]
extern int16 GetAsyncKeyState(int vKey)

[<DllImport("user32.dll", SetLastError = true)>]
extern bool OpenClipboard(nativeint hWndNewOwner)

[<DllImport("user32.dll")>]
extern bool CloseClipboard()

[<DllImport("user32.dll")>]
extern bool EmptyClipboard()

[<DllImport("user32.dll")>]
extern nativeint GetClipboardData(uint32 uFormat)

[<DllImport("user32.dll")>]
extern nativeint SetClipboardData(uint32 uFormat, nativeint hMem)

[<DllImport("user32.dll")>]
extern uint32 EnumClipboardFormats(uint32 format)

[<DllImport("user32.dll", CharSet = CharSet.Unicode)>]
extern uint32 RegisterClipboardFormatW(string lpszFormat)

[<DllImport("user32.dll")>]
extern uint32 GetClipboardSequenceNumber()

[<DllImport("user32.dll")>]
extern bool IsClipboardFormatAvailable(uint32 format)

[<DllImport("kernel32.dll")>]
extern nativeint GlobalLock(nativeint hMem)

[<DllImport("kernel32.dll")>]
extern bool GlobalUnlock(nativeint hMem)

[<DllImport("kernel32.dll")>]
extern unativeint GlobalSize(nativeint hMem)

[<DllImport("kernel32.dll")>]
extern nativeint GlobalAlloc(uint32 uFlags, unativeint dwBytes)

[<DllImport("kernel32.dll")>]
extern nativeint GlobalFree(nativeint hMem)

let isKeyDown (vk: int) = GetAsyncKeyState vk &&& 0x8000s <> 0s

let windowTitle (hwnd: nativeint) =
    let length = GetWindowTextLengthW hwnd

    if length <= 0 then
        ""
    else
        let buffer = Array.zeroCreate<char>(length + 1)
        let copied = GetWindowTextW(hwnd, buffer, buffer.Length)
        String(buffer, 0, copied)

let windowProcessId (hwnd: nativeint) =
    let mutable pid = 0u
    GetWindowThreadProcessId(hwnd, &pid) |> ignore
    int pid
