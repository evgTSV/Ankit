/// The app: a tray icon, the settings window, and a popup for each capture
module Ankit.Client.Desktop.Shell

open System
open System.IO
open System.Threading.Tasks
open Avalonia
open Avalonia.Controls
open Avalonia.Controls.ApplicationLifetimes
open Avalonia.FuncUI.Hosts
open Avalonia.Input
open Avalonia.Media
open Avalonia.Media.Imaging
open Avalonia.Threading
open Ankit.Client.Core
open Ankit.Client.Core.Api
open Ankit.Client.Core.Settings
open Ankit.Client.Http

/// A blue circle with a white dot, drawn so that the app ships no image files
let private icon () =
    let size = 64.0
    let bitmap = new RenderTargetBitmap(PixelSize(64, 64))

    using (bitmap.CreateDrawingContext()) (fun context ->
        context.DrawEllipse(
            SolidColorBrush(Color.FromRgb(0x2Fuy, 0x6Fuy, 0xEBuy)),
            null,
            Point(size / 2.0, size / 2.0),
            30.0,
            30.0
        )

        context.DrawEllipse(Brushes.White, null, Point(size / 2.0, size / 2.0), 11.0, 11.0))

    use stream = new MemoryStream()
    bitmap.Save(stream, PngBitmapEncoderOptions())
    stream.Position <- 0L
    WindowIcon(stream)

type private State = {
    mutable Settings: Store.AppSettings
    mutable Api: AnkitHttpApi option
    mutable SettingsWindow: Window option
    mutable Popup: Window option
}

let private state = {
    Settings = Store.empty
    Api = None
    SettingsWindow = None
    Popup = None
}

let private connect (server: ServerSettings) =
    state.Api |> Option.iter (fun api -> (api :> IDisposable).Dispose())

    state.Api <-
        if ServerSettings.isComplete server then
            Some(AnkitHttpApi.Create server)
        else
            None

let private save (settings: Store.AppSettings) =
    state.Settings <- settings
    Store.save settings

let openSettings () =
    match state.SettingsWindow with
    | Some window -> window.Activate()
    | None ->
        let window =
            HostWindow(Title = "Ankit settings", Width = 420.0, SizeToContent = SizeToContent.Height, CanResize = false)

        let persist server =
            save { state.Settings with Server = server }
            connect server
            Dispatcher.UIThread.Post(fun () -> window.Close())

        window.Content <- Views.settings state.Settings.Server persist
        window.Closed.Add(fun _ -> state.SettingsWindow <- None)
        state.SettingsWindow <- Some window
        window.Show()

/// Keeps the window inside the screen it opens on
let private place (window: Window) (x: int, y: int) =
    window.WindowStartupLocation <- WindowStartupLocation.Manual
    window.Position <- PixelPoint(x + 12, y + 12)

    window.Opened.Add(fun _ ->
        match window.Screens.ScreenFromPoint(PixelPoint(x, y)) |> Option.ofObj with
        | Some screen ->
            let area = screen.WorkingArea
            let scale = screen.Scaling
            let width = int (window.Bounds.Width * scale)
            let height = int (window.Bounds.Height * scale)
            let left = min (x + 12) (area.Right - width) |> max area.X
            let top = min (y + 12) (area.Bottom - height) |> max area.Y
            window.Position <- PixelPoint(left, top)
        | None -> ())

let openPopup (captured: Popup.Captured) (at: (int * int) option) =
    match state.Api with
    | None -> openSettings ()
    | Some api ->
        state.Popup |> Option.iter _.Close()

        let window =
            HostWindow(Title = "Ankit", Width = 560.0, Height = 640.0, Topmost = true, ShowActivated = true)

        let rememberDeck id = save { state.Settings with LastDeckId = Some id }

        window.Content <- Views.popup (api :> IAnkitApi) captured state.Settings.LastDeckId rememberDeck

        window.KeyDown.Add(fun args ->
            if args.Key = Key.Escape then
                window.Close())

        window.Closed.Add(fun _ ->
            if state.Popup = Some(window :> Window) then
                state.Popup <- None)

        match at with
        | Some point -> place window point
        | None -> window.WindowStartupLocation <- WindowStartupLocation.CenterScreen

        state.Popup <- Some window
        window.Show()
        window.Activate()

let private empty: Popup.Captured = {
    Term = ""
    Context = ""
    Source = None
}

/// Reads the selection off the UI thread (UI Automation, the clipboard, OCR take up to a few seconds), then opens the
/// popup; an empty one when nothing was selected, to type the term in
let private onHotkey () =
    Task.Run(fun () ->
        let captured = Selection.capture () |> Option.defaultValue empty
        let at = Selection.cursor ()
        Dispatcher.UIThread.Post(fun () -> openPopup captured at))
    |> ignore

let start (desktop: IClassicDesktopStyleApplicationLifetime) (app: Application) =
    desktop.ShutdownMode <- ShutdownMode.OnExplicitShutdown
    state.Settings <- Store.load ()
    connect state.Settings.Server

    let hotkey = Selection.listen onHotkey

    let hint =
        match hotkey with
        | Ok _ -> "Ankit: select a word and press Ctrl+Alt+A"
        | Error problem -> $"Ankit: %s{problem}"

    let menu = NativeMenu()

    let item (header: string) (action: unit -> unit) =
        let item = NativeMenuItem(header)
        item.Click.Add(fun _ -> action ())
        menu.Items.Add item

    item "New note…" (fun () -> openPopup empty None)
    item "Settings…" openSettings
    menu.Items.Add(NativeMenuItemSeparator())
    item "Quit" (fun () -> desktop.Shutdown())

    let tray =
        new TrayIcon(Icon = icon (), ToolTipText = hint, Menu = menu, IsVisible = true)

    tray.Clicked.Add(fun _ -> openPopup empty None)
    let icons = TrayIcons()
    icons.Add tray
    TrayIcon.SetIcons(app, icons)

    desktop.Exit.Add(fun _ ->
        hotkey |> Result.iter _.Dispose()
        tray.Dispose()
        Selection.shutdown ()
        state.Api |> Option.iter (fun api -> (api :> IDisposable).Dispose()))

    if not (ServerSettings.isComplete state.Settings.Server) then
        openSettings ()
