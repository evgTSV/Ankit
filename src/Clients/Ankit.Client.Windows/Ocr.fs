/// The text around a clipboard selection read from the screen, for apps that draw text without exposing it to UI
/// Automation (WPS, Telegram's messages, Acrobat, JetBrains IDEs). What's Windows here is the screenshot and the anchor;
/// recognition is an IOcrEngine, finding the selection ScreenText.locate
module Ankit.Client.Windows.Ocr

open System.IO
open System.Runtime.InteropServices
open System.Text
open Ankit.Client.Core.ScreenText
open Ankit.Client.Ocr
open Ankit.Client.Windows.Interop
open type Ankit.Client.Windows.Interop.User32
open type Ankit.Client.Windows.Interop.Gdi32

/// Logical pixels above and below the anchor: a few lines first, more when the selection isn't there or its paragraph
/// runs past the strip
let private heights = [ 100; 300 ]

/// Where the selection probably is: the caret when the app reports one, otherwise the mouse cursor
let private anchor (hwnd: nativeint) =
    let mutable info = GuiThreadInfo()
    info.Size <- uint32 (Marshal.SizeOf<GuiThreadInfo>())
    let mutable pid = 0u
    let thread = GetWindowThreadProcessId(hwnd, &pid)

    if GetGUIThreadInfo(thread, &info) && info.Caret <> 0n then
        let mutable point = Point()
        point.X <- info.CaretRect.Left
        point.Y <- (info.CaretRect.Top + info.CaretRect.Bottom) / 2
        ClientToScreen(info.Caret, &point) |> ignore
        point.X, point.Y
    else
        let mutable point = Point()
        GetCursorPos &point |> ignore
        point.X, point.Y

/// A 24-bit bottom-up BMP of a screen area, which Leptonica reads without extra libraries
let private screenshot (area: Box) =
    let width = area.Right - area.Left
    let height = area.Bottom - area.Top
    let screen = GetDC 0n
    let memory = CreateCompatibleDC screen
    let bitmap = CreateCompatibleBitmap(screen, width, height)
    let previous = SelectObject(memory, bitmap)

    try
        BitBlt(memory, 0, 0, width, height, screen, area.Left, area.Top, SRCCOPY ||| CAPTUREBLT)
        |> ignore

        SelectObject(memory, previous) |> ignore
        let stride = (width * 3 + 3) &&& ~~~3
        let pixels = Array.zeroCreate<byte>(stride * height)
        let mutable header = BitmapInfoHeader()
        header.Size <- 40u
        header.Width <- width
        header.Height <- height
        header.Planes <- 1us
        header.BitCount <- 24us

        GetDIBits(memory, bitmap, 0u, uint32 height, pixels, &header, DIB_RGB_COLORS)
        |> ignore

        use stream = new MemoryStream()
        use writer = new BinaryWriter(stream)
        writer.Write(Encoding.ASCII.GetBytes "BM")
        writer.Write(14 + 40 + pixels.Length)
        writer.Write 0
        writer.Write(14 + 40)
        writer.Write 40
        writer.Write width
        writer.Write height
        writer.Write 1s
        writer.Write 24s
        writer.Write 0
        writer.Write pixels.Length
        writer.Write 3780
        writer.Write 3780
        writer.Write 0
        writer.Write 0
        writer.Write pixels
        writer.Flush()
        stream.ToArray()
    finally
        DeleteObject bitmap |> ignore
        DeleteDC memory |> ignore
        ReleaseDC(0n, screen) |> ignore

/// The selection in the text read around the caret or the mouse cursor, its text the clipboard's. Must run on a thread
/// that may change its DPI awareness: coordinates and pixels are physical
let around (engine: IOcrEngine) (selected: string) =
    SetThreadDpiAwarenessContext DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2
    |> ignore

    let hwnd = GetForegroundWindow()
    let mutable window = Rect()
    GetWindowRect(hwnd, &window) |> ignore
    let x, y = anchor hwnd

    let dpi =
        match GetDpiForWindow hwnd with
        | 0u -> 96
        | dpi -> int dpi

    let describe (area: Box) (screen: ScreenText) =
        let lines =
            screen.Blocks |> List.sumBy (fun b -> b.Paragraphs |> List.sumBy List.length)

        $"%d{lines} lines from %d{area.Right - area.Left}x%d{area.Bottom - area.Top} px"

    let read (half: int) =
        let half = half * dpi / 96

        let area = {
            Left = window.Left
            Top = max window.Top (y - half)
            Right = window.Right
            Bottom = min window.Bottom (y + half)
        }

        if area.Right <= area.Left || area.Bottom <= area.Top then
            Error "The window has no area to read"
        else
            let screen =
                engine.Recognize(screenshot area, selected) |> offset area.Left area.Top

            match locate selected (x, y) screen with
            | Some located -> Ok(located, $"%d{located.Matches} match(es), %s{describe area screen}")
            | None -> Error $"\"%s{selected}\" isn't in the %s{describe area screen}"

    // A larger strip only when the smaller one missed the selection or cut its paragraph
    let rec attempt (heights: int list) (best: Result<Located * string, string>) =
        match heights, best with
        | [], _ -> best
        | _, Ok(located, _) when not located.CutByEdge -> best
        | half :: rest, _ ->
            match read half, best with
            | Error _, Ok _ -> attempt rest best
            | result, _ -> attempt rest result

    attempt heights (Error "Nothing was read")
    |> Result.map (fun (located, details) -> located.Capture, details)
