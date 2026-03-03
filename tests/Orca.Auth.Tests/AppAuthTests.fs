module Orca.Auth.Tests.AppAuthTests

open System
open Xunit
open Orca.Auth.AppAuth
open Orca.Auth.Tests.TestHelpers

// ---------------------------------------------------------------------------
// resolveConfig — env var override tests
// ---------------------------------------------------------------------------

/// Returns a set of env vars that provide a complete App config without any file.
let private allAppEnvVars () =
    [ "ORCA_APP_ID",              Some "app-123"
      "ORCA_APP_INSTALLATION_ID", Some "install-456"
      "ORCA_APP_KEY_PATH",        Some "/tmp/key.pem" ]

[<Fact>]
let ``resolveConfig succeeds when all fields provided via env vars`` () =
    withEnvVars (allAppEnvVars ()) (fun () ->
        let result = resolveConfig ()
        match result with
        | Error e -> Assert.Fail($"Expected Ok but got Error: {e}")
        | Ok cfg  ->
            Assert.Equal("app-123",       cfg.AppId)
            Assert.Equal("install-456",   cfg.InstallationId)
            Assert.Equal("/tmp/key.pem",  cfg.PrivateKeyPath))

[<Fact>]
let ``resolveConfig uses ORCA_APP_ID over stored value`` () =
    withEnvVars
        [ "ORCA_APP_ID",              Some "env-app-id"
          "ORCA_APP_INSTALLATION_ID", Some "install-456"
          "ORCA_APP_KEY_PATH",        Some "/tmp/key.pem" ]
        (fun () ->
            let result = resolveConfig ()
            match result with
            | Error e -> Assert.Fail($"Expected Ok but got Error: {e}")
            | Ok cfg  -> Assert.Equal("env-app-id", cfg.AppId))

[<Fact>]
let ``resolveConfig uses ORCA_APP_INSTALLATION_ID over stored value`` () =
    withEnvVars
        [ "ORCA_APP_ID",              Some "app-123"
          "ORCA_APP_INSTALLATION_ID", Some "env-install-id"
          "ORCA_APP_KEY_PATH",        Some "/tmp/key.pem" ]
        (fun () ->
            let result = resolveConfig ()
            match result with
            | Error e -> Assert.Fail($"Expected Ok but got Error: {e}")
            | Ok cfg  -> Assert.Equal("env-install-id", cfg.InstallationId))

[<Fact>]
let ``resolveConfig uses ORCA_APP_KEY_PATH over stored value`` () =
    withEnvVars
        [ "ORCA_APP_ID",              Some "app-123"
          "ORCA_APP_INSTALLATION_ID", Some "install-456"
          "ORCA_APP_KEY_PATH",        Some "/tmp/env-key.pem" ]
        (fun () ->
            let result = resolveConfig ()
            match result with
            | Error e -> Assert.Fail($"Expected Ok but got Error: {e}")
            | Ok cfg  -> Assert.Equal("/tmp/env-key.pem", cfg.PrivateKeyPath))

[<Fact>]
let ``resolveConfig returns error when App ID is missing`` () =
    withEnvVars
        [ "ORCA_APP_ID",              None
          "ORCA_APP_INSTALLATION_ID", Some "install-456"
          "ORCA_APP_KEY_PATH",        Some "/tmp/key.pem" ]
        (fun () ->
            let result = resolveConfig ()
            match result with
            | Ok _    -> Assert.Fail("Expected Error when App ID is missing")
            | Error e -> Assert.Contains("App ID", e))

[<Fact>]
let ``resolveConfig returns error when Installation ID is missing`` () =
    withEnvVars
        [ "ORCA_APP_ID",              Some "app-123"
          "ORCA_APP_INSTALLATION_ID", None
          "ORCA_APP_KEY_PATH",        Some "/tmp/key.pem" ]
        (fun () ->
            let result = resolveConfig ()
            match result with
            | Ok _    -> Assert.Fail("Expected Error when Installation ID is missing")
            | Error e -> Assert.Contains("Installation ID", e))

[<Fact>]
let ``resolveConfig returns error when private key is missing`` () =
    withEnvVars
        [ "ORCA_APP_ID",              Some "app-123"
          "ORCA_APP_INSTALLATION_ID", Some "install-456"
          "ORCA_APP_KEY_PATH",        None
          "ORCA_APP_PRIVATE_KEY",     None ]
        (fun () ->
            let result = resolveConfig ()
            match result with
            | Ok _    -> Assert.Fail("Expected Error when private key is missing")
            | Error e -> Assert.Contains("key", e.ToLowerInvariant()))

// ---------------------------------------------------------------------------
// AppAuthContext.GetToken — ORCA_APP_PRIVATE_KEY raw PEM env var
// ---------------------------------------------------------------------------

[<Fact>]
let ``AppAuthContext GetToken uses ORCA_APP_PRIVATE_KEY raw PEM instead of key file`` () =
    // We don't have a real key, so we provide a clearly invalid PEM.
    // The test verifies the env var was read (the error will be a JWT generation
    // failure, not a file-not-found error).
    withEnv "ORCA_APP_PRIVATE_KEY" (Some "not-a-real-pem") (fun () ->
        let config = { AppId = "app-123"; PrivateKeyPath = "/nonexistent/key.pem"; InstallationId = "install-456" }
        let ctx = AppAuthContext(config) :> Orca.Core.AuthContext.IAuthContext
        let result = ctx.GetToken() |> Async.RunSynchronously
        match result with
        | Ok _    -> Assert.Fail("Expected error with invalid PEM content")
        | Error e ->
            // Should fail during JWT generation, not file I/O
            Assert.DoesNotContain("nonexistent", e))
