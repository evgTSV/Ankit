module Ankit.Server.Tests.AnkiSyncServer

open System
open System.Diagnostics
open System.IO
open System.Text.RegularExpressions
open System.Threading.Tasks
open Ankit.Server.Env
open DotNet.Testcontainers.Builders
open DotNet.Testcontainers.Containers
open Xunit

let private repoRoot =
    let rec find (dir: DirectoryInfo) =
        if isNull dir then failwith "paket.dependencies not found above the test output directory"
        elif File.Exists(Path.Combine(dir.FullName, "paket.dependencies")) then dir.FullName
        else find dir.Parent
    find (DirectoryInfo AppContext.BaseDirectory)

/// The sync server is built from the same Anki tag as the rslib we link against, so the protocol versions match
let private ankiVersion =
    let dependencies = File.ReadAllText(Path.Combine(repoRoot, "paket.dependencies"))
    Regex.Match(dependencies, @"github ankitects/anki:(\S+)").Groups[1].Value

let private linuxDockerAvailable =
    lazy
        try
            use docker = Process.Start(ProcessStartInfo("docker", "info --format {{.OSType}}", RedirectStandardOutput = true, RedirectStandardError = true))
            let osType = docker.StandardOutput.ReadToEnd().Trim()
            docker.WaitForExit(10_000) && docker.ExitCode = 0 && osType = "linux"
        with _ ->
            false

type LinuxDockerFactAttribute() as this =
    inherit FactAttribute()
    do
        if not linuxDockerAvailable.Value then
            this.Skip <- "Needs Docker with Linux containers"

/// Parallel rustc jobs for compiling the sync server, more jobs need more memory in the Docker VM
let private cargoBuildJobs = Environment.GetEnvironmentVariable "ANKIT_TESTS_CARGO_JOBS" |> Option.ofObj |> Option.defaultValue "4"

[<Literal>]
let private SyncPort = 8080

[<Literal>]
let Password = "password"

/// Accounts of the sync server, give every test its own so they don't see each other's collections
let Users = [ for i in 1..5 -> $"user{i}" ]

type AnkiSyncServer() =
    let mutable container: IContainer = null

    member _.Settings(username: string) : SyncServerSettings = {
        Host = container.Hostname
        Port = int (container.GetMappedPublicPort SyncPort)
        UseHttps = false
        Username = username
        Password = Password
    }

    interface IAsyncLifetime with
        member _.InitializeAsync() =
            task {
                if linuxDockerAvailable.Value then
                    // Compiling the server takes several minutes the first time, Docker caches the layers after that
                    let image =
                        ImageFromDockerfileBuilder()
                            .WithDockerfileDirectory(Path.Combine(repoRoot, "tests", "Ankit.Server.Tests", "SyncServer"))
                            .WithDockerfile("Dockerfile")
                            .WithBuildArgument("ANKI_VERSION", ankiVersion)
                            .WithBuildArgument("CARGO_BUILD_JOBS", cargoBuildJobs)
                            .WithName($"ankit-tests/anki-sync-server:{ankiVersion}")
                            .WithCleanUp(false)
                            .Build()
                    do! image.CreateAsync()

                    let builder =
                        Users
                        |> List.indexed
                        |> List.fold
                            (fun (b: ContainerBuilder) (i, user) -> b.WithEnvironment($"SYNC_USER{i + 1}", $"{user}:{Password}"))
                            (ContainerBuilder(image.FullName))
                    container <-
                        builder
                            .WithPortBinding(SyncPort, true)
                            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(fun r -> r.ForPort(uint16 SyncPort).ForPath("/health")))
                            .Build()
                    do! container.StartAsync()
            }

        member _.DisposeAsync() =
            if isNull container then Task.CompletedTask else container.DisposeAsync().AsTask()
