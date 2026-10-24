module Ankit.Server.ErrorHandler

open System
open System.Text.Json
open System.Threading.Tasks
open Ankit.Server.Dto
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Logging
open Oxpecker

open type Microsoft.AspNetCore.Http.TypedResults
open type Microsoft.AspNetCore.Http.StatusCodes

let notFoundHandler: EndpointHandler =
    fun ctx ->
        ctx.Write
        <| NotFound {|
            message = "Requested method doesn't exist"
        |}

let private (|RejectedJsonField|_|) (ex: exn) : string option =
    match ex with
    | :? JsonException as ex when ex.Message.StartsWith("Missing") ->
        Some (ex.Message.Split(' ') |> Array.last)
    | :? JsonException as ex when ex.Message.EndsWith("but was null.") ->
        let path = ex.Message.Split(' ') |> Array.head
        Some (path.Substring(path.LastIndexOf('.') + 1))
    | _ -> None

let errorHandler (ctx: HttpContext) (next: RequestDelegate) =
    task {
        let logger = ctx.GetLogger("Ankit.Server.ErrorHandler")

        try
            return! next.Invoke(ctx)
        with
        | :? OperationCanceledException when ctx.RequestAborted.IsCancellationRequested ->
            logger.LogDebug("Request was aborted by the client")
        | :? ModelBindException as ex ->
            logger.LogWarning(ex, "Reading request body for validation errors")

            match ex.InnerException with
            | RejectedJsonField field ->
                let fieldErr = {
                    field = field
                    issue = ex.InnerException.Message
                    rejectedValue = None
                }

                let code = Status422UnprocessableEntity

                let valError = {
                    ValidationError.code = code
                    message = "Validation failed"
                    traceId = ctx.TraceIdentifier
                    timestamp = DateTime.UtcNow.ToString("O")
                    path = ctx.Request.Path.Value
                    fieldErrors = [| fieldErr |]
                }

                ctx.SetStatusCode(code)
                return! json valError ctx
            | ex ->
                logger.LogWarning(ex, "Model bind error")

                let code = Status400BadRequest

                let error = {
                    code = code
                    message = "Model binding failed: " + ex.Message
                    traceId = ctx.TraceIdentifier
                    timestamp = DateTime.UtcNow.ToString("O")
                    path = ctx.Request.Path.Value
                    details = Some {| errors = ex.Message |}
                }

                ctx.SetStatusCode(code)
                return! json error ctx

        | :? RouteParseException as ex ->
            logger.LogWarning(ex, "Route parse error")

            let code = Status400BadRequest

            let error = {
                code = code
                message = "Invalid route:" + ex.Message
                traceId = ctx.TraceIdentifier
                timestamp = DateTime.UtcNow.ToString("O")
                path = ctx.Request.Path.Value
                details = None
            }

            ctx.SetStatusCode(code)
            return! json error ctx
        | ex ->
            logger.LogError(ex, "Unhandled error")

            let code = Status500InternalServerError

            let error = {
                code = code
                message = "Internal server error: an unexpected error occurred"
                traceId = ctx.TraceIdentifier
                timestamp = DateTime.UtcNow.ToString("O")
                path = ctx.Request.Path.Value
                details = None
            }

            ctx.SetStatusCode(code)
            return! json error ctx
    }
    :> Task
