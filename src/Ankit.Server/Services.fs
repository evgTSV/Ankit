module Ankit.Server.Services

open Anki.Backend
open Anki.Sync
open Ankit.Native
open Ankit.Server.Dto
open Ankit.Server.Env
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Options
open FsToolkit.ErrorHandling

open type SyncCollectionResponse.Types
open type SyncStatusResponse.Types

[<RequireQualifiedAccess>]
type InfraFail =
    | BackendFailed of kind: BackendError.Types.Kind * message: string
    | NativeFailed of message: string

[<RequireQualifiedAccess>]
type SyncFailed =
    | AuthFailed of message: string
    | Aborted of message: string
    | NetworkError of message: string
    /// The server refused the sync and explained why, e.g. the account is locked
    | ServerRejected of message: string
    /// The collections diverged and one side has to overwrite the other, which needs a human decision
    | FullSyncRequired of SyncCollectionResponse.Types.ChangesRequired
    | InfraFail of InfraFail

type SyncResult<'a> = Result<'a, SyncFailed>
type BackendErrorKind = BackendError.Types.Kind

[<RequireQualifiedAccess>]
module SyncFailed =
    let classify (kind: BackendErrorKind, msg: string) : SyncFailed =
        match kind with
        | BackendErrorKind.SyncAuthError -> SyncFailed.AuthFailed msg
        | BackendErrorKind.Interrupted -> SyncFailed.Aborted msg
        | BackendErrorKind.NetworkError -> SyncFailed.NetworkError msg
        | BackendErrorKind.SyncServerMessage -> SyncFailed.ServerRejected msg
        | kind -> SyncFailed.InfraFail(InfraFail.BackendFailed(kind, msg))

    let ofInfra (error: InfraFail) : SyncFailed =
        match error with
        | InfraFail.BackendFailed(k, m) -> classify(k, m)
        | e -> SyncFailed.InfraFail e

module ServiceUtils =

    /// Converts expected backend/native exceptions into `Error`. Everything else
    /// (i.e. genuine bugs) escapes via `reraise`. Callers at the HTTP/CLI boundary
    /// must have an outer exception handler.
    let guardCall (f: unit -> 'a) : Result<'a, InfraFail> =
        try
            Ok <| f ()
        with
        | :? BackendException as e ->
            Error <| InfraFail.BackendFailed(e.Error.Kind, e.Error.Message)
        | :? RslibException as e -> Error <| InfraFail.NativeFailed e.Message
        | _ -> reraise()

    let guardCallSync (f: unit -> 'a) : SyncResult<'a> =
        guardCall f |> Result.mapError SyncFailed.ofInfra

open ServiceUtils

[<Interface>]
type IAuthService =
    abstract GetAuth: unit -> SyncResult<SyncAuth>

type AnkiAuthService(backend: RslibBackend, syncServerSettings: IOptionsMonitor<SyncServerSettings>) =

    let getAuth () : SyncResult<SyncAuth> =
        let loginRequest =
            SyncLoginRequest(
                Username = syncServerSettings.CurrentValue.Username,
                Password = syncServerSettings.CurrentValue.Password,
                Endpoint = syncServerSettings.CurrentValue.Endpoint
            )

        guardCallSync (fun() -> backend.SyncLogin loginRequest)

    interface IAuthService with
        member this.GetAuth() : SyncResult<SyncAuth> = getAuth ()

[<Interface>]
type ISyncService =
    abstract TrySync: unit -> SyncResult<unit>
    abstract FullSync: SyncDirection -> SyncResult<unit>

type AnkiSyncService
    (backend: RslibBackend, authService: IAuthService, logger: ILogger<AnkiSyncService>) =

    /// The server may redirect us to another shard, subsequent requests have to go there
    let followRedirect (auth: SyncAuth) (newEndpoint: string option) : SyncAuth =
        match newEndpoint with
        | Some endpoint when endpoint <> auth.Endpoint ->
            logger.LogInformation("Sync server moved the collection to {Endpoint}", endpoint)
            let redirected = auth.Clone()
            redirected.Endpoint <- endpoint
            redirected
        | _ -> auth

    let syncAll (auth: SyncAuth) : SyncResult<SyncAuth> =
        result {
            let request = SyncCollectionRequest(Auth = auth, SyncMedia = true)
            let! response =
                guardCall (fun () -> backend.SyncCollection request)
                |> Result.mapError SyncFailed.ofInfra

            if not (System.String.IsNullOrEmpty response.ServerMessage) then
                logger.LogWarning("Sync server says: {ServerMessage}", response.ServerMessage)

            let auth =
                followRedirect
                    auth
                    (if response.HasNewEndpoint then
                         Some response.NewEndpoint
                     else
                         None)

            match response.Required with
            | ChangesRequired.NoChanges
            | ChangesRequired.NormalSync -> return auth
            | required -> return! Error <| SyncFailed.FullSyncRequired required
        }

    let fullSync (auth: SyncAuth) (direction: SyncDirection) : SyncResult<unit> =
        result {
            let request =
                FullUploadOrDownloadRequest(Auth = auth, Upload = (direction = Upload))

            do! guardCallSync (fun () -> backend.FullUploadOrDownload request)
            // rslib skips media without the server's media usn, so it's synced on its own
            do! guardCallSync (fun () -> backend.SyncMedia auth)
        }

    let getSyncStatus (auth: SyncAuth) : SyncResult<SyncStatusResponse> =
        guardCallSync (fun () -> backend.SyncStatus auth)
    let logSyncStatus (status: SyncStatusResponse) =
        match status.Required with
        | Required.NoChanges -> logger.LogInformation("Sync finished, collection is up to date")
        | required -> logger.LogWarning("Sync finished, but the collection still requires {Required}", required)

    let logFailure (failure: SyncFailed) =
        match failure with
        | SyncFailed.Aborted message ->
            logger.LogInformation("Sync was aborted: {Message}", message)
        | SyncFailed.NetworkError message ->
            logger.LogWarning("Sync failed, sync server is unreachable: {Message}", message)
        | SyncFailed.AuthFailed reason ->
            logger.LogWarning("Sync failed, authentication rejected: {Reason}", reason)
        | SyncFailed.ServerRejected reason ->
            logger.LogWarning("Sync failed, server rejected the request: {Reason}", reason)
        | SyncFailed.FullSyncRequired required ->
            logger.LogInformation("Sync stopped, full sync required: {Required}", required)
        | SyncFailed.InfraFail(InfraFail.BackendFailed(kind, message)) ->
            logger.LogError("Sync failed, backend error {Kind}: {Message}", kind, message)
        | SyncFailed.InfraFail(InfraFail.NativeFailed message) ->
            logger.LogError("Sync failed, native error: {Message}", message)

    interface ISyncService with
        member this.TrySync() =
            result {
                let! auth = authService.GetAuth()
                let! auth = syncAll auth
                let! status = getSyncStatus auth
                logSyncStatus status
            }
            |> Result.teeError logFailure

        member this.FullSync(direction) =
            result {
                let! auth = authService.GetAuth()
                logger.LogWarning("Starting full {Direction}, the other side's collection is replaced", direction)
                do! fullSync auth direction
                let! status = getSyncStatus auth
                logSyncStatus status
            }
            |> Result.teeError logFailure

type NoteService(logger: ILogger<NoteService>) =
    member _.AddNote() = ()
