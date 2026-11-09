/// The server's API as clients see it: their own records, not the server's DTOs (those carry DataAnnotations and
/// [<CLIMutable>], which Fable can't compile). The JSON names are the camelCase of the field names
module Ankit.Client.Core.Api

type Deck = {
    Id: int64
    Name: string
}

type NoteType = {
    Id: int64
    Name: string
    Fields: string list
    IsCloze: bool
}

type SuggestRequest = {
    Term: string
    Context: string option
    Source: string option
    NoteTypeId: int64 option
    DeckId: int64 option
    Count: int option
    Module: string option
    Style: string option
    Instructions: string list
}

module SuggestRequest =
    let ofTerm (term: string) = {
        Term = term
        Context = None
        Source = None
        NoteTypeId = None
        DeckId = None
        Count = None
        Module = None
        Style = None
        Instructions = []
    }

type SuggestedNote = {
    NoteTypeId: int64
    /// One value per field of the note type, in its order
    Fields: string list
}

type Suggestions = {
    Notes: SuggestedNote list
    /// Notes of the collection that already contain the term
    ExistingNoteIds: int64 list
}

type NewNote = {
    DeckId: int64
    NoteTypeId: int64
    Fields: string list
    Tags: string list
}

[<RequireQualifiedAccess>]
type ApiError =
    /// No answer: the server is down, the address is wrong, the connection or TLS failed, or it timed out
    | Unreachable of message: string
    /// 401: the API key is missing or not configured on the server
    | Unauthorized
    /// Any other error status, with the server's message
    | Failed of status: int * message: string

module ApiError =
    let describe (error: ApiError) =
        match error with
        | ApiError.Unreachable message -> $"The server can't be reached: %s{message}"
        | ApiError.Unauthorized -> "The server doesn't accept the API key"
        | ApiError.Failed(503, message) -> $"The server can't do that now: %s{message}"
        | ApiError.Failed(_, message) -> message

/// The calls clients make; implemented over HttpClient in .NET and over fetch in the browser
[<Interface>]
type IAnkitApi =
    abstract Decks: unit -> Async<Result<Deck list, ApiError>>
    abstract NoteTypes: unit -> Async<Result<NoteType list, ApiError>>
    abstract Suggest: request: SuggestRequest -> Async<Result<Suggestions, ApiError>>
    /// The id of the new note
    abstract AddNote: note: NewNote -> Async<Result<int64, ApiError>>
