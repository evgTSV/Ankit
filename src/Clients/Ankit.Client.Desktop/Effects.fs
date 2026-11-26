/// Runs the core programs' effects as Elmish commands, with the app's IAnkitApi
module Ankit.Client.Desktop.Effects

open Ankit.Client.Core
open Ankit.Client.Core.Api
open Ankit.Client.Http
open Elmish

/// Both calls at once; async has no and!, task has since F# 10
let private both (first: Async<Result<'A, ApiError>>) (second: Async<Result<'B, ApiError>>) =
    task {
        let! a = Async.StartAsTask first
        and! b = Async.StartAsTask second

        return
            match a, b with
            | Ok a, Ok b -> Ok(a, b)
            | Error e, _
            | _, Error e -> Error e
    }

let popup (api: IAnkitApi) (rememberDeck: int64 -> unit) (effects: Popup.Effect list) : Cmd<Popup.Msg> =
    effects
    |> List.map (fun effect ->
        match effect with
        | Popup.Effect.LoadLists -> Cmd.OfTask.perform (fun () -> both (api.Decks()) (api.NoteTypes())) () Popup.Loaded
        | Popup.Effect.RequestSuggestions request -> Cmd.OfAsync.perform api.Suggest request Popup.Suggested
        | Popup.Effect.AddNotes notes -> Cmd.OfAsync.perform api.AddNotes notes Popup.AddedNotes
        | Popup.Effect.RememberDeck id -> Cmd.ofEffect (fun _ -> rememberDeck id))
    |> Cmd.batch

let settings (persist: Settings.ServerSettings -> unit) (effects: Settings.Effect list) : Cmd<Settings.Msg> =
    effects
    |> List.map (fun effect ->
        match effect with
        | Settings.Effect.TestConnection server ->
            let test () =
                async {
                    use api = AnkitHttpApi.Create server
                    return! (api :> IAnkitApi).Decks()
                }

            Cmd.OfAsync.perform test () Settings.Tested
        | Settings.Effect.Persist server -> Cmd.ofEffect (fun _ -> persist server))
    |> Cmd.batch
