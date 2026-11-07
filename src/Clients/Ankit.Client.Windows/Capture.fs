/// Reads the selection and the text around it from the foreground app
module Ankit.Client.Windows.Capture

open System
open System.Diagnostics
open System.Runtime.InteropServices
open System.Text
open System.Threading
open Ankit.Client.Core.Context
open Ankit.Client.Windows.Interop
open type Ankit.Client.Windows.Interop.User32
open type Ankit.Client.Windows.Interop.Kernel32
open Interop.UIAutomationClient

[<RequireQualifiedAccess>]
type CaptureMethod =
    | UiAutomation
    | Clipboard
    /// The text around from UI Automation, the selection from the clipboard: UI Automation reported a selection that
    /// cuts words, e.g. in Edge's PDF viewer
    | UiAutomationAndClipboard

type Source = {
    Process: string
    WindowTitle: string
    /// From the clipboard's HTML format, which browsers fill
    Url: string option
}

type Captured = {
    Capture: Capture
    Method: CaptureMethod
    Source: Source
    /// What the method saw, for diagnostics, e.g. the UI framework of the element
    Details: string
}

let source (url: string option) =
    let hwnd = GetForegroundWindow()

    let name =
        try
            use p = Process.GetProcessById(WindowProcessId hwnd)
            p.ProcessName
        with _ ->
            ""

    {
        Process = name
        WindowTitle = WindowTitle hwnd
        Url = url
    }

/// Runs f on its own MTA thread: UI Automation calls into other processes and must stay off a UI thread, and a hung
/// app must not hang the caller
let private onMtaThread (timeout: TimeSpan) (f: unit -> Result<'T, string>) =
    let mutable result = Error "Timed out"

    let thread =
        Thread(
            (fun () ->
                result <-
                    try
                        f ()
                    with ex ->
                        Error ex.Message),
            IsBackground = true
        )

    thread.SetApartmentState ApartmentState.MTA
    thread.Start()

    if thread.Join timeout then result else Error "Timed out"

module UiAutomation =
    let private automationClsid = Guid "e22ad333-b25f-460c-83d0-0581107395c9"

    let private textPatternId = 10014

    /// Paragraphs shorter than this may be lines of a hard-wrapped text, e.g. a PDF: lines around them join the context
    let private shortParagraph = 200

    let private create () =
        let automation =
            Activator.CreateInstance(Type.GetTypeFromCLSID automationClsid) :?> IUIAutomation

        match automation with
        | :? IUIAutomation2 as automation2 ->
            automation2.ConnectionTimeout <- 1000u
            automation2.TransactionTimeout <- 1000u
        | _ -> ()

        automation

    /// The focused element or its nearest ancestor with a text pattern: browsers focus a link or the document
    let private textPattern (automation: IUIAutomation) (element: IUIAutomationElement) =
        let walker = automation.ControlViewWalker

        let rec find (element: IUIAutomationElement) depth =
            if isNull element || depth > 8 then
                None
            else
                match element.GetCurrentPattern textPatternId with
                | :? IUIAutomationTextPattern as pattern -> Some(element, pattern)
                | _ -> find (walker.GetParentElement element) (depth + 1)

        find element 0

    /// The selection within the text, near the offset UI Automation reported: providers differ in how they count
    /// line breaks and embedded objects
    let private locate (text: string) (selected: string) (offset: int) =
        if
            offset >= 0
            && offset + selected.Length <= text.Length
            && text.Substring(offset, selected.Length) = selected
        then
            Some offset
        else
            let before =
                text.LastIndexOf(selected, min (text.Length - 1) (max 0 (offset + selected.Length - 1)))

            let after = text.IndexOf(selected, max 0 (min text.Length offset))

            match before, after with
            | -1, -1 -> None
            | b, -1 -> Some b
            | -1, a -> Some a
            | b, a -> Some(if offset - b <= a - offset then b else a)

    let private read () =
        let automation = create ()
        let focused = automation.GetFocusedElement()

        if isNull focused then
            Error "No focused element"
        elif focused.CurrentIsPassword <> 0 then
            Error "The focused element is a password field"
        else
            match textPattern automation focused with
            | None -> Error $"No text pattern on %s{focused.CurrentFrameworkId}/%s{focused.CurrentClassName}"
            | Some(element, pattern) ->
                let ranges = pattern.GetSelection()

                if isNull ranges || ranges.Length = 0 then
                    Error "No selection"
                else
                    let selection = ranges.GetElement 0
                    let selected = selection.GetText -1

                    if String.IsNullOrWhiteSpace selected then
                        Error "The selection is empty"
                    else
                        let start = TextPatternRangeEndpoint.TextPatternRangeEndpoint_Start
                        let finish = TextPatternRangeEndpoint.TextPatternRangeEndpoint_End
                        let around = selection.Clone()
                        around.ExpandToEnclosingUnit TextUnit.TextUnit_Paragraph

                        // Expanding may cover only the paragraph where the selection starts
                        if around.CompareEndpoints(finish, selection, finish) < 0 then
                            around.MoveEndpointByRange(finish, selection, finish)

                        if (around.GetText -1).Length < shortParagraph then
                            around.MoveEndpointByUnit(start, TextUnit.TextUnit_Line, -2) |> ignore
                            around.MoveEndpointByUnit(finish, TextUnit.TextUnit_Line, 2) |> ignore

                        let measure () =
                            let text = around.GetText -1
                            let before = around.Clone()
                            before.MoveEndpointByRange(finish, selection, start)
                            let offset = (before.GetText -1).Length

                            locate text selected offset
                            |> Option.map (fun found ->
                                {
                                    Text = text
                                    SelectionStart = found
                                    SelectionLength = selected.Length
                                },
                                offset)

                        // A selection at an edge of the text can't be checked for cut words, see isWordAligned
                        let measured =
                            match measure () with
                            | Some(capture, _) when not (isWordAligned capture) ->
                                around.MoveEndpointByUnit(start, TextUnit.TextUnit_Paragraph, -1) |> ignore
                                around.MoveEndpointByUnit(finish, TextUnit.TextUnit_Paragraph, 1) |> ignore
                                measure ()
                            | result -> result

                        match measured with
                        | None -> Error "The selection isn't in the text around it"
                        | Some(capture, offset) ->
                            Ok {
                                Capture = capture
                                Method = CaptureMethod.UiAutomation
                                Source = source None
                                Details =
                                    $"framework %s{element.CurrentFrameworkId}, class %s{element.CurrentClassName}, reported offset %d{offset}, found at %d{capture.SelectionStart}, word-aligned %b{isWordAligned capture}"
                            }

    let capture () = onMtaThread (TimeSpan.FromSeconds 3.0) read

module Clipboard =
    /// Handles to GDI objects, not global memory, can't be copied byte by byte
    let private gdiFormats = set [ 2u; 3u; 9u; 14u; 0x80u; 0x82u; 0x83u; 0x8Eu ]

    let private htmlFormat = lazy (RegisterClipboardFormatW "HTML Format")

    let private openClipboard () =
        let rec attempt n =
            if OpenClipboard 0n then
                true
            elif n = 0 then
                false
            else
                Thread.Sleep 20
                attempt (n - 1)

        attempt 10

    let private bytesOf (handle: nativeint) =
        let size = int (GlobalSize handle)
        let pointer = GlobalLock handle

        if pointer = 0n then
            None
        else
            try
                let bytes = Array.zeroCreate<byte> size
                Marshal.Copy(pointer, bytes, 0, size)
                Some bytes
            finally
                GlobalUnlock handle |> ignore

    let private save () =
        if not (openClipboard ()) then
            []
        else
            try
                let rec formats format acc =
                    match EnumClipboardFormats format with
                    | 0u -> List.rev acc
                    | next -> formats next (next :: acc)

                formats 0u []
                |> List.filter (gdiFormats.Contains >> not)
                |> List.choose (fun format ->
                    match GetClipboardData format with
                    | 0n -> None
                    | handle -> bytesOf handle |> Option.map (fun bytes -> format, bytes))
            finally
                CloseClipboard() |> ignore

    let private restore (saved: (uint32 * byte[]) list) =
        if openClipboard () then
            try
                EmptyClipboard() |> ignore

                for format, bytes in saved do
                    let handle = GlobalAlloc(GMEM_MOVEABLE, unativeint bytes.Length)
                    let pointer = GlobalLock handle
                    Marshal.Copy(bytes, 0, pointer, bytes.Length)
                    GlobalUnlock handle |> ignore

                    // The clipboard owns the memory once it's set
                    if SetClipboardData(format, handle) = 0n then
                        GlobalFree handle |> ignore
            finally
                CloseClipboard() |> ignore

    let private read (format: uint32) =
        match GetClipboardData format with
        | 0n -> None
        | handle -> bytesOf handle

    /// The memory may be larger than the text: it ends at the first NUL
    let private text () =
        read CF_UNICODETEXT
        |> Option.map (fun bytes ->
            let text = Encoding.Unicode.GetString(bytes, 0, bytes.Length &&& ~~~1)

            match text.IndexOf '\000' with
            | -1 -> text
            | nul -> text.Substring(0, nul))

    let private isWebUrl (url: string) =
        match Uri.TryCreate(url, UriKind.Absolute) with
        | true, uri -> uri.Scheme = Uri.UriSchemeHttp || uri.Scheme = Uri.UriSchemeHttps
        | _ -> false

    /// The CF_HTML header has a SourceURL line in browsers; other apps may put about:blank there
    let private sourceUrl () =
        read htmlFormat.Value
        |> Option.bind (fun bytes ->
            let header = Encoding.UTF8.GetString(bytes, 0, min bytes.Length 1024)

            header.Split([| '\r'; '\n' |], StringSplitOptions.RemoveEmptyEntries)
            |> Array.tryFind _.StartsWith("SourceURL:")
            |> Option.map _.Substring("SourceURL:".Length).Trim())
        |> Option.filter isWebUrl

    /// The hotkey's modifiers would turn Ctrl+C into another shortcut
    let private waitForModifiers () =
        let modifiers = [ VK_CONTROL; VK_MENU; VK_SHIFT; VK_LWIN; VK_RWIN ]
        let deadline = DateTime.UtcNow.AddSeconds 1.0

        while DateTime.UtcNow < deadline && List.exists IsKeyDown modifiers do
            Thread.Sleep 10

    let private key (vk: int) (up: bool) =
        let mutable input = Input()
        input.Type <- INPUT_KEYBOARD
        let mutable keyboard = KeyboardInput()
        keyboard.Vk <- uint16 vk
        keyboard.Flags <- if up then KEYEVENTF_KEYUP else 0u
        let mutable union = InputUnion()
        union.Keyboard <- keyboard
        input.Union <- union
        input

    let private sendCopy () =
        let inputs = [|
            key VK_CONTROL false
            key (int 'C') false
            key (int 'C') true
            key VK_CONTROL true
        |]

        SendInput(uint32 inputs.Length, inputs, Marshal.SizeOf<Input>()) = uint32 inputs.Length

    let private copySelection () =
        waitForModifiers ()
        let sequence = GetClipboardSequenceNumber()
        let saved = save ()

        if not (sendCopy ()) then
            Error "SendInput failed"
        else
            let deadline = DateTime.UtcNow.AddMilliseconds 500.0

            // Some apps (Acrobat) empty the clipboard first and put the text a moment later
            while DateTime.UtcNow < deadline
                  && (GetClipboardSequenceNumber() = sequence
                      || not (IsClipboardFormatAvailable CF_UNICODETEXT)) do
                Thread.Sleep 10

            if GetClipboardSequenceNumber() = sequence then
                Error "The app copied nothing"
            else
                let copied =
                    if openClipboard () then
                        try
                            Ok(text (), sourceUrl ())
                        finally
                            CloseClipboard() |> ignore
                    else
                        Error "The clipboard is busy"

                restore saved

                match copied with
                | Ok(Some text, url) when not (String.IsNullOrWhiteSpace text) ->
                    Ok {
                        Capture = {
                            Text = text
                            SelectionStart = 0
                            SelectionLength = text.Length
                        }
                        Method = CaptureMethod.Clipboard
                        Source = source url
                        Details = $"%d{saved.Length} clipboard format(s) restored"
                    }
                | Ok _ -> Error "The copy has no text"
                | Error e -> Error e

    /// Sends Ctrl+C to the foreground app and puts the previous clipboard back. The copy still lands in the clipboard
    /// history (Win+V), which is why this is the fallback
    let capture () = onMtaThread (TimeSpan.FromSeconds 3.0) copySelection

/// The text around from UI Automation with the selection the clipboard got, at its occurrence nearest to the one UI
/// Automation reported; None when the text doesn't contain it
let combine (uia: Captured) (clipboard: Captured) =
    let c = clipboard.Capture
    let selected = c.Text.Substring(c.SelectionStart, c.SelectionLength)

    relocate selected uia.Capture
    |> Option.map (fun capture -> {
        Capture = capture
        Method = CaptureMethod.UiAutomationAndClipboard
        Source = { uia.Source with Url = clipboard.Source.Url }
        Details = $"%s{uia.Details}; selection from the clipboard"
    })

/// UI Automation, checked with the clipboard when its selection cuts words; the clipboard alone when UI Automation
/// can't read the app. The errors of both when neither works
let choose (uia: Result<Captured, string>) (clipboard: Lazy<Result<Captured, string>>) =
    match uia with
    | Ok uia when isWordAligned uia.Capture -> Ok uia
    | uia ->
        match uia, clipboard.Force() with
        | Ok uia, Ok clipboard -> Ok(combine uia clipboard |> Option.defaultValue clipboard)
        | Ok uia, Error _ -> Ok uia
        | Error _, Ok clipboard -> Ok clipboard
        | Error uia, Error clipboard -> Error $"UI Automation: %s{uia}; clipboard: %s{clipboard}"

let selection () =
    choose (UiAutomation.capture ()) (lazy (Clipboard.capture ()))
