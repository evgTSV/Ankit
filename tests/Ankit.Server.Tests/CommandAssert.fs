module Ankit.Server.Tests.CommandAssert

open Ankit.Server.Services
open Xunit

let ok (result: CommandResult<'a>) : 'a =
    match result with
    | Ok value -> value
    | Error e -> failwith $"Expected success, got %A{e}"

let notFound (result: CommandResult<'a>) =
    match result with
    | Error(CommandFailed.NotFound _) -> ()
    | other -> Assert.Fail $"Expected NotFound, got %A{other}"

let rejected (result: CommandResult<'a>) =
    match result with
    | Error(CommandFailed.Rejected _) -> ()
    | other -> Assert.Fail $"Expected Rejected, got %A{other}"
