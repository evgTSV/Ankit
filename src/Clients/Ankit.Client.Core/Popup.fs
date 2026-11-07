/// The popup that turns a captured term into notes: edit the term and its context, ask for suggestions, edit and pick
/// the drafts, add them. An Elmish program whose update returns effects instead of commands, so that it stays pure and
/// each client runs the effects with its own IAnkitApi
module Ankit.Client.Core.Popup

open System
open Ankit.Client.Core.Api

type Draft = {
    NoteTypeId: int64
    Fields: string list
    Selected: bool
}

[<RequireQualifiedAccess>]
type Status =
    | Loading
    | Ready
    | Suggesting
    | Adding
    /// How many notes were added
    | Added of count: int
    | Failed of message: string

type Model = {
    Term: string
    Context: string
    Source: string option
    Decks: Deck list
    NoteTypes: NoteType list
    DeckId: int64 option
    Drafts: Draft list
    /// Notes of the collection that already contain the term
    Existing: int
    Status: Status
}

type Msg =
    | Loaded of Result<Deck list * NoteType list, ApiError>
    | SetTerm of string
    | SetContext of string
    | SetDeck of int64
    | Suggest
    | Suggested of Result<Suggestions, ApiError>
    | ToggleDraft of draft: int
    | SetField of draft: int * field: int * value: string
    | Add
    /// The ids of the notes added, in the order of the selected drafts; an error stops at the first failed note
    | AddedNotes of Result<int64 list, ApiError>

[<RequireQualifiedAccess>]
type Effect =
    | LoadLists
    | RequestSuggestions of SuggestRequest
    | AddNotes of NewNote list
    /// The deck to preselect next time
    | RememberDeck of int64

type Captured = {
    Term: string
    Context: string
    Source: string option
}

/// The deck preselected: the remembered one while it exists, otherwise the first
let private chooseDeck (remembered: int64 option) (decks: Deck list) =
    match remembered with
    | Some id when decks |> List.exists (fun d -> d.Id = id) -> Some id
    | _ -> decks |> List.tryHead |> Option.map _.Id

let init (captured: Captured) (rememberedDeck: int64 option) =
    {
        Term = captured.Term
        Context = captured.Context
        Source = captured.Source
        Decks = []
        NoteTypes = []
        DeckId = rememberedDeck
        Drafts = []
        Existing = 0
        Status = Status.Loading
    },
    [ Effect.LoadLists ]

let private nonEmpty (text: string) =
    if String.IsNullOrWhiteSpace text then
        None
    else
        Some(text.Trim())

let canSuggest (model: Model) =
    not (String.IsNullOrWhiteSpace model.Term)
    && (match model.Status with
        | Status.Ready
        | Status.Added _
        | Status.Failed _ -> true
        | _ -> false)

let canAdd (model: Model) =
    model.DeckId.IsSome
    && model.Drafts |> List.exists _.Selected
    && (match model.Status with
        | Status.Ready
        | Status.Added _
        | Status.Failed _ -> true
        | _ -> false)

let noteTypeName (model: Model) (id: int64) =
    model.NoteTypes
    |> List.tryFind (fun n -> n.Id = id)
    |> Option.map _.Name
    |> Option.defaultValue (string id)

let fieldNames (model: Model) (id: int64) =
    model.NoteTypes
    |> List.tryFind (fun n -> n.Id = id)
    |> Option.map _.Fields
    |> Option.defaultValue []

let private updateDraft (index: int) (change: Draft -> Draft) (drafts: Draft list) =
    drafts |> List.mapi (fun i d -> if i = index then change d else d)

let update (msg: Msg) (model: Model) =
    match msg with
    | Loaded(Ok(decks, noteTypes)) ->
        {
            model with
                Decks = decks
                NoteTypes = noteTypes
                DeckId = chooseDeck model.DeckId decks
                Status = Status.Ready
        },
        []
    | Loaded(Error e) -> { model with Status = Status.Failed(ApiError.describe e) }, []
    | SetTerm term -> { model with Term = term }, []
    | SetContext context -> { model with Context = context }, []
    | SetDeck id -> { model with DeckId = Some id }, [ Effect.RememberDeck id ]
    | Suggest when canSuggest model ->
        let request = {
            SuggestRequest.ofTerm (model.Term.Trim()) with
                Context = nonEmpty model.Context
                Source = model.Source
                DeckId = model.DeckId
        }

        { model with Status = Status.Suggesting }, [ Effect.RequestSuggestions request ]
    | Suggest -> model, []
    | Suggested(Ok suggestions) ->
        {
            model with
                Drafts =
                    suggestions.Notes
                    |> List.map (fun n -> {
                        NoteTypeId = n.NoteTypeId
                        Fields = n.Fields
                        Selected = true
                    })
                Existing = suggestions.ExistingNoteIds.Length
                Status = Status.Ready
        },
        []
    | Suggested(Error e) -> { model with Status = Status.Failed(ApiError.describe e) }, []
    | ToggleDraft index ->
        {
            model with
                Drafts =
                    model.Drafts
                    |> updateDraft index (fun d -> { d with Selected = not d.Selected })
        },
        []
    | SetField(index, field, value) ->
        {
            model with
                Drafts =
                    model.Drafts
                    |> updateDraft index (fun d -> {
                        d with
                            Fields = d.Fields |> List.mapi (fun i f -> if i = field then value else f)
                    })
        },
        []
    | Add when canAdd model ->
        let deckId = model.DeckId.Value

        let notes =
            model.Drafts
            |> List.filter _.Selected
            |> List.map (fun d -> {
                DeckId = deckId
                NoteTypeId = d.NoteTypeId
                Fields = d.Fields
                Tags = []
            })

        { model with Status = Status.Adding }, [ Effect.AddNotes notes ]
    | Add -> model, []
    | AddedNotes(Ok ids) ->
        {
            model with
                Drafts = model.Drafts |> List.filter (fun d -> not d.Selected)
                Status = Status.Added ids.Length
        },
        []
    | AddedNotes(Error e) -> { model with Status = Status.Failed(ApiError.describe e) }, []
