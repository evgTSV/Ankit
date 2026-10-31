module Ankit.Server.Tests.JsonTests

open System.Text.Json
open Ankit.Server.Dto
open Ankit.Server.Utils
open Xunit

let private suggest (json: string) =
    JsonSerializer.Deserialize<SuggestNotesDto>(json, jsonOptions)

[<Fact>]
let ``Null option fields are read as None`` () =
    let dto =
        suggest
            """{"term":"x","noteTypeId":1,"context":null,"source":null,"deckId":null,"count":null,"module":null,"style":null,"instructions":null}"""

    Assert.Equal(None, dto.Context)
    Assert.Equal(None, dto.Source)
    Assert.Equal(None, dto.DeckId)
    Assert.Equal(None, dto.Count)
    Assert.Equal(None, dto.Module)
    Assert.Equal(None, dto.Style)
    Assert.Equal(None, dto.Instructions)

[<Fact>]
let ``Missing option fields are read as None`` () =
    let dto = suggest """{"term":"x","noteTypeId":1}"""

    Assert.Equal(None, dto.Style)
    Assert.Equal(None, dto.Count)
    Assert.Equal(None, dto.Instructions)

[<Fact>]
let ``Given option fields are read as Some`` () =
    let dto =
        suggest """{"term":"x","noteTypeId":1,"style":"AUTO","count":2,"instructions":["a"]}"""

    Assert.Equal(Some "AUTO", dto.Style)
    Assert.Equal(Some 2, dto.Count)
    Assert.Equal(Some [| "a" |], dto.Instructions)

[<Fact>]
let ``None is not written`` () =
    let json =
        JsonSerializer.Serialize(
            {
                code = 404
                message = "m"
                traceId = "t"
                timestamp = "now"
                path = "/p"
                details = None
            },
            jsonOptions
        )

    Assert.DoesNotContain("details", json)
