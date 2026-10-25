namespace Ankit.Server.Tests

open System
open Ankit.Server.Services
open Ankit.Server.Tests.CommandAssert
open Xunit

type DeckServiceTests() =
    let collection = new TempCollection()
    let decks: IDeckService = AnkiDeckService collection.Backend

    let listed () =
        ok (decks.List()) |> List.map (fun deck -> deck.Id, deck.Name)

    [<Fact>]
    member _.``New collection lists the default deck``() = Assert.Contains((1L, "Default"), listed ())

    [<Fact>]
    member _.``Created deck is listed under its id with a trimmed name``() =
        let id = ok (decks.Create "  Japanese ")

        Assert.Contains((id, "Japanese"), listed ())

    [<Fact>]
    member _.``Nested deck creates its parents``() =
        let id = ok (decks.Create "Japanese::Kanji")

        let names = listed ()
        Assert.Contains((id, "Japanese::Kanji"), names)
        Assert.Contains("Japanese", names |> List.map snd)

    [<Fact>]
    member _.``Renamed deck keeps its id``() =
        let id = ok (decks.Create "Japanese")

        ok (decks.Rename(id, "日本語"))

        let names = listed ()
        Assert.Contains((id, "日本語"), names)
        Assert.DoesNotContain("Japanese", names |> List.map snd)

    [<Fact>]
    member _.``Renaming a missing deck is not found``() =
        Assert.Equal(Error(CommandFailed.NotFound "Deck 424242 doesn't exist"), decks.Rename(424242L, "Nowhere"))

    [<Fact>]
    member _.``Deleted deck is no longer listed``() =
        let id = ok (decks.Create "Temporary")

        ok (decks.Delete id)

        Assert.DoesNotContain(id, listed () |> List.map fst)

    [<Fact>]
    member _.``Deleting the default deck keeps it under its default name``() =
        ok (decks.Rename(1L, "Inbox"))

        ok (decks.Delete 1L)

        Assert.Contains((1L, "Default"), listed ())

    [<Fact>]
    member _.``Deleting a missing deck is not found``() = notFound (decks.Delete 424242L)

    interface IDisposable with
        member _.Dispose() = (collection :> IDisposable).Dispose()
