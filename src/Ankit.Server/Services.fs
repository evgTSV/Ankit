module Ankit.Server.Services

open Anki.Backend
open Anki.Decks
open Anki.Notes
open Anki.Notetypes
open Anki.Search
open Anki.Sync
open Ankit.Native
open Ankit.Server.Dto
open Ankit.Server.Env
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Options
open FsToolkit.ErrorHandling

open type SyncCollectionResponse.Types
open type SyncStatusResponse.Types

[<RequireQualifiedAccess>]
type InfraFail =
    | BackendFailed of kind: BackendError.Types.Kind * message: string
    | NativeFailed of message: string

[<RequireQualifiedAccess>]
type SyncFailed =
    | AuthFailed of message: string
    | Aborted of message: string
    | NetworkError of message: string
    /// The server refused the sync and explained why, e.g. the account is locked
    | ServerRejected of message: string
    /// The collections diverged and one side has to overwrite the other, which needs a human decision
    | FullSyncRequired of SyncCollectionResponse.Types.ChangesRequired
    | InfraFail of InfraFail

type SyncResult<'a> = Result<'a, SyncFailed>
type BackendErrorKind = BackendError.Types.Kind

[<RequireQualifiedAccess>]
module SyncFailed =
    let classify (kind: BackendErrorKind, msg: string) : SyncFailed =
        match kind with
        | BackendErrorKind.SyncAuthError -> SyncFailed.AuthFailed msg
        | BackendErrorKind.Interrupted -> SyncFailed.Aborted msg
        | BackendErrorKind.NetworkError -> SyncFailed.NetworkError msg
        | BackendErrorKind.SyncServerMessage -> SyncFailed.ServerRejected msg
        | kind -> SyncFailed.InfraFail(InfraFail.BackendFailed(kind, msg))

    let ofInfra (error: InfraFail) : SyncFailed =
        match error with
        | InfraFail.BackendFailed(k, m) -> classify (k, m)
        | e -> SyncFailed.InfraFail e

/// Why a deck, note or notetype command failed
[<RequireQualifiedAccess>]
type CommandFailed =
    /// The deck, note or notetype doesn't exist
    | NotFound of message: string
    /// rslib refused the input, e.g. a wrong number of fields or a malformed search
    | Rejected of message: string
    | InfraFail of InfraFail

type CommandResult<'a> = Result<'a, CommandFailed>

[<RequireQualifiedAccess>]
module CommandFailed =
    let ofInfra (error: InfraFail) : CommandFailed =
        match error with
        | InfraFail.BackendFailed(BackendErrorKind.NotFoundError, msg) -> CommandFailed.NotFound msg
        | InfraFail.BackendFailed((BackendErrorKind.InvalidInput | BackendErrorKind.SearchError | BackendErrorKind.CardTypeError | BackendErrorKind.Exists),
                                  msg) -> CommandFailed.Rejected msg
        | e -> CommandFailed.InfraFail e

module ServiceUtils =

    /// Converts expected backend/native exceptions into `Error`. Everything else
    /// (i.e. genuine bugs) escapes via `reraise`. Callers at the HTTP/CLI boundary
    /// must have an outer exception handler.
    let guardCall (f: unit -> 'a) : Result<'a, InfraFail> =
        try
            Ok <| f ()
        with
        | :? BackendException as e -> Error <| InfraFail.BackendFailed(e.Error.Kind, e.Error.Message)
        | :? RslibException as e -> Error <| InfraFail.NativeFailed e.Message
        | _ -> reraise ()

    let guardCallSync (f: unit -> 'a) : SyncResult<'a> = guardCall f |> Result.mapError SyncFailed.ofInfra

    let guardCommand (f: unit -> 'a) : CommandResult<'a> = guardCall f |> Result.mapError CommandFailed.ofInfra

    /// Replaces rslib's not-found text, which is written for the desktop app ("...use the Check Database action")
    let whenNotFound (error: CommandFailed) (result: CommandResult<'a>) : CommandResult<'a> =
        result
        |> Result.mapError (function
            | CommandFailed.NotFound _ -> error
            | e -> e)

open ServiceUtils

[<Interface>]
type IErrorTextService =
    abstract InvalidCredentials: string

type AnkiErrorTextService(backend: RslibBackend) =
    interface IErrorTextService with
        member _.InvalidCredentials = backend.Translate "sync-wrong-pass"

[<Interface>]
type IAuthService =
    abstract GetAuth: unit -> SyncResult<SyncAuth>

type AnkiAuthService(backend: RslibBackend, syncServerSettings: IOptionsMonitor<SyncServerSettings>) =

    let getAuth () : SyncResult<SyncAuth> =
        let loginRequest =
            SyncLoginRequest(
                Username = syncServerSettings.CurrentValue.Username,
                Password = syncServerSettings.CurrentValue.Password,
                Endpoint = syncServerSettings.CurrentValue.Endpoint
            )

        guardCallSync (fun () -> backend.SyncLogin loginRequest)

    interface IAuthService with
        member this.GetAuth() : SyncResult<SyncAuth> = getAuth ()

[<Interface>]
type ISyncService =
    abstract TrySync: unit -> SyncResult<unit>
    abstract FullSync: SyncDirection -> SyncResult<unit>

type AnkiSyncService(backend: RslibBackend, authService: IAuthService, logger: ILogger<AnkiSyncService>) =

    /// The server may redirect us to another shard, subsequent requests have to go there
    let followRedirect (auth: SyncAuth) (newEndpoint: string option) : SyncAuth =
        match newEndpoint with
        | Some endpoint when endpoint <> auth.Endpoint ->
            logger.LogInformation("Sync server moved the collection to {Endpoint}", endpoint)
            let redirected = auth.Clone()
            redirected.Endpoint <- endpoint
            redirected
        | _ -> auth

    let syncAll (auth: SyncAuth) : SyncResult<SyncAuth> =
        result {
            let request = SyncCollectionRequest(Auth = auth, SyncMedia = true)

            let! response =
                guardCall (fun () -> backend.SyncCollection request)
                |> Result.mapError SyncFailed.ofInfra

            if not (System.String.IsNullOrEmpty response.ServerMessage) then
                logger.LogWarning("Sync server says: {ServerMessage}", response.ServerMessage)

            let auth =
                followRedirect
                    auth
                    (if response.HasNewEndpoint then
                         Some response.NewEndpoint
                     else
                         None)

            match response.Required with
            | ChangesRequired.NoChanges
            | ChangesRequired.NormalSync -> return auth
            | required -> return! Error <| SyncFailed.FullSyncRequired required
        }

    let fullSync (auth: SyncAuth) (direction: SyncDirection) : SyncResult<unit> =
        result {
            let request =
                FullUploadOrDownloadRequest(Auth = auth, Upload = (direction = Upload))

            do! guardCallSync (fun () -> backend.FullUploadOrDownload request)
            // rslib skips media without the server's media usn, so it's synced on its own
            do! guardCallSync (fun () -> backend.SyncMedia auth)
        }

    let getSyncStatus (auth: SyncAuth) : SyncResult<SyncStatusResponse> =
        guardCallSync (fun () -> backend.SyncStatus auth)

    let logSyncStatus (status: SyncStatusResponse) =
        match status.Required with
        | Required.NoChanges -> logger.LogInformation("Sync finished, collection is up to date")
        | required -> logger.LogWarning("Sync finished, but the collection still requires {Required}", required)

    let logFailure (failure: SyncFailed) =
        match failure with
        | SyncFailed.Aborted message -> logger.LogInformation("Sync was aborted: {Message}", message)
        | SyncFailed.NetworkError message ->
            logger.LogWarning("Sync failed, sync server is unreachable: {Message}", message)
        | SyncFailed.AuthFailed reason -> logger.LogWarning("Sync failed, authentication rejected: {Reason}", reason)
        | SyncFailed.ServerRejected reason ->
            logger.LogWarning("Sync failed, server rejected the request: {Reason}", reason)
        | SyncFailed.FullSyncRequired required ->
            logger.LogInformation("Sync stopped, full sync required: {Required}", required)
        | SyncFailed.InfraFail(InfraFail.BackendFailed(kind, message)) ->
            logger.LogError("Sync failed, backend error {Kind}: {Message}", kind, message)
        | SyncFailed.InfraFail(InfraFail.NativeFailed message) ->
            logger.LogError("Sync failed, native error: {Message}", message)

    interface ISyncService with
        member this.TrySync() =
            result {
                let! auth = authService.GetAuth()
                let! auth = syncAll auth
                let! status = getSyncStatus auth
                logSyncStatus status
            }
            |> Result.teeError logFailure

        member this.FullSync(direction) =
            result {
                let! auth = authService.GetAuth()
                logger.LogWarning("Starting full {Direction}, the other side's collection is replaced", direction)
                do! fullSync auth direction
                let! status = getSyncStatus auth
                logSyncStatus status
            }
            |> Result.teeError logFailure

[<Interface>]
type IDeckService =
    abstract List: unit -> CommandResult<DeckNameId list>
    /// Creates a normal deck, "::" in the name nests it under parent decks
    abstract Create: name: string -> CommandResult<int64>
    abstract Rename: deckId: int64 * name: string -> CommandResult<unit>
    /// Removes the deck and its child decks along with their cards. The default deck stays, emptied and with its name reset
    abstract Delete: deckId: int64 -> CommandResult<unit>

type AnkiDeckService(backend: RslibBackend) =
    let missing (deckId: int64) = CommandFailed.NotFound $"Deck {deckId} doesn't exist"

    interface IDeckService with
        member _.List() =
            guardCommand (fun () ->
                backend.GetDeckNames(GetDeckNamesRequest(IncludeFiltered = true)).Entries
                |> List.ofSeq)

        member _.Create name =
            guardCommand (fun () ->
                let deck = backend.NewDeck()
                deck.Name <- name.Trim()
                backend.AddDeck(deck).Id)

        member _.Rename(deckId, name) =
            guardCommand (fun () ->
                backend.RenameDeck(RenameDeckRequest(DeckId = deckId, NewName = name.Trim()))
                |> ignore)
            |> whenNotFound (missing deckId)

        member _.Delete deckId =
            guardCommand (fun () ->
                // rslib skips unknown decks silently
                backend.GetDeck(DeckId(Did = deckId)) |> ignore
                let ids = DeckIds()
                ids.Dids.Add deckId
                backend.RemoveDecks ids |> ignore)
            |> whenNotFound (missing deckId)

/// The editable part of a note, fields are in the order of its notetype
type NoteContent = {
    Fields: string list
    Tags: string list
}

[<Interface>]
type INoteService =
    abstract Get: noteId: int64 -> CommandResult<Note>
    /// Ids of the notes matching an Anki search, e.g. "deck:Japanese tag:verb"
    abstract Search: query: string -> CommandResult<int64 list>
    /// Adds the note with its cards in `deckId`, which must be a normal deck
    abstract Add: deckId: int64 * notetypeId: int64 * content: NoteContent -> CommandResult<int64>
    /// Replaces the fields, and the tags when given
    abstract Update: noteId: int64 * fields: string list * tags: string list option -> CommandResult<unit>
    /// Removes the note with all of its cards
    abstract Delete: noteId: int64 -> CommandResult<unit>

type AnkiNoteService(backend: RslibBackend) =
    let getNote (noteId: int64) = backend.GetNote(NoteId(Nid = noteId))

    let missing (noteId: int64) = CommandFailed.NotFound $"Note {noteId} doesn't exist"

    interface INoteService with
        member _.Get noteId =
            guardCommand (fun () -> getNote noteId) |> whenNotFound (missing noteId)

        member _.Search query =
            guardCommand (fun () -> backend.SearchNotes(SearchRequest(Search = query)).Ids |> List.ofSeq)

        member _.Add(deckId, notetypeId, content) =
            result {
                // rslib puts the cards into the default deck when the deck is missing or filtered, without telling
                let! deck =
                    guardCommand (fun () -> backend.GetDeck(DeckId(Did = deckId)))
                    |> whenNotFound (CommandFailed.Rejected $"Deck {deckId} doesn't exist")

                if deck.KindCase = Deck.KindOneofCase.Filtered then
                    return! Error(CommandFailed.Rejected $"Deck {deckId} is filtered, notes can't be added to it")

                // rslib only says "missing note type"
                do!
                    guardCommand (fun () -> backend.GetNotetype(NotetypeId(Ntid = notetypeId)) |> ignore)
                    |> whenNotFound (CommandFailed.Rejected $"Note type {notetypeId} doesn't exist")

                return!
                    guardCommand (fun () ->
                        let note = Note(NotetypeId = notetypeId)
                        note.Fields.AddRange content.Fields
                        note.Tags.AddRange content.Tags
                        backend.AddNote(AddNoteRequest(Note = note, DeckId = deckId)).NoteId)
            }

        member _.Update(noteId, fields, tags) =
            guardCommand (fun () ->
                let note = getNote noteId
                note.Fields.Clear()
                note.Fields.AddRange fields

                tags
                |> Option.iter (fun tags ->
                    note.Tags.Clear()
                    note.Tags.AddRange tags)

                let request = UpdateNotesRequest()
                request.Notes.Add note
                backend.UpdateNotes request |> ignore)
            |> whenNotFound (missing noteId)

        member _.Delete noteId =
            guardCommand (fun () ->
                // rslib skips unknown notes silently
                getNote noteId |> ignore
                let request = RemoveNotesRequest()
                request.NoteIds.Add noteId
                backend.RemoveNotes request |> ignore)
            |> whenNotFound (missing noteId)

type NotetypeInfo = {
    Id: int64
    Name: string
    /// Field names in the order notes list their fields
    Fields: string list
}

[<Interface>]
type INotetypeService =
    abstract List: unit -> CommandResult<NotetypeInfo list>

type AnkiNotetypeService(backend: RslibBackend) =
    interface INotetypeService with
        member _.List() =
            guardCommand (fun () ->
                backend.GetNotetypeNames().Entries
                |> Seq.map (fun entry -> {
                    Id = entry.Id
                    Name = entry.Name
                    Fields = backend.GetFieldNames(NotetypeId(Ntid = entry.Id)).Vals |> List.ofSeq
                })
                |> List.ofSeq)
