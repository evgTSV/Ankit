namespace Ankit.Server.Tests

open System.Linq
open Anki.Notes
open Anki.Search
open Anki.Sync
open Ankit.Native
open Ankit.Server
open Ankit.Server.Dto
open Ankit.Server.Env
open Ankit.Server.Services
open Ankit.Server.Tests.AnkiSyncServer
open Microsoft.Extensions.Logging.Abstractions
open Microsoft.Extensions.Options
open Xunit

type SyncServiceTests(server: AnkiSyncServer) =

    let assertResult (expected: SyncResult<unit>) (actual: SyncResult<unit>) =
        Assert.Equal($"%A{expected}", $"%A{actual}")

    let syncServerSettings (settings: SyncServerSettings) =
        {
            new IOptionsMonitor<SyncServerSettings> with
                member _.CurrentValue = settings
                member _.Get _ = settings
                member _.OnChange _ = null
        }

    let authService (backend: RslibBackend) (settings: SyncServerSettings) : IAuthService =
        AnkiAuthService(backend, syncServerSettings settings)

    let syncService (backend: RslibBackend) (settings: SyncServerSettings) : ISyncService =
        let authService = authService backend settings
        AnkiSyncService(backend, authService, NullLogger<AnkiSyncService>.Instance)

    let addBasicNote (backend: RslibBackend) (front: string) (back: string) =
        let basic = backend.GetNotetypeNames().Entries.Single(fun e -> e.Name = "Basic")
        let note = backend.NewNote(Anki.Notetypes.NotetypeId(Ntid = basic.Id))
        note.Fields[0] <- front
        note.Fields[1] <- back
        backend.AddNote(AddNoteRequest(Note = note, DeckId = 1L)) |> ignore

    [<LinuxDockerFact>]
    member _.``Wrong password is reported as invalid credentials``() =
        use collection = new TempCollection()
        let settings = { server.Settings Users[0] with Password = "wrong" }

        assertResult (Error <| SyncFailed.AuthFailed ErrorText.InvalidCredentialsText) ((syncService collection.Backend settings).TrySync())

    [<LinuxDockerFact>]
    member _.``Untouched collection has nothing to sync with a fresh account``() =
        use collection = new TempCollection()

        assertResult (Ok()) ((syncService collection.Backend (server.Settings Users[1])).TrySync())

    [<LinuxDockerFact>]
    member _.``First sync of a collection with notes needs a full upload``() =
        use collection = new TempCollection()
        addBasicNote collection.Backend "犬" "dog"

        let result = (syncService collection.Backend (server.Settings Users[2])).TrySync()

        assertResult (Error <| SyncFailed.FullSyncRequired SyncCollectionResponse.Types.ChangesRequired.FullUpload) result

    [<LinuxDockerFact>]
    member _.``Changes reach other clients after the initial full sync``() =
        let settings = server.Settings Users[3]
        use local = new TempCollection()
        addBasicNote local.Backend "犬" "dog"
        let service = syncService local.Backend settings
        assertResult (Ok()) (service.FullSync Upload)

        addBasicNote local.Backend "猫" "cat"
        assertResult (Ok()) (service.TrySync())
        assertResult (Ok()) (service.TrySync())

        use other = new TempCollection()
        let otherService = syncService other.Backend settings

        assertResult
            (Error(SyncFailed.FullSyncRequired SyncCollectionResponse.Types.ChangesRequired.FullDownload))
            (otherService.TrySync())

        assertResult (Ok()) (otherService.FullSync Download)
        assertResult (Ok()) (otherService.TrySync())
        Assert.Equal(2, other.Backend.SearchNotes(SearchRequest(Search = "")).Ids.Count)

    [<LinuxDockerFact>]
    member _.``Full sync with a wrong password is reported as invalid credentials``() =
        use collection = new TempCollection()
        let settings = { server.Settings Users[4] with Password = "wrong" }

        assertResult (Error <| SyncFailed.AuthFailed ErrorText.InvalidCredentialsText) ((syncService collection.Backend settings).FullSync Upload)

    interface IClassFixture<AnkiSyncServer>
