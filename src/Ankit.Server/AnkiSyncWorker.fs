namespace Ankit.Server

open System
open System.Threading
open System.Threading.Tasks
open Ankit.Native
open Ankit.Server.Env
open Ankit.Server.Services
open Microsoft.Extensions.Hosting
open System.Threading.Channels
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Options

[<RequireQualifiedAccess>]
type AnkiError =
    | CreateDeckError
    | AddNoteError

type AnkiCommand =
    | CreateDeck of Anki.Decks.Deck
    | AddNote of Anki.Notes.AddNoteRequest

type AnkiSyncMsg =
    | AnkiSyncMsg of cmd: AnkiCommand * reply: TaskCompletionSource<Result<unit, AnkiError>>

type AnkiSyncWorker(backend: RslibBackend, syncService: ISyncService, ch: Channel<AnkiSyncMsg>, logger: ILogger<AnkiSyncWorker>, settings: IOptionsMonitor<AnkitServerSettings>) =
    inherit BackgroundService()

    let getDebounceTime() =
        TimeSpan.FromMilliseconds(float settings.CurrentValue.SyncDebounceTime)

    let applyCommand (cmd: AnkiCommand) : Result<unit, AnkiError> =
        try
            match cmd with
            | CreateDeck deck ->
                backend.AddDeck deck |> ignore
            | AddNote noteRequest ->
                backend.AddNote noteRequest |> ignore
            Ok ()
        with :? RslibException as e ->
            logger.LogError(e, "Failed to apply {Command}", cmd)
            match cmd with
            | CreateDeck _ -> Error AnkiError.CreateDeckError
            | AddNote _ -> Error AnkiError.AddNoteError

    override this.ExecuteAsync(stoppingToken) =
        task {
            logger.LogInformation("AnkiSyncWorker is starting.")

            let rd = ch.Reader
            let requiresSync = ref false

            let applyPending() =
                let mutable msg = Unchecked.defaultof<AnkiSyncMsg>
                while rd.TryRead(&msg) do
                    let (AnkiSyncMsg(cmd, reply)) = msg
                    let result = applyCommand cmd
                    if Result.isOk result then requiresSync.Value <- true
                    reply.TrySetResult result |> ignore

            try
                let mutable running = true
                while running do
                    let! canRead = rd.WaitToReadAsync(stoppingToken)
                    if not canRead then
                        running <- false
                    else
                        applyPending()
                        use window = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken)
                        window.CancelAfter(getDebounceTime())
                        try
                            let mutable windowOpen = true
                            while windowOpen do
                                let! more = rd.WaitToReadAsync(window.Token)
                                if more then applyPending() else windowOpen <- false
                        with :? OperationCanceledException when not stoppingToken.IsCancellationRequested ->
                            ()

                        if requiresSync.Value then
                            requiresSync.Value <- false
                            syncService.TrySync() |> ignore
            with :? OperationCanceledException when stoppingToken.IsCancellationRequested ->
                ()

            let mutable msg = Unchecked.defaultof<AnkiSyncMsg>
            while rd.TryRead(&msg) do
                let (AnkiSyncMsg(_, reply)) = msg
                reply.TrySetCanceled(stoppingToken) |> ignore

            logger.LogInformation("AnkiSyncWorker is stopping due to cancellation.")
        }
