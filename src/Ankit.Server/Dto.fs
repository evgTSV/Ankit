module Ankit.Server.Dto

open System
open System.ComponentModel.DataAnnotations
open System.Threading.Tasks
open Ankit.Server.Services
open Ankit.Server.Utils
open Microsoft.AspNetCore.Http
open Microsoft.FSharp.Reflection
open Oxpecker

open type Microsoft.AspNetCore.Http.StatusCodes

[<CLIMutable>]
type CreateDeckDto = {
    [<Required(ErrorMessage = "Deck name is required")>]
    Name: string
}

[<CLIMutable>]
type AddNoteDto = {
    [<NumericId>]
    DeckId: int64

    [<NumericId>]
    NoteTypeId: int64

    [<Required(ErrorMessage = "Fields are required"); MinLength(1, ErrorMessage = "Note needs at least one field")>]
    Fields: string array

    Tags: string array option
}

[<CLIMutable>]
type FullSyncDto = {
    /// "upload" replaces the server's collection with ours, "download" replaces ours with the server's
    [<Required(ErrorMessage = "Direction is required")>]
    Direction: SyncDirection
}

[<CLIMutable>]
type FieldError = { field: string; issue: string; rejectedValue: obj option }

[<CLIMutable>]
type ValidationError = {
    code: int
    message: string
    traceId: string
    timestamp: string
    path: string
    fieldErrors: FieldError[]
}

[<CLIMutable>]
type ApiError = {
    code: int
    message: string
    traceId: string
    timestamp: string
    path: string
    details: obj option
}

let fetchInvalidFieldsFromModel (model: 'T) (errors: ValidationResult seq) =
    // Option fields report the wrapped value, None and null report nothing
    let unwrap (value: obj) : obj option =
        if isNull value then
            None
        else
            let ty = value.GetType()

            if ty.IsGenericType && ty.GetGenericTypeDefinition() = typedefof<option<_>> then
                FSharpValue.GetUnionFields(value, ty) |> snd |> Array.tryHead
            else
                Some value

    let lookUpValue (name: string) : obj option =
        let m = box model

        if isNull m || not (FSharpType.IsRecord(m.GetType())) then
            None
        else
            FSharpType.GetRecordFields(m.GetType())
            |> Array.tryFind (fun p -> p.Name = name)
            |> Option.bind (fun p -> FSharpValue.GetRecordField(m, p) |> unwrap)

    errors
    |> Seq.collect (fun err ->
        // A rule may cover several members, each of them gets its own entry
        match err.MemberNames |> Seq.map _.Trim() |> List.ofSeq with
        | [] -> [
            {
                field = ""
                issue = err.ErrorMessage
                rejectedValue = None
            }
          ]
        | names ->
            names
            |> List.map (fun name -> {
                // Same naming as the JSON body, so it matches the fields the client sent
                field = jsonOptions.PropertyNamingPolicy.ConvertName name
                issue = err.ErrorMessage
                rejectedValue = lookUpValue name
            }))
    |> Seq.toArray

let buildValidationError (invalidFields: FieldError[]) (ctx: HttpContext) = {
    ValidationError.code = Status422UnprocessableEntity
    message = "Validation failed"
    traceId = ctx.TraceIdentifier
    timestamp = DateTime.UtcNow.ToString("O")
    path = ctx.Request.Path.Value
    fieldErrors = invalidFields
}

let validateModel (f: 'T -> EndpointHandler) : ModelValidationResult<'T> -> HttpContext -> Task =
    (fun res ctx ->
        match res with
        | ModelValidationResult.Valid dto -> f dto ctx
        | ModelValidationResult.Invalid(model, errors) ->
            task {
                let invalidFields = fetchInvalidFieldsFromModel model errors.All
                let valError = buildValidationError invalidFields ctx

                ctx.SetStatusCode(Status422UnprocessableEntity)
                return! json valError ctx
            })

let toDeck (dto: CreateDeckDto) : Anki.Decks.Deck =
    // Mirrors Deck::new_normal in rslib
    Anki.Decks.Deck(
        Name = dto.Name.Trim(),
        Common = Anki.Decks.Deck.Types.Common(StudyCollapsed = true, BrowserCollapsed = true),
        Normal = Anki.Decks.Deck.Types.Normal(ConfigId = 1L)
    )

let toAddNoteRequest (dto: AddNoteDto) : Anki.Notes.AddNoteRequest =
    let note = Anki.Notes.Note(NotetypeId = dto.NoteTypeId)
    note.Fields.AddRange dto.Fields
    dto.Tags |> Option.iter note.Tags.AddRange
    Anki.Notes.AddNoteRequest(Note = note, DeckId = dto.DeckId)
