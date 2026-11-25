/// The selected term and its context in the app in front, where the platform can read it: Windows so far
module Ankit.Client.Desktop.Selection

open System
open System.Diagnostics
open System.Runtime.InteropServices
open Ankit.Client.Core
open Ankit.Client.Ocr
open Ankit.Client.Windows

let isSupported = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)

/// Without trained data the context comes from UI Automation or the clipboard alone
let private ocr =
    lazy
        (let engine = new TesseractOcr(TesseractSettings.defaults)

         if engine.IsAvailable then
             Some(engine :> IOcrEngine)
         else
             Trace.TraceWarning $"No trained data in %s{TesseractSettings.defaults.TessdataPath}, OCR is off"
             (engine :> IDisposable).Dispose()
             None)

/// The term and its sentence, the URL or the window title as the source; None when nothing is selected
let capture () : Popup.Captured option =
    if not isSupported then
        None
    else
        match Capture.selection ocr.Value with
        | Ok captured ->
            Context.extract captured.Capture
            |> Option.map (fun extracted -> {
                Term = extracted.Term
                Context = extracted.Context
                Source =
                    captured.Source.Url
                    |> Option.orElse (
                        Option.ofObj captured.Source.WindowTitle
                        |> Option.filter (String.IsNullOrWhiteSpace >> not)
                    )
            })
        | Error _ -> None

/// Ctrl+Alt+A
let listen (onPressed: unit -> unit) : Result<IDisposable, string> =
    if not isSupported then
        Error "Reading the selection isn't supported on this platform yet"
    else
        Hotkey.listen (Hotkey.Modifiers.Control ||| Hotkey.Modifiers.Alt) ConsoleKey.A onPressed

/// Where the popup opens, in physical pixels
let cursor () : (int * int) option =
    if not isSupported then
        None
    else
        let mutable point = Ankit.Client.Windows.Interop.Point()
        Ankit.Client.Windows.Interop.User32.GetCursorPos &point |> ignore
        Some(point.X, point.Y)

let shutdown () =
    if ocr.IsValueCreated then
        ocr.Value |> Option.iter _.Dispose()
