/// OpenAPI descriptions of the endpoints, served at /openapi/v1.json
module Ankit.Server.ApiDocs

open System
open System.Text.Json.Nodes
open System.Threading.Tasks
open Ankit.Server.Dto
open Ankit.Server.Utils
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.OpenApi
open Microsoft.FSharp.Reflection
open Microsoft.OpenApi
open Oxpecker
open Oxpecker.OpenApi

open type Microsoft.AspNetCore.Http.StatusCodes

/// Unions without fields are written as their case name (see Utils.jsonOptions), the generator can't see that
type FieldlessUnionSchemaTransformer() =
    interface IOpenApiSchemaTransformer with
        member _.TransformAsync(schema, context, _) =
            let ty = context.JsonTypeInfo.Type

            if FSharpType.IsUnion ty then
                let cases = FSharpType.GetUnionCases ty

                if cases |> Array.forall (fun case -> case.GetFields().Length = 0) then
                    schema.Type <- JsonSchemaType.String
                    schema.Description <- "Case-insensitive"

                    schema.Enum <-
                        ResizeArray<JsonNode>(
                            cases
                            |> Seq.map (fun case -> JsonValue.Create(unionTagNaming.ConvertName case.Name) :> JsonNode)
                        )

            Task.CompletedTask

/// F# record fields are only null when they're options, the generator assumes any reference type may be
type RecordFieldNullabilitySchemaTransformer() =
    interface IOpenApiSchemaTransformer with
        member _.TransformAsync(schema, context, _) =
            match context.JsonPropertyInfo with
            | null -> ()
            | property ->
                let isOption =
                    property.PropertyType.IsGenericType
                    && property.PropertyType.GetGenericTypeDefinition() = typedefof<option<_>>

                if
                    FSharpType.IsRecord(property.DeclaringType, true)
                    && not isOption
                    && schema.Type.HasValue
                then
                    schema.Type <- Nullable(schema.Type.Value &&& ~~~JsonSchemaType.Null)

            Task.CompletedTask

let ok<'T> = ResponseBody(typeof<'T>, statusCode = Status200OK)
let okText = ResponseBody(typeof<string>, [| "text/plain" |], Status200OK)
let created = ResponseBody(typeof<CreatedDto>, statusCode = Status201Created)

/// Marker for a response without a body, `describe` adds it to the operation itself
let noContent = ResponseBody(typeof<unit>, statusCode = Status204NoContent)

let notFound = ResponseBody(typeof<ApiError>, statusCode = Status404NotFound)

/// Failed validation (with fieldErrors) or a command rslib rejected
let invalid =
    ResponseBody(typeof<ValidationError>, statusCode = Status422UnprocessableEntity)

let syncFailed = ResponseBody(typeof<ApiError>, statusCode = Status502BadGateway)

let shuttingDown =
    ResponseBody(typeof<ApiError>, statusCode = Status503ServiceUnavailable)

let describe (operationId: string) (summary: string) (request: Type option) (responses: ResponseBody list) =
    // A body type is required by ResponseBody, but 204 has no body
    let hasNoContent =
        responses |> List.exists (fun r -> obj.ReferenceEquals(r, noContent))

    let responses =
        responses |> List.filter (fun r -> not (obj.ReferenceEquals(r, noContent)))

    addOpenApi (
        OpenApiConfig(
            ?requestBody = (request |> Option.map (fun t -> RequestBody(t))),
            responseBodies = responses,
            configureOperation =
                fun operation _ _ ->
                    operation.OperationId <- operationId
                    operation.Summary <- summary

                    if hasNoContent then
                        operation.Responses.Add("204", OpenApiResponse(Description = "No Content"))

                    Task.CompletedTask
        )
    )

/// Documents a query string parameter, which Oxpecker can't infer from the handler
let withQuery (name: string) (description: string) =
    configureEndpoint (fun builder ->
        builder.AddOpenApiOperationTransformer(fun operation _ _ ->
            if isNull operation.Parameters then
                operation.Parameters <- ResizeArray()

            operation.Parameters.Add(
                OpenApiParameter(
                    Name = name,
                    In = ParameterLocation.Query,
                    Required = true,
                    Description = description,
                    Schema = OpenApiSchema(Type = JsonSchemaType.String)
                )
            )

            Task.CompletedTask))
