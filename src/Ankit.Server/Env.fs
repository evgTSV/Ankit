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

[<Literal>]
let LlmSettingsSection = "Llm"

/// How the model is asked for JSON; not every OpenAI-compatible server supports JSON schemas
type LlmResponseFormat =
    | JsonSchema = 0
    | JsonObject = 1
    | Prompt = 2

[<CLIMutable>]
type LlmSettings = {
    /// Base URL of an OpenAI-compatible API, empty for OpenAI itself
    Provider: string
    ApiKey: string
    /// Card suggestions are off while it's empty
    Model: string
    ResponseFormat: LlmResponseFormat
    TimeoutSeconds: int
    /// The suggestion module used when a request names none
    Module: string
    /// Added to every prompt, e.g. the learner's native language or the style of the cards
    Instructions: string[]
}
