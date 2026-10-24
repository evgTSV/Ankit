module Ankit.Server.Services

open Anki.Backend
open Anki.Sync
open Ankit.Native
open Ankit.Server.Env
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Options
open FsToolkit.ErrorHandling

open type SyncCollectionResponse.Types
open type SyncStatusResponse.Types

[<RequireQualifiedAccess>]
type SyncFailed =
    | InvalidCredentials
    | Aborted
    | NetworkError of message: string
    /// The server refused the sync and explained why, e.g. the account is locked
    | ServerRejected of message: string
    /// The collections diverged and one side has to overwrite the other, which needs a human decision
    | FullSyncRequired of SyncCollectionResponse.Types.ChangesRequired
    | BackendFailed of kind: BackendError.Types.Kind * message: string
    | NativeFailed of message: string

type SyncResult<'a> = Result<'a, SyncFailed>
type BackendErrorKind = BackendError.Types.Kind

[<Interface>]
type ISyncService =
    abstract TrySync: unit -> SyncResult<unit>

type AnkiSyncService(backend: RslibBackend, syncServerSettings: IOptionsMonitor<SyncServerSettings>, logger: ILogger<AnkiSyncService>) =

    let classify (error: BackendError) : SyncFailed =
        match error.Kind with
        | BackendErrorKind.SyncAuthError -> SyncFailed.InvalidCredentials
        | BackendErrorKind.Interrupted -> SyncFailed.Aborted
        | BackendErrorKind.NetworkError -> SyncFailed.NetworkError error.Message
        | BackendErrorKind.SyncServerMessage -> SyncFailed.ServerRejected error.Message
        | kind -> SyncFailed.BackendFailed(kind, error.Message)

    let call (f: unit -> 'a) : SyncResult<'a> =
        try
            Ok <| f()
        with
        | :? BackendException as e -> Error <| classify e.Error
        | :? RslibException as e -> Error <| SyncFailed.NativeFailed e.Message

    /// The server may redirect us to another shard, subsequent requests have to go there
    let followRedirect (auth: SyncAuth) (newEndpoint: string option) : SyncAuth =
        match newEndpoint with
        | Some endpoint when endpoint <> auth.Endpoint ->
            logger.LogInformation("Sync server moved the collection to {Endpoint}", endpoint)
            let redirected = auth.Clone()
            redirected.Endpoint <- endpoint
            redirected
        | _ -> auth

    let getAuth() : SyncResult<SyncAuth> =
        let loginRequest = SyncLoginRequest(
            Username = syncServerSettings.CurrentValue.Username,
            Password = syncServerSettings.CurrentValue.Password,
            Endpoint = syncServerSettings.CurrentValue.Endpoint)
        call (fun () -> backend.SyncLogin loginRequest)

    let syncAll (auth: SyncAuth) : SyncResult<SyncAuth> =
        result {
            let request = SyncCollectionRequest(Auth = auth, SyncMedia = true)
            let! response = call (fun () -> backend.SyncCollection request)

            if not (System.String.IsNullOrEmpty response.ServerMessage) then
                logger.LogWarning("Sync server says: {ServerMessage}", response.ServerMessage)

            let auth = followRedirect auth (if response.HasNewEndpoint then Some response.NewEndpoint else None)

            match response.Required with
            | ChangesRequired.NoChanges
            | ChangesRequired.NormalSync -> return auth
            | required -> return! Error <| SyncFailed.FullSyncRequired required
        }

    let getSyncStatus (auth: SyncAuth) : SyncResult<SyncStatusResponse> =
        call (fun () -> backend.SyncStatus auth)

    let logSyncStatus (status: SyncStatusResponse) =
        match status.Required with
        | Required.NoChanges ->
            logger.LogInformation("Sync finished, collection is up to date")
        | required ->
            logger.LogWarning("Sync finished, but the collection still requires {Required}", required)

    let logFailure (failure: SyncFailed) =
        match failure with
        | SyncFailed.Aborted -> logger.LogInformation("Sync was aborted")
        | SyncFailed.NetworkError message -> logger.LogWarning("Sync failed, sync server is unreachable: {Message}", message)
        | failure -> logger.LogError("Sync failed: {Failure}", failure)

    interface ISyncService with
        member this.TrySync() =
            result {
                let! auth = getAuth()
                let! auth = syncAll auth
                let! status = getSyncStatus auth
                logSyncStatus status
            }
            |> Result.teeError logFailure
