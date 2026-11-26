namespace Ankit.Server.Tests

open System
open Ankit.Server.Env
open Ankit.Server.Services
open Microsoft.Extensions.Options
open Xunit

type AuthServiceTests() =
    let collection = new TempCollection()

    let auth (settings: SyncServerSettings) : IAuthService =
        AnkiAuthService(
            collection.Backend,
            {
                new IOptionsMonitor<SyncServerSettings> with
                    member _.CurrentValue = settings
                    member _.Get _ = settings
                    member _.OnChange _ = null
            }
        )

    let configured = {
        Host = "localhost"
        Port = 8080
        UseHttps = false
        Username = "user"
        Password = "pass"
    }

    [<Fact>]
    member _.``Missing credentials are reported without logging in``() =
        match
            (auth {
                configured with
                    Username = null
                    Password = ""
            })
                .GetAuth()
        with
        | Error(SyncFailed.NotConfigured message) ->
            Assert.Equal("SyncServer:Username, SyncServer:Password not set", message)
        | other -> Assert.Fail $"Expected NotConfigured, got %A{other}"

    [<Fact>]
    member _.``Missing host is reported without logging in``() =
        match (auth { configured with Host = null }).GetAuth() with
        | Error(SyncFailed.NotConfigured message) -> Assert.Equal("SyncServer:Host not set", message)
        | other -> Assert.Fail $"Expected NotConfigured, got %A{other}"

    interface IDisposable with
        member _.Dispose() = (collection :> IDisposable).Dispose()
