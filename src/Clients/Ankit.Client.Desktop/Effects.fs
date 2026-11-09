/// Runs the core programs' effects as Elmish commands, with the app's IAnkitApi
module Ankit.Client.Desktop.Effects

open Ankit.Client.Core
open Ankit.Client.Core.Api
open Ankit.Client.Http
open Elmish

let private both (first: Async<Result<'A, ApiError>>) (second: Async<Result<'B, ApiError>>) =
    async {
        let! a = first
        let! b = second

        return
            match a, b with
            | Ok a, Ok b -> Ok(a, b)
            | Error e, _
            | _, Error e -> Error e
    }

/// One note after the other; the first error stops the rest
let private addAll (api: IAnkitApi) (notes: NewNote list) =
    let rec loop (notes: NewNote list) (ids: int64 list) =
        async {
            match notes with
            | [] -> return Ok(List.rev ids)
            | note :: rest ->
                match! api.AddNote note with
                | Ok id -> return! loop rest (id :: ids)
                | Error e -> return Error e
        }

    loop notes []

let popup (api: IAnkitApi) (rememberDeck: int64 -> unit) (effects: Popup.Effect list) : Cmd<Popup.Msg> =
    effects
    |> List.map (fun effect ->
        match effect with
        | Popup.Effect.LoadLists ->
            Cmd.OfAsync.perform (fun () -> both (api.Decks()) (api.NoteTypes())) () Popup.Loaded
        | Popup.Effect.RequestSuggestions request -> Cmd.OfAsync.perform api.Suggest request Popup.Suggested
        | Popup.Effect.AddNotes notes -> Cmd.OfAsync.perform (addAll api) notes Popup.AddedNotes
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
