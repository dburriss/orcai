module Orca.Auth.Tests.PatAuthTests

open System
open Xunit
open Orca.Auth.PatAuth
open Orca.Auth.Tests.TestHelpers

// ---------------------------------------------------------------------------
// loadToken — env var tests (no file I/O needed)
// ---------------------------------------------------------------------------

[<Fact>]
let ``loadToken returns ORCA_PAT when env var is set`` () =
    withEnv "ORCA_PAT" (Some "ghp_test_token_from_env") (fun () ->
        let result = loadToken ()
        Assert.Equal(Ok "ghp_test_token_from_env", result))

[<Fact>]
let ``loadToken ignores ORCA_PAT when it is empty string`` () =
    // Empty string should fall through to the file path (which will error
    // because no config file exists in a clean test environment).
    withEnv "ORCA_PAT" (Some "") (fun () ->
        let result = loadToken ()
        match result with
        | Error _ -> () // expected — no config file present
        | Ok _    -> Assert.Fail("Expected an error when ORCA_PAT is empty and no file exists"))

[<Fact>]
let ``loadToken returns error when ORCA_PAT absent and no config file exists`` () =
    withEnv "ORCA_PAT" None (fun () ->
        // Point config to a temp dir that has no auth.json.
        let tmpHome = IO.Path.Combine(IO.Path.GetTempPath(), Guid.NewGuid().ToString())
        let originalHome = Environment.GetEnvironmentVariable("HOME")
        try
            // Override HOME so configPath() resolves to the temp directory.
            // Note: on Windows this would be USERPROFILE; this test targets macOS/Linux.
            Environment.SetEnvironmentVariable("HOME", tmpHome)
            let result = loadToken ()
            match result with
            | Error _ -> () // expected
            | Ok _    -> Assert.Fail("Expected an error when no env var and no config file")
        finally
            Environment.SetEnvironmentVariable("HOME", originalHome)
            if IO.Directory.Exists(tmpHome) then IO.Directory.Delete(tmpHome, true))
