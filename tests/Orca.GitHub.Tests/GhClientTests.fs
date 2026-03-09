module Orca.GitHub.Tests.GhClientTests

open Xunit
open SimpleExec

// ---------------------------------------------------------------------------
// Integration tests for the gh CLI wrapper.
//
// These tests are opt-in and require GH_TOKEN to be set in the environment.
// They test real subprocess invocations against the gh binary.
// ---------------------------------------------------------------------------

[<Fact>]
let ``GhCliClient can invoke gh --version`` () : unit =
    // Verify the gh binary is present and exits with code 0.
    // --version does not require authentication, so no GH_TOKEN is needed.
    let struct (stdout, _) =
        Command.ReadAsync("gh", "--version")
        |> Async.AwaitTask
        |> Async.RunSynchronously
    Assert.False(System.String.IsNullOrWhiteSpace(stdout), "Expected gh --version to produce output")
