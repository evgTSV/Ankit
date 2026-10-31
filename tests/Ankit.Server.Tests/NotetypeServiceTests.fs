namespace Ankit.Server.Tests

open System
open Ankit.Server.Services
open Ankit.Server.Tests.CommandAssert
open Xunit

type NotetypeServiceTests() =
    let collection = new TempCollection()
    let notetypes: INotetypeService = AnkiNotetypeService collection.Backend

    [<Fact>]
    member _.``Stock note types are listed with their fields in order``() =
        let listed = ok (notetypes.List())
        let fieldsOf name = (listed |> List.find (fun nt -> nt.Name = name)).Fields

        Assert.Equal<string list>([ "Front"; "Back" ], fieldsOf "Basic")
        Assert.Equal<string list>([ "Text"; "Back Extra" ], fieldsOf "Cloze")

    [<Fact>]
    member _.``Listed ids identify the note types``() =
        for nt in ok (notetypes.List()) do
            let byName =
                collection.Backend.GetNotetypeIdByName(Anki.Generic.String(Val = nt.Name)).Ntid

            Assert.Equal(byName, nt.Id)

    [<Fact>]
    member _.``Cloze note types are marked as cloze``() =
        let listed = ok (notetypes.List())

        Assert.Equal<string list>(
            [ "Cloze"; "Image Occlusion" ],
            listed |> List.filter _.IsCloze |> List.map _.Name |> List.sort
        )

    [<Fact>]
    member _.``Note type is read by id``() =
        let cloze =
            collection.Backend.GetNotetypeIdByName(Anki.Generic.String(Val = "Cloze")).Ntid

        let notetype = ok (notetypes.Get cloze)

        Assert.Equal("Cloze", notetype.Name)
        Assert.Equal<string list>([ "Text"; "Back Extra" ], notetype.Fields)
        Assert.True(notetype.IsCloze)

    [<Fact>]
    member _.``Missing note type is not found``() = notFound (notetypes.Get 424242L)

    interface IDisposable with
        member _.Dispose() = (collection :> IDisposable).Dispose()
