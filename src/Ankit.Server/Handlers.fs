module Ankit.Server.Handlers

open System
open System.Threading.Channels
open System.Threading.Tasks
open Ankit.Server
open Ankit.Server.Dto
open Ankit.Server.Services
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Logging
open Oxpecker
open type Microsoft.AspNetCore.Http.StatusCodes

let ping = (setStatusCode Status200OK) >=> (text "PONG!")

let writeError (ctx: HttpContext) (code: int) (msg: string) (details: obj option) =
    let logger = ctx.GetLogger()
    logger.LogWarning("Request failed with {StatusCode}: {Message}", code, msg)

    let error = {
        code = code
        message = msg
        traceId = ctx.TraceIdentifier
        timestamp = DateTime.UtcNow.ToString("O")
        path = ctx.Request.Path.Value
        details = details
    }

    ctx.SetStatusCode(code)

    ctx.WriteJson <| error

let private writeCommandError (ctx: HttpContext) (error: CommandFailed) =
    match error with
    | CommandFailed.NotFound msg -> writeError ctx Status404NotFound msg None
    | CommandFailed.Rejected msg -> writeError ctx Status422UnprocessableEntity msg None
    | CommandFailed.InfraFail failure ->
        ctx.GetLogger().LogError("Command failed in the backend: {Failure}", failure)
        writeError ctx Status500InternalServerError "Internal server error: the backend failed" None

let private writeJson (code: int) (value: 'T) (ctx: HttpContext) =
    ctx.SetStatusCode code
    ctx.WriteJson value

let private noContent (ctx: HttpContext) : Task =
    ctx.SetStatusCode Status204NoContent
    Task.CompletedTask

/// Runs a read, which needs no sync and so doesn't go through the worker
let private query (job: unit -> CommandResult<'a>) (onOk: 'a -> HttpContext -> Task) : EndpointHandler =
    fun ctx ->
        match job () with
        | Ok value -> onOk value ctx
        | Error e -> writeCommandError ctx e

/// Queues a job for the worker and answers once it has run
let private submit
    (kind: JobKind)
    (job: unit -> Result<'a, 'e>)
    (onOk: 'a -> HttpContext -> Task)
    (onError: 'e -> HttpContext -> Task)
    : EndpointHandler =
    fun ctx ->
        task {
            let ch = ctx.GetService<Channel<AnkiSyncMsg>>()

            try
                match! AnkiSyncMsg.submit ch kind job ctx.RequestAborted with
                | Ok value -> return! onOk value ctx
                | Error e -> return! onError e ctx
            with :? TaskCanceledException when not ctx.RequestAborted.IsCancellationRequested ->
                return! writeError ctx Status503ServiceUnavailable "Server is shutting down" None
        }

let private change (job: unit -> CommandResult<'a>) (onOk: 'a -> HttpContext -> Task) : EndpointHandler =
    submit JobKind.Change job onOk (fun e ctx -> writeCommandError ctx e)

let private created (id: int64) = writeJson Status201Created { CreatedDto.Id = id }

module ModelBinder =
    let bindOrDeclineJson<'T when 'T: not null> (f: 'T -> EndpointHandler) : EndpointHandler =
        bindAndValidateJson<'T> <| Dto.validateModel f

    let bindOrDeclineJson1 (f: 'T0 -> 'T1 -> EndpointHandler) : 'T1 -> EndpointHandler =
        fun x -> bindOrDeclineJson (f >> (fun g -> g x))

    let bindOrDeclineQuery<'T when 'T: not null> (f: 'T -> EndpointHandler) : EndpointHandler =
        fun ctx ->
            // Oxpecker's query binder throws on an empty query string, so validate an empty model instead
            if ctx.Request.Query.Count = 0 then
                Dto.validateModel f (validateModel (Activator.CreateInstance<'T>())) ctx
            else
                bindAndValidateQuery<'T> (Dto.validateModel f) ctx

    let bindOrDeclineQuery1 (f: 'T0 -> 'T1 -> EndpointHandler) : 'T1 -> EndpointHandler =
        fun x -> bindOrDeclineQuery (f >> (fun g -> g x))

// --- Decks ---

let listDecks: EndpointHandler =
    fun ctx ->
        let decks = ctx.GetService<IDeckService>()

        let toDto (deck: Anki.Decks.DeckNameId) : DeckDto = {
            Id = deck.Id
            Name = deck.Name
        }

        query decks.List (fun list -> writeJson Status200OK (list |> List.map toDto)) ctx

let createDeck (dto: CreateDeckDto) : EndpointHandler =
    fun ctx ->
        let decks = ctx.GetService<IDeckService>()
        change (fun () -> decks.Create dto.Name) created ctx

let renameDeck (dto: RenameDeckDto) (deckId: int64) : EndpointHandler =
    fun ctx ->
        let decks = ctx.GetService<IDeckService>()
        change (fun () -> decks.Rename(deckId, dto.Name)) (fun () -> noContent) ctx

let deleteDeck (deckId: int64) : EndpointHandler =
    fun ctx ->
        let decks = ctx.GetService<IDeckService>()
        change (fun () -> decks.Delete deckId) (fun () -> noContent) ctx

// --- Notes ---

let private toNoteDto (note: Anki.Notes.Note) : NoteDto = {
    Id = note.Id
    NoteTypeId = note.NotetypeId
    Fields = Array.ofSeq note.Fields
    Tags = Array.ofSeq note.Tags
}

let getNote (noteId: int64) : EndpointHandler =
    fun ctx ->
        let notes = ctx.GetService<INoteService>()
        query (fun () -> notes.Get noteId) (toNoteDto >> writeJson Status200OK) ctx

let searchNotes (dto: SearchNotesDto) : EndpointHandler =
    fun ctx ->
        let notes = ctx.GetService<INoteService>()
        query (fun () -> notes.Search dto.query) (Array.ofList >> writeJson Status200OK) ctx

let addNote (dto: AddNoteDto) : EndpointHandler =
    fun ctx ->
        let notes = ctx.GetService<INoteService>()

        let content = {
            Fields = List.ofArray dto.Fields
            Tags = dto.Tags |> Option.map List.ofArray |> Option.defaultValue []
        }

        change (fun () -> notes.Add(dto.DeckId, dto.NoteTypeId, content)) created ctx

let updateNote (dto: UpdateNoteDto) (noteId: int64) : EndpointHandler =
    fun ctx ->
        let notes = ctx.GetService<INoteService>()
        let tags = dto.Tags |> Option.map List.ofArray

        change (fun () -> notes.Update(noteId, List.ofArray dto.Fields, tags)) (fun () -> noContent) ctx

let deleteNote (noteId: int64) : EndpointHandler =
    fun ctx ->
        let notes = ctx.GetService<INoteService>()
        change (fun () -> notes.Delete noteId) (fun () -> noContent) ctx

// --- Note types ---

let listNoteTypes: EndpointHandler =
    fun ctx ->
        let notetypes = ctx.GetService<INotetypeService>()

        let toDto (notetype: NotetypeInfo) : NoteTypeDto = {
            Id = notetype.Id
            Name = notetype.Name
            Fields = Array.ofList notetype.Fields
        }

        query notetypes.List (fun list -> writeJson Status200OK (list |> List.map toDto)) ctx

// --- Sync ---

let fullSync (dto: FullSyncDto) : EndpointHandler =
    fun ctx ->
        let sync = ctx.GetService<ISyncService>()

        submit
            JobKind.FullSync
            (fun () -> sync.FullSync dto.Direction)
            (fun () -> noContent)
            (fun failure ctx -> writeError ctx Status502BadGateway $"Full sync failed: %A{failure}" None)
            ctx
