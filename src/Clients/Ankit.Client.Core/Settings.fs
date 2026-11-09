/// Where the server is and how to authenticate, edited and tested before they are saved
module Ankit.Client.Core.Settings

open System
open Ankit.Client.Core.Api

type ServerSettings = {
    /// e.g. https://ankit.example.com
    Url: string
    ApiKey: string
}

module ServerSettings =
    let empty = {
        Url = ""
        ApiKey = ""
    }

    let private isWebUrl (url: string) =
        match Uri.TryCreate(url.Trim(), UriKind.Absolute) with
        | true, uri -> uri.Scheme = "http" || uri.Scheme = "https"
        | _ -> false

    /// What's wrong with the settings, if anything
    let validate (settings: ServerSettings) =
        if not (isWebUrl settings.Url) then
            Some "The server address must be an http or https URL"
        elif String.IsNullOrWhiteSpace settings.ApiKey then
            Some "The API key is missing"
        else
            None

    let isComplete (settings: ServerSettings) = (validate settings).IsNone

[<RequireQualifiedAccess>]
type Status =
    | Editing
    | Testing
    /// The server answered with the decks
    | Connected of decks: int
    | Failed of message: string
    | Saved

type Model = {
    Settings: ServerSettings
    Status: Status
}

type Msg =
    | SetUrl of string
    | SetApiKey of string
    | Test
    | Tested of Result<Deck list, ApiError>
    | Save

[<RequireQualifiedAccess>]
type Effect =
    /// Ask the server for its decks with these settings
    | TestConnection of ServerSettings
    | Persist of ServerSettings

let init (settings: ServerSettings) =
    {
        Settings = settings
        Status = Status.Editing
    },
    []

let private invalidOr (settings: ServerSettings) (whenValid: unit -> Model * Effect list) =
    match ServerSettings.validate settings with
    | Some problem ->
        {
            Settings = settings
            Status = Status.Failed problem
        },
        []
    | None -> whenValid ()

let update (msg: Msg) (model: Model) =
    match msg with
    | SetUrl url ->
        {
            Settings = { model.Settings with Url = url }
            Status = Status.Editing
        },
        []
    | SetApiKey key ->
        {
            Settings = { model.Settings with ApiKey = key }
            Status = Status.Editing
        },
        []
    | Test ->
        invalidOr model.Settings (fun () ->
            { model with Status = Status.Testing }, [ Effect.TestConnection model.Settings ])
    | Tested(Ok decks) -> { model with Status = Status.Connected decks.Length }, []
    | Tested(Error e) -> { model with Status = Status.Failed(ApiError.describe e) }, []
    | Save ->
        invalidOr model.Settings (fun () -> { model with Status = Status.Saved }, [ Effect.Persist model.Settings ])
