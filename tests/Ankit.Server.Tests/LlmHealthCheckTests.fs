module Ankit.Server.Tests.LlmHealthCheckTests

open System
open System.Net
open System.Net.Sockets
open System.Threading
open System.Threading.Tasks
open Ankit.Server.Env
open Ankit.Server.Llm
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Diagnostics.HealthChecks
open Microsoft.Extensions.Logging
open Xunit

let private settings provider = {
    Provider = provider
    ApiKey = "key"
    Model = "test-model"
    ResponseFormat = LlmResponseFormat.JsonSchema
    TimeoutSeconds = 5
    Module = ""
    Instructions = [||]
}

let private check (settings: LlmSettings) =
    match LlmClient.create settings with
    | Ok llm -> (LlmHealthCheck llm :> IHealthCheck).CheckHealthAsync(HealthCheckContext(), CancellationToken.None)
    | Error message -> failwith message

/// An OpenAI-compatible server on Kestrel at a free port that answers every request with the given status and body
let private withProvider (status: int) (body: string) (test: string -> Task) : Task =
    task {
        let builder = WebApplication.CreateSlimBuilder()
        builder.Logging.ClearProviders() |> ignore
        let app = builder.Build()
        app.Urls.Clear()
        app.Urls.Add "http://127.0.0.1:0"

        app.Run(fun ctx ->
            ctx.Response.StatusCode <- status
            ctx.Response.ContentType <- "application/json"
            ctx.Response.WriteAsync body)

        do! app.StartAsync()

        try
            do! test $"%s{Seq.head app.Urls}/v1"
        finally
            app.StopAsync().Wait()
    }

let private closedPort () =
    let listener = new TcpListener(IPAddress.Loopback, 0)
    listener.Start()
    let port = (listener.LocalEndpoint :?> IPEndPoint).Port
    listener.Stop()
    port

[<Fact>]
let ``Health is fine while suggestions are off`` () : Task =
    task {
        let! result = check { settings "" with Model = "" }
        Assert.Equal(HealthStatus.Healthy, result.Status)
    }

[<Fact>]
let ``Provider that lists its models is healthy`` () : Task =
    withProvider
        200
        """{"object": "list", "data": [{"id": "test-model", "object": "model", "created": 0, "owned_by": "me"}]}"""
        (fun url ->
            task {
                let! result = check (settings url)
                Assert.Equal(HealthStatus.Healthy, result.Status)
            })

[<Fact>]
let ``Provider that refuses the key is unhealthy`` () : Task =
    withProvider 401 """{"error": {"message": "Invalid API key"}}""" (fun url ->
        task {
            let! result = check (settings url)
            Assert.Equal(HealthStatus.Unhealthy, result.Status)
            Assert.Contains("401", result.Description)
        })

[<Fact>]
let ``Unreachable provider is unhealthy`` () : Task =
    task {
        let! result = check (settings $"http://127.0.0.1:%d{closedPort ()}/v1")
        Assert.Equal(HealthStatus.Unhealthy, result.Status)
        Assert.Contains("unreachable", result.Description)
    }
