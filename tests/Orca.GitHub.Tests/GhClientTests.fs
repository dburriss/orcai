module Orca.GitHub.Tests.GhClientTests

open Xunit

// ---------------------------------------------------------------------------
// Integration tests for the gh CLI wrapper.
//
// These tests are opt-in and require GH_TOKEN to be set in the environment.
// They test real subprocess invocations against the gh binary.
// ---------------------------------------------------------------------------

[<Fact(Skip = "Integration test — requires GH_TOKEN environment variable")>]
let ``GhCliClient can invoke gh --version`` () : unit =
    // TODO: assert gh binary is found and returns exit code 0
    failwith "not implemented"
