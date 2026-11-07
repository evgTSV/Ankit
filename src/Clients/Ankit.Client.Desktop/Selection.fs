/// The selected term and its context in the app in front, where the platform can read it: Windows so far
module Ankit.Client.Desktop.Selection

open System
open Ankit.Client.Core

#if WINDOWS
open Ankit.Client.Ocr
open Ankit.Client.Windows

let private ocr =
    lazy
        (let engine = new TesseractOcr(TesseractSettings.defaults)

         if engine.IsAvailable then
             Some(engine :> IOcrEngine)
         else
             (engine :> IDisposable).Dispose()
             None)

let isSupported = true

/// The term and its sentence, the URL or the window title as the source; None when nothing is selected
let capture () : Popup.Captured option =
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
let listen (onPressed: unit -> unit) =
    Hotkey.listen (Hotkey.Modifiers.Control ||| Hotkey.Modifiers.Alt) ConsoleKey.A onPressed

/// Where the popup opens, in physical pixels
let cursor () =
    let mutable point = Ankit.Client.Windows.Interop.Point()
    Ankit.Client.Windows.Interop.User32.GetCursorPos &point |> ignore
    Some(point.X, point.Y)

let shutdown () =
    if ocr.IsValueCreated then
        ocr.Value |> Option.iter _.Dispose()
#else
let isSupported = false

let capture () : Popup.Captured option = None

let listen (_: unit -> unit) : Result<IDisposable, string> =
    Error "Reading the selection isn't supported on this platform yet"

let cursor () : (int * int) option = None

let shutdown () = ()
#endif
