module Ankit.Server.Handlers

open System
open System.Threading.Channels
open System.Threading.Tasks
open Ankit.Server
open Ankit.Server.Dto
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Logging
open Oxpecker
open type Microsoft.AspNetCore.Http.StatusCodes

let ping =
    (setStatusCode Status200OK) >=> (text "PONG!")

let writeError (ctx: HttpContext) (code: int) (msg: string) (details: obj option)  =
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

let private describe (error: AnkiError) =
    match error with
    | AnkiError.CreateDeckError -> "Failed to create the deck"
    | AnkiError.AddNoteError -> "Failed to add the note, check the deck, the notetype and the number of fields"
    | AnkiError.FullSyncError failure -> $"Full sync failed: %A{failure}"

let private statusOf (error: AnkiError) =
    match error with
    | AnkiError.FullSyncError _ -> Status502BadGateway
    | _ -> Status422UnprocessableEntity

let private runCommand (ctx: HttpContext) (cmd: AnkiCommand) : Task =
    task {
        let reply = TaskCompletionSource<Result<unit, AnkiError>>(TaskCreationOptions.RunContinuationsAsynchronously)
        let ch = ctx.GetService<Channel<AnkiSyncMsg>>()
        do! ch.Writer.WriteAsync(AnkiSyncMsg(cmd, reply), ctx.RequestAborted)
        try
            match! reply.Task.WaitAsync(ctx.RequestAborted) with
            | Ok () -> ctx.SetStatusCode Status204NoContent
            | Error e -> return! writeError ctx (statusOf e) (describe e) None
        with :? TaskCanceledException when not ctx.RequestAborted.IsCancellationRequested ->
            return! writeError ctx Status503ServiceUnavailable "Server is shutting down" None
    }

module ModelBinder =
    let bindOrDeclineJson<'T when 'T: not null> (f: 'T -> EndpointHandler) : EndpointHandler =
        bindAndValidateJson<'T> <| Dto.validateModel f

    let bindOrDeclineJson1 (f: 'T0 -> 'T1 -> EndpointHandler) : 'T1 -> EndpointHandler =
        fun x -> bindOrDeclineJson (f >> (fun g -> g x))

    let bindOrDeclineQuery<'T when 'T: not null> (f: 'T -> EndpointHandler) : EndpointHandler =
        bindAndValidateQuery<'T> <| Dto.validateModel f

    let bindOrDeclineQuery1 (f: 'T0 -> 'T1 -> EndpointHandler) : 'T1 -> EndpointHandler =
        fun x -> bindOrDeclineQuery (f >> (fun g -> g x))

let createDeck (createDeck: CreateDeckDto) : EndpointHandler =
    fun (ctx: HttpContext) ->
        task {
            let deck = toDeck createDeck
            return! runCommand ctx (CreateDeck deck)
        }

let addNote (addNote: AddNoteDto) : EndpointHandler =
    let note = toAddNoteRequest addNote
    fun (ctx: HttpContext) ->
        runCommand ctx (AddNote note)

let fullSync (fullSync: FullSyncDto) : EndpointHandler =
    fun (ctx: HttpContext) ->
        runCommand ctx (FullSync fullSync.Direction)
