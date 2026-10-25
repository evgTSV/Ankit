namespace Ankit.Server.Tests

open System
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks
open Ankit.Server
open Ankit.Server.Env
open Ankit.Server.Services
open Microsoft.Extensions.Logging.Abstractions
open Microsoft.Extensions.Options
open Xunit

/// Counts the syncs the worker starts, without a sync server
type private CountingSyncService() =
    let mutable syncs = 0
    let synced = new SemaphoreSlim(0)

    member _.Syncs = Volatile.Read &syncs

    member _.WaitForSync(timeout: TimeSpan) = synced.WaitAsync timeout

    interface ISyncService with
        member _.TrySync() =
            Interlocked.Increment &syncs |> ignore
            synced.Release() |> ignore
            Ok()

        member _.FullSync _ = Ok()

type AnkiSyncWorkerTests() =
    let debounce = TimeSpan.FromMilliseconds 200.
    let sync = CountingSyncService()

    let ch =
        Channel.CreateUnbounded<AnkiSyncMsg>(UnboundedChannelOptions(SingleReader = true))

    let settings =
        let value = {
            SyncDebounceTime = int64 debounce.TotalMilliseconds
            CollectionPath = ""
        }

        {
            new IOptionsMonitor<AnkitServerSettings> with
                member _.CurrentValue = value
                member _.Get _ = value
                member _.OnChange _ = null
        }

    let worker =
        new AnkiSyncWorker(sync, ch, NullLogger<AnkiSyncWorker>.Instance, settings)

    do worker.StartAsync(CancellationToken.None).Wait()

    let submit kind (job: unit -> Result<'a, string>) = AnkiSyncMsg.submit ch kind job CancellationToken.None

    /// Long enough for a debounce window to close and its sync to start
    let settle () = Task.Delay(debounce * 3.)

    [<Fact>]
    member _.``Changes in one window are synced once``() : Task =
        task {
            for i in 1..3 do
                let! result = submit JobKind.Change (fun () -> Ok i)
                Assert.Equal(Ok i, result)

            let! synced = sync.WaitForSync(TimeSpan.FromSeconds 10.)
            Assert.True(synced, "No sync after the changes")
            do! settle ()
            Assert.Equal(1, sync.Syncs)
        }

    [<Fact>]
    member _.``Failed change is not synced``() : Task =
        task {
            let! result = submit JobKind.Change (fun () -> Error "rejected")
            Assert.Equal(Error "rejected", result)

            do! settle ()
            Assert.Equal(0, sync.Syncs)
        }

    [<Fact>]
    member _.``Full sync makes the pending sync unnecessary``() : Task =
        task {
            let! _ = submit JobKind.Change (fun () -> Ok())
            let! _ = submit JobKind.FullSync (fun () -> Ok())

            do! settle ()
            Assert.Equal(0, sync.Syncs)
        }

    [<Fact>]
    member _.``Exception of a job reaches the caller and the worker keeps running``() : Task =
        task {
            let! _ =
                Assert.ThrowsAsync<InvalidOperationException>(fun () ->
                    submit JobKind.Change (fun () -> invalidOp "boom") :> Task)

            let! result = submit JobKind.Change (fun () -> Ok 42)
            Assert.Equal(Ok 42, result)
        }

    interface IDisposable with
        member _.Dispose() =
            worker.StopAsync(CancellationToken.None).Wait()
            worker.Dispose()
