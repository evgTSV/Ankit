module Ankit.Client.Windows.Hotkey

open System
open System.Runtime.InteropServices
open System.Threading
open System.Threading.Tasks
open Ankit.Client.Windows.Win32

[<Flags>]
type Modifiers =
    | None = 0u
    | Alt = 0x0001u
    | Control = 0x0002u
    | Shift = 0x0004u
    | Win = 0x0008u

[<Literal>]
let private WM_QUIT = 0x0012u

/// Registers a global hotkey on its own thread, which needs a message loop for WM_HOTKEY; onPressed runs on the thread
/// pool. Disposing unregisters it. Fails when another app holds the same hotkey
let listen (modifiers: Modifiers) (key: ConsoleKey) (onPressed: unit -> unit) : Result<IDisposable, string> =
    let registered = new ManualResetEventSlim()
    let mutable error = None
    let mutable threadId = 0u

    let loop () =
        threadId <- GetCurrentThreadId()

        if not (RegisterHotKey(0n, 1, uint32 modifiers ||| MOD_NOREPEAT, uint32 key)) then
            error <-
                Some
                    $"%A{modifiers}+%A{key} can't be registered (error %d{Marshal.GetLastWin32Error()}), another app may use it"

            registered.Set()
        else
            registered.Set()
            let mutable msg = Msg()

            while GetMessageW(&msg, 0n, 0u, 0u) > 0 do
                if msg.Message = WM_HOTKEY then
                    Task.Run onPressed |> ignore

            UnregisterHotKey(0n, 1) |> ignore

    let thread = Thread(loop, IsBackground = true, Name = "Hotkey")
    thread.Start()
    registered.Wait()

    match error with
    | Some e -> Error e
    | None ->
        Ok
            {
                new IDisposable with
                    member _.Dispose() =
                        PostThreadMessageW(threadId, WM_QUIT, 0n, 0n) |> ignore
                        thread.Join() |> ignore
            }
