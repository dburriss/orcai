module OrcAI.Auth.Tests.PatAuthTests

open Xunit
open OrcAI.Auth.PatAuth
open OrcAI.Auth.AuthConfig
open OrcAI.Auth.Tests.TestData
open OrcAI.Auth.Tests.TestHelpers

// ---------------------------------------------------------------------------
// loadTokenWith — pure tests (no env mutation)
// ---------------------------------------------------------------------------

let private noConfig () : Result<AuthConfigFile, string> = Error "no config"

let private patConfig token =
    fun () -> Ok (A.AuthConfig.withProfiles "pat" [ "pat", A.ProfileEntry.pat token ])

[<Fact>]
let ``loadTokenWith returns token from getEnv when ORCAI_PAT is set`` () =
    let getEnv name = if name = "ORCAI_PAT" then Some "ghp_from_env" else None
    Assert.Equal(Ok "ghp_from_env", loadTokenWith getEnv noConfig)

[<Fact>]
let ``loadTokenWith ignores empty ORCAI_PAT and falls back to config`` () =
    let getEnv name = if name = "ORCAI_PAT" then Some "" else None
    Assert.Equal(Ok "ghp_stored", loadTokenWith getEnv (patConfig "ghp_stored"))

[<Fact>]
let ``loadTokenWith returns error when env var absent and no config`` () =
    Assert.True(Result.isError (loadTokenWith (fun _ -> None) noConfig))

[<Fact>]
let ``loadTokenWith returns error when active profile type is not pat`` () =
    let appConfig =
        fun () -> Ok (A.AuthConfig.withProfiles "my-app" [ "my-app", A.ProfileEntry.app "id" "/k" "i" ])
    match loadTokenWith (fun _ -> None) appConfig with
    | Ok _    -> Assert.Fail("Expected error for non-PAT config")
    | Error e -> Assert.Contains("PAT", e)

[<Fact>]
let ``loadTokenWith returns error when pat token in config is empty`` () =
    let emptyToken =
        fun () -> Ok (A.AuthConfig.withProfiles "pat" [ "pat", A.ProfileEntry.pat "" ])
    Assert.True(Result.isError (loadTokenWith (fun _ -> None) emptyToken))

// ---------------------------------------------------------------------------
// loadToken — integration: real env + real config file path
// ---------------------------------------------------------------------------

[<Fact>]
let ``loadToken returns ORCAI_PAT when env var is set`` () =
    withEnv "ORCAI_PAT" (Some "ghp_test_token_from_env") (fun () ->
        Assert.Equal(Ok "ghp_test_token_from_env", loadToken ()))

[<Fact>]
let ``loadToken ignores ORCAI_PAT when it is empty string`` () =
    withEnv "ORCAI_PAT" (Some "") (fun () ->
        match loadToken () with
        | Error _ -> ()
        | Ok _    -> Assert.Fail("Expected an error when ORCAI_PAT is empty and no file exists"))

[<Fact>]
let ``loadToken returns error when ORCAI_PAT absent and no config file exists`` () =
    withEnv "ORCAI_PAT" None (fun () ->
        withTempHome (fun tmpHome ->
            withEnv "HOME" (Some tmpHome) (fun () ->
                match loadToken () with
                | Error _ -> ()
                | Ok _    -> Assert.Fail("Expected an error when no env var and no config file"))))
