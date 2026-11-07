/// Prints what each capture method gets from the foreground app when the hotkey is pressed, to see which apps need
/// which fallback
module Ankit.Client.Windows.Probe.Program

open System
open System.Diagnostics
open System.Threading
open Ankit.Client.Core.Context
open Ankit.Client.Ocr
open Ankit.Client.Windows
open Ankit.Client.Windows.Capture

let private gate = obj ()

let private shorten (text: string) =
    let oneLine = text.Replace("\r", "\\r").Replace("\n", "\\n")

    if oneLine.Length > 300 then
        oneLine.Substring(0, 300) + "…"
    else
        oneLine

let private report (name: string) (result: Result<Captured, string>) (elapsed: TimeSpan) =
    printfn "  %s (%d ms)" name (int elapsed.TotalMilliseconds)

    match result with
    | Error e -> printfn "    failed: %s" e
    | Ok captured ->
        let c = captured.Capture
        printfn "    %s" captured.Details
        printfn "    selection: \"%s\"" (shorten (c.Text.Substring(c.SelectionStart, c.SelectionLength)))
        printfn "    text (%d chars): \"%s\"" c.Text.Length (shorten c.Text)
        captured.Source.Url |> Option.iter (printfn "    url: %s")

        match extract c with
        | None -> printfn "    extracted: nothing"
        | Some e ->
            printfn "    term: \"%s\"" e.Term
            printfn "    context: \"%s\"" e.Context

let private timed (f: unit -> 'T) =
    let watch = Stopwatch.StartNew()
    let result = f ()
    result, watch.Elapsed

let private ocr = new TesseractOcr(TesseractSettings.defaults) :> IOcrEngine

let private probe () =
    lock gate (fun () ->
        let source = Capture.source None
        printfn ""
        printfn "[%s] %s — \"%s\"" (DateTime.Now.ToString "HH:mm:ss") source.Process source.WindowTitle
        let uia, uiaTime = timed UiAutomation.capture
        report "UI Automation" uia uiaTime
        let clipboard, clipboardTime = timed Clipboard.capture
        report "Clipboard" clipboard clipboardTime
        // What the client would use, from the two results above instead of a second copy
        let chosen, chosenTime = timed (fun () -> choose (Some ocr) uia (lazy clipboard))

        let methodName =
            chosen |> Result.map (fun c -> $"%A{c.Method}") |> Result.defaultValue "none"

        report $"Chosen: %s{methodName}" chosen chosenTime)

[<EntryPoint>]
let main _ =
    match Hotkey.listen (Hotkey.Modifiers.Control ||| Hotkey.Modifiers.Alt) ConsoleKey.A probe with
    | Error e ->
        eprintfn "%s" e
        1
    | Ok hotkey ->
        use _ = hotkey
        printfn "Select text in any app and press Ctrl+Alt+A. Ctrl+C here quits."
        use quit = new ManualResetEventSlim()

        Console.CancelKeyPress.Add(fun args ->
            args.Cancel <- true
            quit.Set())

        quit.Wait()
        lock gate ocr.Dispose
        0
