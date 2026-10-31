module Ankit.Server.Env

open System

let tryGetEnv (key: string) : string option =
    match Environment.GetEnvironmentVariable(key) with
    | null -> None
    | value -> Some value

let tryGetEnvOrDefault (key: string) (defaultValue: string) : string = tryGetEnv key |> Option.defaultValue defaultValue

[<Literal>]
let AnkitServerSettingsSection = "Ankit"

[<CLIMutable>]
type ApiKeySettings = {
    Name: string
    Hash: string
}

[<CLIMutable>]
type AnkitServerSettings = {
    SyncDebounceTime: int64
    CollectionPath: string
    ApiKeys: ApiKeySettings[]
    DisableAuth: bool
}

[<Literal>]
let SyncServerSettingsSection = "SyncServer"

[<CLIMutable>]
type SyncServerSettings = {
    Host: string
    Port: int
    UseHttps: bool
    Username: string
    Password: string
} with

    member this.Endpoint =
        UriBuilder(Host = this.Host, Port = this.Port, Scheme = if this.UseHttps then "https" else "http").ToString()

let defaultLangs = [ "en" ]
