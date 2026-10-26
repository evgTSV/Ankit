namespace Ankit.Server.Tests

open System
open Ankit.Server.Services
open Ankit.Server.Tests.CommandAssert
open Xunit

type NoteServiceTests() =
    let collection = new TempCollection()
    let notes: INoteService = AnkiNoteService collection.Backend

    let basic =
        collection.Backend.GetNotetypeIdByName(Anki.Generic.String(Val = "Basic")).Ntid

    let defaultDeck = 1L

    let content fields tags = {
        Fields = fields
        Tags = tags
    }

    let addBasic front back tags =
        ok (notes.Add(defaultDeck, basic, content [ front; back ] tags))

    [<Fact>]
    member _.``Added note can be read back``() =
        let id = addBasic "犬" "dog" [ "animal" ]

        let note = ok (notes.Get id)

        Assert.Equal(id, note.Id)
        Assert.Equal(basic, note.NotetypeId)
        Assert.Equal<string list>([ "犬"; "dog" ], List.ofSeq note.Fields)
        Assert.Equal<string list>([ "animal" ], List.ofSeq note.Tags)

    [<Fact>]
    member _.``Note with a wrong number of fields is rejected``() =
        rejected (notes.Add(defaultDeck, basic, content [ "only front" ] []))

    [<Fact>]
    member _.``Note of a missing note type is rejected``() =
        rejected (notes.Add(defaultDeck, 424242L, content [ "犬"; "dog" ] []))

    [<Fact>]
    member _.``Note in a missing deck is rejected``() =
        rejected (notes.Add(424242L, basic, content [ "犬"; "dog" ] []))

    [<Fact>]
    member _.``Note in a filtered deck is rejected``() =
        addBasic "犬" "dog" [] |> ignore

        let filtered =
            collection.Backend.GetOrCreateFilteredDeck(Anki.Decks.DeckId(Did = 0L))

        filtered.Name <- "Review"
        filtered.Config.SearchTerms[0].Search <- "dog"
        let filteredId = collection.Backend.AddOrUpdateFilteredDeck(filtered).Id

        rejected (notes.Add(filteredId, basic, content [ "猫"; "cat" ] []))

    [<Fact>]
    member _.``Reading a missing note is not found``() = notFound (notes.Get 424242L)

    [<Fact>]
    member _.``Search finds notes by content and tag``() =
        let dog = addBasic "犬" "dog" [ "animal" ]
        let cat = addBasic "猫" "cat" [ "animal" ]
        let _ = addBasic "水" "water" []

        Assert.Equal<int64 list>([ dog ], ok (notes.Search "dog"))
        Assert.Equal<Set<int64>>(set [ dog; cat ], set (ok (notes.Search "tag:animal")))

    [<Fact>]
    member _.``Malformed search is rejected``() = rejected (notes.Search "(dog")

    [<Fact>]
    member _.``Update replaces the fields and keeps the tags``() =
        let id = addBasic "犬" "dgo" [ "animal" ]

        ok (notes.Update(id, [ "犬"; "dog" ], None))

        let note = ok (notes.Get id)
        Assert.Equal<string list>([ "犬"; "dog" ], List.ofSeq note.Fields)
        Assert.Equal<string list>([ "animal" ], List.ofSeq note.Tags)

    [<Fact>]
    member _.``Update replaces the tags when given``() =
        let id = addBasic "犬" "dog" [ "animal" ]

        ok (notes.Update(id, [ "犬"; "dog" ], Some [ "pet"; "n5" ]))

        Assert.Equal<Set<string>>(set [ "pet"; "n5" ], set (ok (notes.Get id)).Tags)

    [<Fact>]
    member _.``Update with a wrong number of fields is rejected``() =
        let id = addBasic "犬" "dog" []

        rejected (notes.Update(id, [ "犬"; "dog"; "extra" ], None))

    [<Fact>]
    member _.``Updating a missing note is not found``() = notFound (notes.Update(424242L, [ "犬"; "dog" ], None))

    [<Fact>]
    member _.``Deleted note is gone``() =
        let id = addBasic "犬" "dog" []

        ok (notes.Delete id)

        notFound (notes.Get id)
        Assert.Empty(ok (notes.Search "dog"))

    [<Fact>]
    member _.``Deleting a missing note is not found``() = notFound (notes.Delete 424242L)

    interface IDisposable with
        member _.Dispose() = (collection :> IDisposable).Dispose()
