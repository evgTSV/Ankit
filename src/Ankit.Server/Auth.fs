module Ankit.Server.Auth

open System
open System.Collections.Generic
open System.Security.Claims
open System.Security.Cryptography
open System.Text
open System.Text.Encodings.Web
open System.Threading.Tasks
open Ankit.Server.Dto
open Ankit.Server.Env
open FsToolkit.ErrorHandling
open Microsoft.AspNetCore.Authentication
open Microsoft.AspNetCore.Authorization
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.OpenApi
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Options
open Microsoft.OpenApi
open Oxpecker

open type Microsoft.AspNetCore.Http.StatusCodes

[<Literal>]
let SchemeName = "ApiKey"

[<Literal>]
let private KeyPrefix = "ankit_"

[<Literal>]
let private HashPrefix = "sha256:"

[<Literal>]
let private BearerPrefix = "Bearer "

module ApiKey =
    let generate () : string =
        KeyPrefix + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes 32)

    let private digest (key: string) = SHA256.HashData(Encoding.UTF8.GetBytes key)

    let hash (key: string) : string = HashPrefix + Convert.ToHexStringLower(digest key)

    let parseHash (hash: string) : byte[] option =
        if hash.StartsWith(HashPrefix, StringComparison.Ordinal) then
            try
                Some(Convert.FromHexString(hash.Substring HashPrefix.Length))
                |> Option.filter (fun bytes -> bytes.Length = SHA256.HashSizeInBytes)
            with :? FormatException ->
                None
        else
            None

    let matches (key: string) (hash: byte[]) = CryptographicOperations.FixedTimeEquals(digest key, hash)

type ApiKeyStore = private {
    Keys: (string * byte[]) list
    Disabled: bool
} with

    member this.IsDisabled = this.Disabled

module ApiKeyStore =
    let load (settings: AnkitServerSettings) : Result<ApiKeyStore, string> =
        let parse (index: int) (key: ApiKeySettings) =
            let name = Option.ofObj key.Name |> Option.filter (String.IsNullOrWhiteSpace >> not)
            let hash = Option.ofObj key.Hash |> Option.bind ApiKey.parseHash

            match name, hash with
            | Some name, Some hash -> Ok(name, hash)
            | None, _ -> Error $"Ankit:ApiKeys:%d{index}:Name is missing"
            | Some name, None ->
                Error
                    $"Ankit:ApiKeys:%d{index}:Hash of \"%s{name}\" must be \"sha256:\" followed by 64 hex digits, as printed by the New-Api-Key build target"

        result {
            let! keys =
                Option.ofObj settings.ApiKeys
                |> Option.defaultValue [||]
                |> List.ofArray
                |> List.mapi parse
                |> List.sequenceResultM

            match keys |> List.countBy fst |> List.tryFind (fun (_, count) -> count > 1) with
            | Some(name, _) -> return! Error $"API key name \"%s{name}\" is used more than once"
            | None when keys.IsEmpty && not settings.DisableAuth ->
                return!
                    Error
                        "No API keys are configured: create one with `./build --key-name <name> New-Api-Key` and add it to Ankit:ApiKeys, or set Ankit:DisableAuth to true"
            | None ->
                return {
                    Keys = keys
                    Disabled = settings.DisableAuth
                }
        }

    let tryFind (key: string) (store: ApiKeyStore) : string option =
        store.Keys
        |> List.filter (fun (_, hash) -> ApiKey.matches key hash)
        |> List.tryHead
        |> Option.map fst

/// Authenticates `Authorization: Bearer <key>`, the principal's name is the name of the key
type ApiKeyAuthenticationHandler
    (
        options: IOptionsMonitor<AuthenticationSchemeOptions>,
        logger: ILoggerFactory,
        encoder: UrlEncoder,
        store: ApiKeyStore
    ) =
    inherit AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)

    let ticket (name: string) =
        let identity = ClaimsIdentity([ Claim(ClaimTypes.Name, name) ], SchemeName)
        AuthenticationTicket(ClaimsPrincipal identity, SchemeName)

    member private this.BearerKey =
        let header = string this.Request.Headers.Authorization

        if header.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase) then
            Some(header.Substring(BearerPrefix.Length).Trim())
        else
            None

    override this.HandleAuthenticateAsync() =
        let result =
            if store.IsDisabled then
                AuthenticateResult.Success(ticket "anonymous")
            else
                match this.BearerKey with
                | None -> AuthenticateResult.NoResult()
                | Some key ->
                    match ApiKeyStore.tryFind key store with
                    | Some name -> AuthenticateResult.Success(ticket name)
                    | None -> AuthenticateResult.Fail "Invalid API key"

        Task.FromResult result

    override this.HandleChallengeAsync _ =
        let message =
            match this.BearerKey with
            | None -> "Missing API key: send it in the Authorization header as Bearer followed by the key"
            | Some _ -> "Invalid API key"

        this.Response.Headers.WWWAuthenticate <- "Bearer"
        Handlers.writeError this.Context Status401Unauthorized message None

let addApiKeyAuth (store: ApiKeyStore) (services: IServiceCollection) =
    services
        .AddSingleton<ApiKeyStore>(store)
        .AddAuthorization()
        .AddAuthentication(SchemeName)
        .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(SchemeName, ignore)
    |> ignore

let private policy =
    AuthorizationPolicyBuilder(SchemeName).RequireAuthenticatedUser().Build()

type ApiKeySecuritySchemeTransformer() =
    interface IOpenApiDocumentTransformer with
        member _.TransformAsync(document, _, _) =
            if isNull document.Components then
                document.Components <- OpenApiComponents()

            if isNull document.Components.SecuritySchemes then
                document.Components.SecuritySchemes <- Dictionary<string, IOpenApiSecurityScheme>()

            document.Components.SecuritySchemes[SchemeName] <-
                OpenApiSecurityScheme(
                    Type = SecuritySchemeType.Http,
                    Scheme = "bearer",
                    Description = "An API key configured in Ankit:ApiKeys"
                )

            Task.CompletedTask

let requireApiKey: Endpoint -> Endpoint =
    configureEndpoint (fun builder ->
        builder
            .RequireAuthorization(policy)
            .AddOpenApiOperationTransformer(fun operation context ct ->
                task {
                    if isNull operation.Security then
                        operation.Security <- ResizeArray()

                    let requirement = OpenApiSecurityRequirement()
                    requirement.Add(OpenApiSecuritySchemeReference(SchemeName, context.Document), ResizeArray())
                    operation.Security.Add requirement

                    let! schema = context.GetOrCreateSchemaAsync(typeof<ApiError>, null, ct)
                    let content = Dictionary<string, OpenApiMediaType>()
                    content["application/json"] <- OpenApiMediaType(Schema = schema)

                    if isNull operation.Responses then
                        operation.Responses <- OpenApiResponses()

                    operation.Responses["401"] <-
                        OpenApiResponse(Description = "Missing or invalid API key", Content = content)
                }
                :> Task))
