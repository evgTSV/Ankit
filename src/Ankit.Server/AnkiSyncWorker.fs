namespace Ankit.Server

open System
open System.Threading
open System.Threading.Tasks
open Ankit.Server.Env
open Ankit.Server.Services
open Microsoft.Extensions.Hosting
open System.Threading.Channels
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Options

/// What a job does to the collection, which decides whether a sync has to follow it
[<RequireQualifiedAccess>]
type JobKind =
    /// Changes the local collection, a sync pushes it to the server later
    | Change
    /// Makes both sides equal, so changes made before it need no sync
    | FullSync

/// A job queued for the worker, `Run` reports whether it succeeded
type AnkiSyncMsg = {
    Kind: JobKind
    Run: unit -> bool
    Cancel: CancellationToken -> unit
}

[<RequireQualifiedAccess>]
module AnkiSyncMsg =
    /// Queues `job` and waits until the worker has run it. Exceptions of the job are rethrown to the caller.
    let submit
        (ch: Channel<AnkiSyncMsg>)
        (kind: JobKind)
        (job: unit -> Result<'a, 'e>)
        (ct: CancellationToken)
        : Task<Result<'a, 'e>> =
        task {
            let reply =
                TaskCompletionSource<Result<'a, 'e>>(TaskCreationOptions.RunContinuationsAsynchronously)

            let run () =
                try
                    let result = job ()
                    reply.TrySetResult result |> ignore
                    Result.isOk result
                with e ->
                    reply.TrySetException e |> ignore
                    false

            let msg = {
                Kind = kind
                Run = run
                Cancel = fun token -> reply.TrySetCanceled token |> ignore
            }

            do! ch.Writer.WriteAsync(msg, ct)
            return! reply.Task.WaitAsync ct
        }

/// Runs queued jobs one at a time and syncs the changes they made once `SyncDebounceTime` has passed
type AnkiSyncWorker
    (
        syncService: ISyncService,
        ch: Channel<AnkiSyncMsg>,
        logger: ILogger<AnkiSyncWorker>,
        settings: IOptionsMonitor<AnkitServerSettings>
    ) =
    inherit BackgroundService()

    let getDebounceTime () =
        TimeSpan.FromMilliseconds(float settings.CurrentValue.SyncDebounceTime)

    override this.ExecuteAsync(stoppingToken) =
        task {
            logger.LogInformation("AnkiSyncWorker is starting.")

            let rd = ch.Reader
            let requiresSync = ref false

            let applyPending () =
                let mutable msg = Unchecked.defaultof<AnkiSyncMsg>

                while rd.TryRead(&msg) do
                    match msg.Kind, msg.Run() with
                    | JobKind.FullSync, true -> requiresSync.Value <- false
                    | JobKind.Change, true -> requiresSync.Value <- true
                    | _, false -> ()

            try
                let mutable running = true

                while running do
                    let! canRead = rd.WaitToReadAsync(stoppingToken)

                    if not canRead then
                        running <- false
                    else
                        applyPending ()
                        use window = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken)
                        window.CancelAfter(getDebounceTime ())

                        try
                            let mutable windowOpen = true

                            while windowOpen do
                                let! more = rd.WaitToReadAsync(window.Token)
                                if more then applyPending () else windowOpen <- false
                        with :? OperationCanceledException when not stoppingToken.IsCancellationRequested ->
                            ()

                        if requiresSync.Value then
                            requiresSync.Value <- false
                            syncService.TrySync() |> ignore
            with :? OperationCanceledException when stoppingToken.IsCancellationRequested ->
                ()

            let mutable msg = Unchecked.defaultof<AnkiSyncMsg>

            while rd.TryRead(&msg) do
                msg.Cancel stoppingToken

            logger.LogInformation("AnkiSyncWorker is stopping due to cancellation.")
        }
