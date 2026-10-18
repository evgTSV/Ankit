#r "nuget: Fake.Core.Trace, 6.2.0"
#r "nuget: Fake.Core.Target, 6.2.0"
#r "nuget: Fake.Core.CommandLineParsing, 6.2.0"
#r "nuget: Fake.IO.FileSystem, 6.2.0"
#r "nuget: Fake.DotNet.Cli, 6.2.0"
#r "nuget: Fake.Tools.Git, 6.2.0"
#r "nuget: FsToolkit.ErrorHandling, 5.2.0"

open System
open System.IO
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
let license = "MIT"
let authors = [ "EvgTsv" ]

let isCI = Environment.GetEnvironmentVariable("CI") <> null

let root = Path.GetFullPath(__SOURCE_DIRECTORY__ + "/..")
let solution = root + "/Ankit.sln"
let usage =
    """
Usage:
  build.fsx [options] [<target>]
  build.fsx --list
  build.fsx -h | --help

Options:
  -c, --configuration <name>  Build configuration: Debug or Release (default: Release).
  -p, --parallel <jobs>       Number of targets to run in parallel (default: 1).
  --list                      List available targets.
  -h, --help                  Show this help.
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
    |> Option.defaultValue "Test"

// Docopt does not reject unknown options, they end up as the positional target
if targetName.StartsWith "-" then
    eprintfn $"Unknown or incomplete option: %s{targetName}"
    eprintfn $"%s{usage}"
    exit 1

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
    Trace.log $"Project: %s{project}"
    Trace.log $"Description: %s{description}"
    Trace.log $"Summary: %s{summary}"
    Trace.log $"License: %s{license}"
    Trace.log $"Authors: %A{authors}"
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

Target.create "Clean" (fun _ ->
    !! "src/**/bin"
    ++ "tests/**/bin"
    ++ "src/**/obj"
    ++ "tests/**/obj"
    |> Shell.cleanDirs)

Target.create "Build" (fun _ ->
    DotNet.build
        (fun p ->
            { p with
                Common = dotnetCli p.Common
                Configuration = configuration
                MSBuildParams = noBinLog p.MSBuildParams })
        solution)

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
            DotNet.TestOptions.MSBuildParams.DisableInternalBinLog = true
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

Target.create "All" doNothing
Target.create "Prepare-Sources" doNothing

"Build" <== [
    "Restore"
    "Clean"
]

"Build"
    ==> "Test"
    ==> "All"

"Restore"
    ==> "Format"

"Remove-BOM"
    =?> ("Prepare-Sources", verifyEncoding() |> Result.isError)

"Format"
    ==> "Prepare-Sources"

if DocoptResult.hasFlag "--list" cli then
    Target.listAvailable ()
else
    Target.run parallelJobs targetName []
