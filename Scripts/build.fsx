#r "nuget: Fake.Core.Trace, 6.2.0"
#r "nuget: Fake.Core.Target, 6.2.0"
#r "nuget: Fake.Core.CommandLineParsing, 6.2.0"
#r "nuget: Fake.IO.FileSystem, 6.2.0"
#r "nuget: Fake.DotNet.Cli, 6.2.0"
#r "nuget: Fake.Tools.Git, 6.2.0"
#r "nuget: FsToolkit.ErrorHandling, 5.2.0"

open System
open System.IO
open System.Runtime.InteropServices
open System.Text
open System.Text.Unicode
open Fake.Core
open Fake.Core.TargetOperators
open Fake.DotNet
open Fake.Tools.Git
open Fake.IO
open Fake.IO.Globbing.Operators
open FsToolkit.ErrorHandling

let project = "Ankit"
let description = "A F# service for managing Anki decks with lightweight binding to the Anki rslib"
let summary = "A F# service for managing Anki decks with lightweight binding to the Anki rslib"
let license = "AGPL-3.0-or-later"
let authors = [ "EvgTsv" ]
let copyright = "Copyright (C) 2026 Evgenii Tsvetkov"
let version = Version(1, 0, 0, 0)

let isCI = Environment.GetEnvironmentVariable("CI") <> null

let root = Path.GetFullPath(__SOURCE_DIRECTORY__ + "/..")
let solution = Path.combine root "Ankit.sln"
let rslibBindings = Path.combine root "src/rslib-bindings"
let ankiSources = Path.combine root "paket-files/anki/ankitects/anki"
let scripts = __SOURCE_DIRECTORY__

let usage =
    """
Usage:
  build.fsx [options] [<target>]
  build.fsx --list
  build.fsx -h | --help

Options:
  -c, --configuration <name>  Build configuration: Debug or Release (default: Release).
  -p, --parallel <jobs>       Number of targets to run in parallel (default: 1).
  -f, --full-clean            Perform removing files left after build process.
  -h, --help                  Show this help.
  --list                      List available targets.
  --no-logo                   Do not display the logo.
"""

let args =
    fsi.CommandLineArgs
    |> Array.skip 1 // first arg is the script path
    |> Array.toList

args
|> Context.FakeExecutionContext.Create false __SOURCE_FILE__
|> Context.RuntimeContext.Fake
|> Context.setExecutionContext

let cli =
    try
        Docopt(usage).Parse(args)
    with :? DocoptException as ex ->
        eprintfn $"%s{ex.Message}"
        eprintfn $"%s{usage}"
        exit 1

if DocoptResult.hasFlag "--help" cli then
    printfn $"%s{usage}"
    exit 0

let configuration =
    DocoptResult.tryGetArgument "--configuration" cli
    |> Option.map DotNet.BuildConfiguration.fromString
    |> Option.defaultValue DotNet.BuildConfiguration.Release

let parallelJobs =
    DocoptResult.tryGetArgument "--parallel" cli
    |> Option.map int
    |> Option.defaultValue 1

let targetName =
    DocoptResult.tryGetArgument "<target>" cli
    |> Option.defaultValue "All"

// Docopt does not reject unknown options, they end up as the positional target
if targetName.StartsWith "-" then
    eprintfn $"Unknown or incomplete option: %s{targetName}"
    eprintfn $"%s{usage}"
    exit 1

if not(DocoptResult.hasFlag "--no-logo" cli) then
    Trace.log $"%s{project} v{version}"

let traceErrors (errors: string list) =
    errors |> List.iter Trace.traceError

let handleResult (target: string) (result: Result<unit, string list>) =
    match result with
    | Ok () ->
        Trace.trace $"%s{target} succeeded"
    | Error errors ->
        traceErrors errors
        failwithf $"%s{target} failed with %d{List.length errors} error(s)"

let doNothing = ignore

let fromProcResult (procResult: ProcessResult) : Result<unit, string list> =
    if procResult.OK then Ok() else Error procResult.Errors

let dotnetCli : DotNet.Options -> DotNet.Options = id

// Fake's binlog reader does not support the binlog format of current MSBuild versions
let noBinLog (p: MSBuild.CliArguments) = { p with DisableInternalBinLog = true }

// Project metadata above is the single source for the assemblies and packages of every project
let metadata (p: MSBuild.CliArguments) =
    { noBinLog p with
        Properties =
            p.Properties
            @ [ "Product", project
                "Description", description
                "Version", string version
                "Authors", String.concat "," authors
                "Copyright", copyright
                "PackageLicenseExpression", license ] }

let dotnet command args =
    DotNet.exec dotnetCli command args |> fromProcResult

let repoFiles() =
    let success, files, error =
        CommandHelper.runGitCommand root "ls-files --cached --others --exclude-standard"

    if not success then
        Error [error] else

    files
    |> List.map (fun f -> Path.Combine(root, f))
    |> List.filter File.Exists
    |> Ok

let relative (path: string) = Path.GetRelativePath(root, path)

Target.create "Info" (fun _ ->
    Trace.log $"Project: %s{project} v{version}"
    Trace.log $"Description: %s{description}"
    Trace.log $"Summary: %s{summary}"
    Trace.log $"License: %s{license}"
    Trace.log $"Authors: %A{authors}"
    Trace.log $"Copyright: %s{copyright}"
    Trace.log $"Root directory: %s{root}"
    Trace.log $"Solution file: %s{solution}"
    Trace.log $"Configuration: %A{configuration}")

Target.create "Restore" (fun _ ->
    result {
        do! dotnet "tool" "restore"
        do! dotnet "paket" "restore"
        DotNet.restore
            (fun p ->
                { p with
                    Common = dotnetCli p.Common
                    MSBuildParams = noBinLog p.MSBuildParams })
            solution
    } |> handleResult "Restore")

Target.create "Format" (fun _ ->
    dotnet "fantomas" "." |> handleResult "Format")

let utf8Bom = Encoding.UTF8.GetPreamble()

let hasBom (bytes: ReadOnlySpan<byte>) =
    bytes.StartsWith(ReadOnlySpan(utf8Bom))

let isValidUtf8 (bytes: ReadOnlySpan<byte>) =
    Utf8.IsValid(bytes)

let verifyEncoding() =
    result {
        let! files = repoFiles()
        let problems =
            files
            |> List.choose (fun f ->
                let bytes = ReadOnlySpan(File.ReadAllBytes(f))
                if hasBom bytes then Some($"%s{relative f} has UTF-8 BOM")
                elif not (isValidUtf8 bytes) then Some($"%s{relative f} is not valid UTF-8")
                else None)
        if problems.Length > 0 then
            return! Error problems else return ()
    }

Target.create "Verify-Encoding" (fun _ ->
    let result = verifyEncoding()

    match result with
    | Ok () -> ()
    | Error errors ->
        traceErrors errors
        failwithf $"%d{errors.Length} file(s) are not UTF-8 without BOM, please run Remove-BOM or Prepare-Sources target")

let removeBom (file: string) =
    try
        let bytes = ReadOnlySpan(File.ReadAllBytes(file))
        let hasBom = hasBom bytes
        if hasBom then
            let newBytes = bytes.Slice(utf8Bom.Length)
            File.WriteAllBytes(file, newBytes)
        Ok hasBom
    with ex ->
        Error $"%s{relative file}: %s{ex.Message}"

Target.create "Remove-BOM" (fun _ ->
    result {
        let! files = repoFiles()
        let errors =
            files
            |> List.fold (fun acc f ->
                match removeBom f with
                | Ok hasBom ->
                    if hasBom then
                        Trace.log $"Removed BOM from %s{relative f}"
                    acc
                | Error e -> e :: acc) []
        if errors.Length > 0 then
            return! Error errors else return ()
    } |> handleResult "Remove-BOM")

let genWorkflowScript = Path.combine scripts "gen-workflow.fsx"

Target.create "Verify-Workflow" (fun _ ->
    dotnet "fsi" $"{genWorkflowScript} verify"
    |> handleResult "VerifyWorkflow")

Target.create "Gen-Workflow" (fun _ ->
    dotnet "fsi" $"{genWorkflowScript}"
    |> handleResult "GenWorkflow")

let isFullCleanTriggered = DocoptResult.hasFlag "--full-clean" cli

Target.create "Clean" (fun _ ->
    !! "src/**/bin"
    ++ "tests/**/bin"
    ++ "src/**/obj"
    ++ "tests/**/obj"
    |> Shell.cleanDirs

    if isFullCleanTriggered then
        Trace.log "Full clean triggered, removing build artifacts"
        !! "packages"
        ++ "Generated"
        ++ "paket-files"
        ++ "coverage-results"
        ++ "src/rslib-bindings/target"
        ++ "src/rslib-bindings/tools"
        ++ "src/rslib-bindings/include"
        |> Shell.cleanDirs)

Target.create "Test" (fun _ ->
    DotNet.test
        (fun p ->
            { p with
                Common = dotnetCli p.Common
                Configuration = configuration
                NoBuild = true
                MSBuildParams = noBinLog p.MSBuildParams })
        solution)

Target.create "Coverage" (fun _ ->
    let setParams (o: DotNet.TestOptions) = {
        o with
            Common = dotnetCli o.Common
            Configuration = configuration
            Collect = Some "XPlat Code Coverage"
            ResultsDirectory = Some "coverage-results"
            MSBuildParams = metadata o.MSBuildParams
            Logger = if isCI then Some "GitHubActions" else None
    }

    solution |> DotNet.test setParams

    Trace.log ""
    Trace.log "Coverage results written to ./coverage-results/"
    Trace.log "To generate an HTML report, install dotnet-reportgenerator-globaltool:"
    Trace.log "  dotnet tool install -g dotnet-reportgenerator-globaltool"
    Trace.log
        "  reportgenerator -reports:coverage-results/**/coverage.cobertura.xml -targetdir:coverage-report -reporttypes:Html"
    Trace.log "")

// --- Anki rslib bindings ---

// protoc shipped with Grpc.Tools (Paket group Native), the same one Ankit.Native generates its C# classes with
let grpcToolsProtoc =
    let platform =
        match RuntimeInformation.OSArchitecture with
        | _ when RuntimeInformation.IsOSPlatform OSPlatform.OSX -> "macosx_universal"
        // Grpc.Tools has no Windows Arm64 build, the x64 one runs under emulation
        | Architecture.X86 when RuntimeInformation.IsOSPlatform OSPlatform.Windows -> "windows_x86"
        | _ when RuntimeInformation.IsOSPlatform OSPlatform.Windows -> "windows_x64"
        | Architecture.Arm64 -> "linux_arm64"
        | Architecture.X86 -> "linux_x86"
        | _ -> "linux_x64"

    let exe = if RuntimeInformation.IsOSPlatform OSPlatform.Windows then "protoc.exe" else "protoc"
    Path.Combine(root, "packages", "native", "Grpc.Tools", "tools", platform, exe)

let protocDir = Path.combine rslibBindings "tools/protoc"

// Copied to the stable path .cargo/config.toml points PROTOC at, so plain cargo commands work too
let copyProtoc () =
    if not (File.Exists grpcToolsProtoc) then
        Error [ $"protoc not found at %s{relative grpcToolsProtoc}, run the Restore target" ]
    else
        let target = Path.Combine(protocDir, "bin", Path.GetFileName grpcToolsProtoc)
        Directory.CreateDirectory(Path.GetDirectoryName target) |> ignore
        File.Copy(grpcToolsProtoc, target, true)

        if not (RuntimeInformation.IsOSPlatform OSPlatform.Windows) then
            File.SetUnixFileMode(target, UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute)

        Trace.log $"Using protoc from %s{relative grpcToolsProtoc}"
        Ok()

let cargo (args: string list) =
    CreateProcess.fromRawCommand "cargo" args
    |> CreateProcess.withWorkingDirectory rslibBindings
    |> Proc.run
    |> fun r ->
        let command = String.concat " " args
        if r.ExitCode = 0 then Ok() else Error [ $"cargo %s{command} failed" ]

let cargoProfile =
    match configuration with
    | DotNet.BuildConfiguration.Release -> [ "--release" ]
    | _ -> []

Target.create "Setup-Rslib" (fun _ ->
    result {
        if not (Directory.Exists ankiSources) then
            return! Error [ $"Anki sources not found at %s{relative ankiSources}, run the Restore target" ]

        for folder in [ "ftl/core-repo/core"; "ftl/qt-repo/desktop" ] do
            Directory.CreateDirectory(Path.Combine(ankiSources, folder)) |> ignore

        do! copyProtoc ()
    }
    |> handleResult "Setup-Rslib")

let csharpClient = Path.combine root "src/Ankit.Native/Generated/RslibBackend.Methods.g.cs"

Target.create "Generate-Rslib-Client" (fun _ ->
    result {
        do! cargo ([ "build"; "--locked"; "-p"; "anki_proto" ] @ cargoProfile)
        do! cargo [ "run"; "--locked"; "--quiet"; "-p"; "csharp-gen"; "--"; csharpClient ]
    }
    |> handleResult "Generate-Rslib-Client")

let cHeader = Path.combine rslibBindings "include/ankit_rslib.h"

Target.create "Generate-Rslib-Header" (fun _ ->
    cargo [ "run"; "--locked"; "--quiet"; "-p"; "header-gen"; "--"; cHeader ]
    |> handleResult "Generate-Rslib-Header")

let requirePrepared (target: string) (path: string) =
    if not (File.Exists path) then
        failwithf $"%s{target}: %s{relative path} not found, run the Prepare target"

let protoc = Path.Combine(protocDir, "bin", Path.GetFileName grpcToolsProtoc)

Target.create "Build-Rslib" (fun _ ->
    requirePrepared "Build-Rslib" protoc
    cargo ([ "build"; "--locked" ] @ cargoProfile) |> handleResult "Build-Rslib")

Target.create "Build-Solution" (fun _ ->
    requirePrepared "Build-Solution" csharpClient

    DotNet.build
        (fun p ->
            { p with
                Common = dotnetCli p.Common
                Configuration = configuration
                MSBuildParams = metadata p.MSBuildParams })
        solution)

Target.create "Test-Rslib" (fun _ -> cargo ([ "test"; "--locked" ] @ cargoProfile) |> handleResult "Test-Rslib")

let dockerCompose (args: string list) =
    CreateProcess.fromRawCommand "docker" ("compose" :: args)
    |> CreateProcess.withWorkingDirectory root
    |> Proc.run
    |> fun r ->
        let command = String.concat " " args
        if r.ExitCode = 0 then Ok() else Error [ $"docker compose %s{command} failed" ]

Target.create "Run" (fun _ ->
    dockerCompose [ "up"; "--build"; "--detach" ] |> handleResult "Run")

Target.create "Stop" (fun _ -> dockerCompose [ "down" ] |> handleResult "Stop")

Target.create "Prepare" doNothing
Target.create "Build" doNothing
Target.create "All" doNothing
Target.create "Prepare-Sources" doNothing
Target.create "Before-Push" doNothing

// --- Prepare: restore packages and tools, generate sources ---

"Prepare" <== [
    "Restore"
    "Setup-Rslib"
    "Generate-Rslib-Client"
    "Generate-Rslib-Header"
]

"Restore" ?=> "Setup-Rslib"
"Restore" ?=> "Generate-Rslib-Header"

"Setup-Rslib"
    ==> "Generate-Rslib-Client"

// --- Build: compile and test, from prepared sources ---

"Build" <== [
    "Clean"
    "Build-Rslib"
    "Build-Solution"
]

"Prepare" ?=> "Clean"
"Prepare" ?=> "Build-Rslib"
"Clean" ?=> "Build-Solution"

"Build"
    ==> "Test"

"Build-Rslib"
    ==> "Test-Rslib"

"All" <== [
    "Prepare"
    "Build"
    "Test"
    "Test-Rslib"
]

"Restore"
    ==> "Format"

"Remove-BOM"
    =?> ("Prepare-Sources", verifyEncoding() |> Result.isError)

"Format"
    ==> "Prepare-Sources"

"Before-Push" <== [
    "Prepare-Sources"
    "Verify-Encoding"
    "Verify-Workflow"
    "Test"
]

if DocoptResult.hasFlag "--list" cli then
    Target.listAvailable()
else
    Target.run parallelJobs targetName []
