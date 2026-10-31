module Ankit.Server.Tests.ApiKeyEndpointTests

open System
open System.Net
open System.Net.Http
open System.Net.Http.Headers
open System.Text.Json
open System.Threading.Tasks
open Ankit.Server.Auth
open Ankit.Server.Env
open Ankit.Server.Utils
open Microsoft.AspNetCore.Builder
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging
open Oxpecker
open Xunit

let private phoneKey = ApiKey.generate ()

let private store (disableAuth: bool) =
    let settings = {
        SyncDebounceTime = 0L
        CollectionPath = ""
        ApiKeys = [|
            {
                Name = "phone"
                Hash = ApiKey.hash phoneKey
            }
        |]
        DisableAuth = disableAuth
    }

    match ApiKeyStore.load settings with
    | Ok store -> store
    | Error message -> failwith message

let private userName: EndpointHandler =
    fun ctx -> ctx.WriteText ctx.User.Identity.Name

let private endpoints = [
    GET [ route "/public" (text "public") ]
    GET [ route "/protected" userName ] |> requireApiKey
    POST [ route "/protected" userName; route "/protected/other" userName ]
    |> requireApiKey
]

/// Runs the auth setup of the server on Kestrel at a free port with stub endpoints
let private withServer (disableAuth: bool) (test: HttpClient -> Task) : Task =
    task {
        let builder = WebApplication.CreateSlimBuilder()
        builder.Logging.ClearProviders() |> ignore

        builder.Services.AddRouting().AddOxpecker().AddSingleton<IJsonSerializer>(SystemTextJsonSerializer(jsonOptions))
        |> addApiKeyAuth (store disableAuth)

        let app = builder.Build()
        app.Urls.Clear()
        app.Urls.Add "http://127.0.0.1:0"

        app.UseRouting().UseAuthentication().UseAuthorization().UseOxpecker(endpoints)
        |> ignore

        do! app.StartAsync()

        try
            use client = new HttpClient(BaseAddress = Uri(Seq.head app.Urls))
            do! test client
        finally
            app.StopAsync().Wait()
            (app :> IDisposable).Dispose()
    }

let private send (client: HttpClient) (method: HttpMethod) (path: string) (authorization: string option) =
    task {
        use request = new HttpRequestMessage(method, path)

        authorization
        |> Option.iter (fun value -> request.Headers.TryAddWithoutValidation("Authorization", value) |> ignore)

        let! response = client.SendAsync request
        let! body = response.Content.ReadAsStringAsync()
        return response, body
    }

let private assertUnauthorized (response: HttpResponseMessage, body: string) (message: string) =
    Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode)
    Assert.Contains(AuthenticationHeaderValue "Bearer", response.Headers.WwwAuthenticate)

    use error = JsonDocument.Parse body
    Assert.Equal(401, error.RootElement.GetProperty("code").GetInt32())
    Assert.StartsWith(message, error.RootElement.GetProperty("message").GetString())

[<Fact>]
let ``Public endpoint answers without an API key`` () : Task =
    withServer false (fun client ->
        task {
            let! response, body = send client HttpMethod.Get "/public" None
            Assert.Equal(HttpStatusCode.OK, response.StatusCode)
            Assert.Equal("public", body)
        })

[<Fact>]
let ``Public endpoint ignores an invalid API key`` () : Task =
    withServer false (fun client ->
        task {
            let! response, _ = send client HttpMethod.Get "/public" (Some "Bearer ankit_wrong")
            Assert.Equal(HttpStatusCode.OK, response.StatusCode)
        })

[<Fact>]
let ``Protected endpoint without an API key answers 401`` () : Task =
    withServer false (fun client ->
        task {
            let! result = send client HttpMethod.Get "/protected" None
            assertUnauthorized result "Missing API key"
        })

[<Fact>]
let ``Protected endpoint with another authorization scheme answers 401`` () : Task =
    withServer false (fun client ->
        task {
            let! result = send client HttpMethod.Get "/protected" (Some $"Basic %s{phoneKey}")
            assertUnauthorized result "Missing API key"
        })

[<Fact>]
let ``Protected endpoint with an unknown API key answers 401`` () : Task =
    withServer false (fun client ->
        task {
            let! result =
                send client HttpMethod.Get "/protected" (Some $"Bearer %s{ApiKey.generate ()}")

            assertUnauthorized result "Invalid API key"

            let! result =
                send client HttpMethod.Get "/protected" (Some $"Bearer %s{ApiKey.hash phoneKey}")

            assertUnauthorized result "Invalid API key"
        })

[<Fact>]
let ``Protected endpoint with a configured API key answers as that client`` () : Task =
    withServer false (fun client ->
        task {
            let! response, body =
                send client HttpMethod.Get "/protected" (Some $"Bearer %s{phoneKey}")

            Assert.Equal(HttpStatusCode.OK, response.StatusCode)
            Assert.Equal("phone", body)

            let! response, _ =
                send client HttpMethod.Get "/protected" (Some $"bearer %s{phoneKey}")

            Assert.Equal(HttpStatusCode.OK, response.StatusCode)
        })

[<Fact>]
let ``Every endpoint of a protected group requires the API key`` () : Task =
    withServer false (fun client ->
        task {
            for path in [ "/protected"; "/protected/other" ] do
                let! result = send client HttpMethod.Post path None
                assertUnauthorized result "Missing API key"

                let! response, _ = send client HttpMethod.Post path (Some $"Bearer %s{phoneKey}")
                Assert.Equal(HttpStatusCode.OK, response.StatusCode)
        })

[<Fact>]
let ``Protected endpoint answers without an API key when auth is disabled`` () : Task =
    withServer true (fun client ->
        task {
            let! response, body = send client HttpMethod.Get "/protected" None
            Assert.Equal(HttpStatusCode.OK, response.StatusCode)
            Assert.Equal("anonymous", body)
        })
