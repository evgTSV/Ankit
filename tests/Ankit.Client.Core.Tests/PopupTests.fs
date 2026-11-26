module Ankit.Client.Core.Tests.PopupTests

open Ankit.Client.Core.Api
open Ankit.Client.Core.Popup
open Xunit

let private decks = [
    {
        Id = 1L
        Name = "Default"
    }
    {
        Id = 2L
        Name = "English"
    }
]

let private noteTypes = [
    {
        Id = 10L
        Name = "Cloze"
        Fields = [ "Text"; "Back Extra" ]
        IsCloze = true
    }
]

let private captured = {
    Term = "bank"
    Context = "We had a picnic on the bank of the river."
    Source = Some "https://example.com"
}

/// The popup after the lists arrived
let private ready (remembered: int64 option) =
    let model, _ = init captured remembered
    fst (update (Loaded(Ok(decks, noteTypes))) model)

let private suggestions = {
    Notes = [
        {
            NoteTypeId = 10L
            Fields = [ "A picnic on the {{c1::bank}} of the river"; "land beside a river" ]
        }
        {
            NoteTypeId = 10L
            Fields = [ "The {{c1::bank}} was steep"; "" ]
        }
    ]
    ExistingNoteIds = [ 5L ]
}

let private withDrafts () = fst (update (Suggested(Ok suggestions)) (ready None))

[<Fact>]
let ``Opening loads the decks and note types`` () =
    let model, effects = init captured None
    Assert.Equal(Status.Loading, model.Status)
    Assert.Equal<Effect list>([ Effect.LoadLists ], effects)

[<Fact>]
let ``The remembered deck is preselected while it exists, otherwise the first`` () =
    Assert.Equal(Some 2L, (ready (Some 2L)).DeckId)
    Assert.Equal(Some 1L, (ready (Some 99L)).DeckId)
    Assert.Equal(Some 1L, (ready None).DeckId)

[<Fact>]
let ``Suggest sends the trimmed term with its context, source and deck`` () =
    let model = { ready (Some 2L) with Term = "  bank " }
    let model, effects = update Suggest model
    Assert.Equal(Status.Suggesting, model.Status)

    match effects with
    | [ Effect.RequestSuggestions request ] ->
        Assert.Equal("bank", request.Term)
        Assert.Equal(Some captured.Context, request.Context)
        Assert.Equal(captured.Source, request.Source)
        Assert.Equal(Some 2L, request.DeckId)
    | other -> failwith $"Unexpected effects %A{other}"

[<Fact>]
let ``An empty context isn't sent`` () =
    let model = { ready None with Context = "  " }

    match snd (update Suggest model) with
    | [ Effect.RequestSuggestions request ] -> Assert.Equal(None, request.Context)
    | other -> failwith $"Unexpected effects %A{other}"

[<Fact>]
let ``Nothing is suggested for an empty term or while a request runs`` () =
    Assert.Empty(snd (update Suggest { ready None with Term = " " }))
    Assert.Empty(snd (update Suggest { ready None with Status = Status.Suggesting }))

[<Fact>]
let ``Suggestions become selected drafts`` () =
    let model = withDrafts ()
    Assert.Equal(2, model.Drafts.Length)
    Assert.True(model.Drafts |> List.forall _.Selected)
    Assert.Equal(1, model.Existing)
    Assert.Equal(Status.Ready, model.Status)

[<Fact>]
let ``Only selected drafts are added, with their edited fields, to the chosen deck`` () =
    let model =
        withDrafts ()
        |> update (ToggleDraft 1)
        |> fst
        |> update (SetField(0, 1, "the land along a river"))
        |> fst
        |> update (SetDeck 2L)
        |> fst

    let model, effects = update Add model
    Assert.Equal(Status.Adding, model.Status)

    Assert.Equal<Effect list>(
        [
            Effect.AddNotes [
                {
                    DeckId = 2L
                    NoteTypeId = 10L
                    Fields = [ "A picnic on the {{c1::bank}} of the river"; "the land along a river" ]
                    Tags = []
                }
            ]
        ],
        effects
    )

[<Fact>]
let ``Added drafts leave the list and the rest can be added after`` () =
    let model = withDrafts () |> update (ToggleDraft 1) |> fst
    let model = update Add model |> fst |> update (AddedNotes(Ok [ 100L ])) |> fst
    Assert.Equal(Status.Added 1, model.Status)
    Assert.Equal(1, model.Drafts.Length)
    Assert.False(canAdd model)
    Assert.True(canAdd (update (ToggleDraft 0) model |> fst))

[<Fact>]
let ``Choosing a deck remembers it`` () =
    Assert.Equal<Effect list>([ Effect.RememberDeck 2L ], snd (update (SetDeck 2L) (ready None)))

[<Fact>]
let ``Errors are shown and allow trying again`` () =
    let model = update (Suggested(Error ApiError.Unauthorized)) (ready None) |> fst
    Assert.Equal(Status.Failed "The server doesn't accept the API key", model.Status)
    Assert.True(canSuggest model)

[<Fact>]
let ``Field names come from the note type`` () =
    let model = ready None
    Assert.Equal<string list>([ "Text"; "Back Extra" ], fieldNames model 10L)
    Assert.Equal("Cloze", noteTypeName model 10L)
    Assert.Equal("42", noteTypeName model 42L)
