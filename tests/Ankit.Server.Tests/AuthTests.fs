module Ankit.Server.Tests.AuthTests

open Ankit.Server.Auth
open Ankit.Server.Env
open Xunit

let private settings (keys: (string * string) list) (disableAuth: bool) = {
    SyncDebounceTime = 0L
    CollectionPath = ""
    ApiKeys =
        keys
        |> List.map (fun (name, hash) -> {
            Name = name
            Hash = hash
        })
        |> Array.ofList
    DisableAuth = disableAuth
}

let private load keys disableAuth =
    match ApiKeyStore.load (settings keys disableAuth) with
    | Ok store -> store
    | Error message -> failwith $"Expected the keys to load, got: %s{message}"

let private rejected keys disableAuth =
    match ApiKeyStore.load (settings keys disableAuth) with
    | Ok _ -> Assert.Fail "Expected the keys to be rejected"
    | Error _ -> ()

[<Fact>]
let ``Generated keys are random and prefixed`` () =
    let first = ApiKey.generate ()
    let second = ApiKey.generate ()

    Assert.StartsWith("ankit_", first)
    Assert.NotEqual<string>(first, second)

[<Fact>]
let ``Key is found by the hash it was configured with`` () =
    let phone = ApiKey.generate ()
    let browser = ApiKey.generate ()

    let store =
        load [ "phone", ApiKey.hash phone; "browser", ApiKey.hash browser ] false

    Assert.Equal(Some "phone", ApiKeyStore.tryFind phone store)
    Assert.Equal(Some "browser", ApiKeyStore.tryFind browser store)

[<Fact>]
let ``Unknown key is not found`` () =
    let store = load [ "phone", ApiKey.hash (ApiKey.generate ()) ] false

    Assert.Equal(None, ApiKeyStore.tryFind (ApiKey.generate ()) store)
    Assert.Equal(None, ApiKeyStore.tryFind "" store)

[<Fact>]
let ``Hash is not accepted as a key`` () =
    let hash = ApiKey.hash (ApiKey.generate ())
    let store = load [ "phone", hash ] false

    Assert.Equal(None, ApiKeyStore.tryFind hash store)

[<Fact>]
let ``Hash in upper case hex is accepted`` () =
    let key = ApiKey.generate ()

    let hash =
        "sha256:" + (ApiKey.hash key).Substring("sha256:".Length).ToUpperInvariant()

    Assert.Equal(Some "phone", ApiKeyStore.tryFind key (load [ "phone", hash ] false))

[<Fact>]
let ``No keys are rejected unless auth is disabled`` () =
    rejected [] false
    Assert.True((load [] true).IsDisabled)

[<Fact>]
let ``Malformed hashes are rejected`` () =
    let hex = (ApiKey.hash (ApiKey.generate ())).Substring("sha256:".Length)

    rejected [ "phone", hex ] false
    rejected [ "phone", "sha256:" + hex.Substring 2 ] false
    rejected [ "phone", "sha256:" + String.replicate 64 "z" ] false
    rejected [ "phone", null ] false

[<Fact>]
let ``Keys without a name or with the same name are rejected`` () =
    let hash = ApiKey.hash (ApiKey.generate ())

    rejected [ " ", hash ] false
    rejected [ null, hash ] false
    rejected [ "phone", hash; "phone", ApiKey.hash (ApiKey.generate ()) ] false
