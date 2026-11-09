module Ankit.Client.Core.Tests.SettingsTests

open Ankit.Client.Core.Api
open Ankit.Client.Core.Settings
open Xunit

let private valid = {
    Url = "https://ankit.example.com"
    ApiKey = "key"
}

[<Fact>]
let ``Settings need a web address and a key`` () =
    Assert.Equal(None, ServerSettings.validate valid)
    Assert.True((ServerSettings.validate { valid with Url = "ankit.example.com" }).IsSome)
    Assert.True((ServerSettings.validate { valid with Url = "ftp://ankit.example.com" }).IsSome)
    Assert.True((ServerSettings.validate { valid with ApiKey = " " }).IsSome)

[<Fact>]
let ``Testing asks the server with the settings being edited`` () =
    let model, _ = init ServerSettings.empty

    let model =
        model
        |> update (SetUrl valid.Url)
        |> fst
        |> update (SetApiKey valid.ApiKey)
        |> fst

    let model, effects = update Test model
    Assert.Equal(Status.Testing, model.Status)
    Assert.Equal<Effect list>([ Effect.TestConnection valid ], effects)

[<Fact>]
let ``Invalid settings are neither tested nor saved`` () =
    let model, _ = init { valid with Url = "nope" }
    let tested, testEffects = update Test model
    let saved, saveEffects = update Save model
    Assert.Empty testEffects
    Assert.Empty saveEffects
    Assert.Equal(Status.Failed "The server address must be an http or https URL", tested.Status)
    Assert.Equal(tested.Status, saved.Status)

[<Fact>]
let ``A test shows how many decks the server has, or why it failed`` () =
    let model, _ = init valid

    let decks = [
        {
            Id = 1L
            Name = "Default"
        }
    ]

    Assert.Equal(Status.Connected 1, (update (Tested(Ok decks)) model |> fst).Status)

    Assert.Equal(
        Status.Failed "The server can't be reached: refused",
        (update (Tested(Error(ApiError.Unreachable "refused"))) model |> fst).Status
    )

[<Fact>]
let ``Saving persists the settings`` () =
    let model, _ = init valid
    let model, effects = update Save model
    Assert.Equal(Status.Saved, model.Status)
    Assert.Equal<Effect list>([ Effect.Persist valid ], effects)

[<Fact>]
let ``Editing after a test clears its result`` () =
    let model, _ = init valid
    let model = update (Tested(Ok [])) model |> fst |> update (SetApiKey "other") |> fst
    Assert.Equal(Status.Editing, model.Status)
