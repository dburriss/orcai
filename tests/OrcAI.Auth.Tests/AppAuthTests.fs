module OrcAI.Auth.Tests.AppAuthTests

open System
open System.IdentityModel.Tokens.Jwt
open Xunit
open OrcAI.Auth.AppAuth
open OrcAI.Auth.Tests.TestHelpers

// ---------------------------------------------------------------------------
// resolveConfigWith — pure env var override tests (no env mutation needed)
// ---------------------------------------------------------------------------

/// Minimal getEnv that returns values from a fixed lookup table.
let private fakeEnv (pairs: (string * string) list) (name: string) : string option =
    pairs |> List.tryFind (fst >> (=) name) |> Option.map snd

let private allAppEnvPairs =
    [ "ORCAI_APP_ID",              "app-123"
      "ORCAI_APP_INSTALLATION_ID", "install-456"
      "ORCAI_APP_KEY_PATH",        "/tmp/key.pem" ]

let private noFileConfig () : Result<AppAuthConfig, string> =
    Error "no stored config"

[<Fact>]
let ``resolveConfigWith succeeds when all fields provided via getEnv`` () =
    let result = resolveConfigWith (fakeEnv allAppEnvPairs) noFileConfig
    match result with
    | Error e -> Assert.Fail($"Expected Ok but got Error: {e}")
    | Ok cfg  ->
        Assert.Equal("app-123",      cfg.AppId)
        Assert.Equal("install-456",  cfg.InstallationId)
        Assert.Equal("/tmp/key.pem", cfg.PrivateKeyPath)

[<Fact>]
let ``resolveConfigWith uses ORCAI_APP_ID over stored value`` () =
    let stored () = Ok { AppId = "stored-id"; PrivateKeyPath = "/tmp/key.pem"; InstallationId = "install-456" }
    let result = resolveConfigWith (fakeEnv ["ORCAI_APP_ID", "env-app-id"
                                             "ORCAI_APP_INSTALLATION_ID", "install-456"
                                             "ORCAI_APP_KEY_PATH", "/tmp/key.pem"]) stored
    match result with
    | Error e -> Assert.Fail($"Expected Ok: {e}")
    | Ok cfg  -> Assert.Equal("env-app-id", cfg.AppId)

[<Fact>]
let ``resolveConfigWith uses ORCAI_APP_INSTALLATION_ID over stored value`` () =
    let stored () = Ok { AppId = "app-123"; PrivateKeyPath = "/tmp/key.pem"; InstallationId = "stored-install" }
    let result = resolveConfigWith (fakeEnv ["ORCAI_APP_ID", "app-123"
                                             "ORCAI_APP_INSTALLATION_ID", "env-install-id"
                                             "ORCAI_APP_KEY_PATH", "/tmp/key.pem"]) stored
    match result with
    | Error e -> Assert.Fail($"Expected Ok: {e}")
    | Ok cfg  -> Assert.Equal("env-install-id", cfg.InstallationId)

[<Fact>]
let ``resolveConfigWith uses ORCAI_APP_KEY_PATH over stored value`` () =
    let stored () = Ok { AppId = "app-123"; PrivateKeyPath = "/stored/key.pem"; InstallationId = "install-456" }
    let result = resolveConfigWith (fakeEnv ["ORCAI_APP_ID", "app-123"
                                             "ORCAI_APP_INSTALLATION_ID", "install-456"
                                             "ORCAI_APP_KEY_PATH", "/env/key.pem"]) stored
    match result with
    | Error e -> Assert.Fail($"Expected Ok: {e}")
    | Ok cfg  -> Assert.Equal("/env/key.pem", cfg.PrivateKeyPath)

[<Fact>]
let ``resolveConfigWith returns error when App ID is missing`` () =
    let result = resolveConfigWith (fakeEnv ["ORCAI_APP_INSTALLATION_ID", "install-456"
                                             "ORCAI_APP_KEY_PATH", "/tmp/key.pem"]) noFileConfig
    match result with
    | Ok _    -> Assert.Fail("Expected Error when App ID is missing")
    | Error e -> Assert.Contains("App ID", e)

[<Fact>]
let ``resolveConfigWith returns error when Installation ID is missing`` () =
    let result = resolveConfigWith (fakeEnv ["ORCAI_APP_ID", "app-123"
                                             "ORCAI_APP_KEY_PATH", "/tmp/key.pem"]) noFileConfig
    match result with
    | Ok _    -> Assert.Fail("Expected Error when Installation ID is missing")
    | Error e -> Assert.Contains("Installation ID", e)

[<Fact>]
let ``resolveConfigWith returns error when private key is missing`` () =
    let result = resolveConfigWith (fakeEnv ["ORCAI_APP_ID", "app-123"
                                             "ORCAI_APP_INSTALLATION_ID", "install-456"]) noFileConfig
    match result with
    | Ok _    -> Assert.Fail("Expected Error when private key is missing")
    | Error e -> Assert.Contains("key", e.ToLowerInvariant())

[<Fact>]
let ``resolveConfigWith falls back to stored config when env vars absent`` () =
    let stored () = Ok { AppId = "stored-app"; PrivateKeyPath = "/stored/k.pem"; InstallationId = "stored-install" }
    let result = resolveConfigWith (fakeEnv []) stored
    match result with
    | Error e -> Assert.Fail($"Expected Ok: {e}")
    | Ok cfg  ->
        Assert.Equal("stored-app",     cfg.AppId)
        Assert.Equal("stored-install", cfg.InstallationId)
        Assert.Equal("/stored/k.pem",  cfg.PrivateKeyPath)

// ---------------------------------------------------------------------------
// resolveConfig — keep existing env-mutation-based integration tests
// ---------------------------------------------------------------------------

let private allAppEnvVars () =
    [ "ORCAI_APP_ID",              Some "app-123"
      "ORCAI_APP_INSTALLATION_ID", Some "install-456"
      "ORCAI_APP_KEY_PATH",        Some "/tmp/key.pem" ]

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

// ---------------------------------------------------------------------------
// generateJwtAt — pure tests with a known RSA key and fixed timestamp
// ---------------------------------------------------------------------------

// Minimal 2048-bit RSA key for testing only (PKCS#8 PEM).
let private testPem = """-----BEGIN PRIVATE KEY-----
MIIEvgIBADANBgkqhkiG9w0BAQEFAASCBKgwggSkAgEAAoIBAQCa+b9XQeeRJ/a7
4Ft1Lk1W9ZWb7k8WxJGdPP9XLlqKyBDZy/8I4ukl6DGEKaPVS3kZdd9jo+GcyOu5
D7tIVTvMT0xJQmhpPr6rWTnWz7APs71KdjNeJYcRvOiZ2jK9r9IQljrCLlyPf2wH
ej6T1hsJfdnwjb/yeG4HpZ9HAA8u+7/p5VEpOqN3elxs7YlgyvTMMK2AeCrojepJ
h5N73lV5VScP+eCTkFpP93Avgpww7an+Y0V8+Xcv/jhFqYbtjf/ZMTeyvmH6h/Np
tUAPRrPFzT3dU/1+Atgc6f3v+LN8mLQqkvoZMtE3kixjx7kaJAFgJCeR6thYmqPD
chljcN/TAgMBAAECggEAJfpzQhJ0CbYF+Ke0MgTNTjSz27ksZ5N3bdWfa4GADceW
nZEo6EgXQ8NhsxYzQJeUz0D8JCJqrS3t2nW4+zJsC5cZRlDAXp5SQpKEophV+Jsf
FcrersE6lwW46M84pRSbwZXXQ3PyGfZrhm+WO0t6Z7qQOKu8MNMDf9s+K7uffO8x
7q6WnDxbbwC9XSzBq8F3ohHwxBg1O6JM0kBYxYLej2I+po2z2HDCPr1Mf+pglQU0
cttkZkOAu/Fx6HibV9JUr8xQdaSad9/iy4X4yWzG6yufaavAWCV/vEh4tz0so7He
6x83AAcmnjfYzasIbKVRa6l0zR1J4i3uf3w58E52IQKBgQDY+1Pdkpb4V8ASyFNc
MWSSjNKOKbj1iXZ+TnKaHWHHAyXpBtyIdRvQw/sFQeCgIUcz6OaD1SA8IO39JC69
LNTWsOWZ1t1koh7eLxr1qIn1eXj9r0FXONjFK0Su1zBJHrCbKAofB4wvJggA0QdR
8gq3hXcrXztc/3GsawKIJdjoIQKBgQC21/09QLFdrtkFhqI2bApZfPEQvtd6vmDv
wPJywqZJGpcKkca2oZ0Vwrr1FyRKu7kWW16gVMe8pEfCZ5UTXyEozf8ueQcTagzU
PHKMRh2Ye2P3SG0z7Zwj/KottzWnAvhm+KJHFKUqD3XMhDWM7lJFn12vgsXw4WA7
/8qZsCl5cwKBgQC+kWXj6XZMoQ0hse18wCi7iZD3qO84P0XhwtZmQr34guxN0Gfq
NSh731RdFrHJEdEuZzPlv05zYNyEgr3GClTYRj8xMQP6+WQw8aA095RLEyfPbpft
mhDQgqLtCDPxVFH5w124SPG3CyjmRq+uKe19p2u1nQtPL07QBqAPoWXy4QKBgH7q
sDK7XCJuQuBOEvz5w7lYO7Dm94WQ7pKdeO1l5azq0xsYEzokNnirYcDMnnltks1N
AQMDtl1gHxt3cQgwSUEctFva0KmOPHd5uf1akiKMy9gTIxIfhfmI4cu313slWa2I
OoRidT8b2iXrQ4yexObk90/j02gf2P/szwIdQLy3AoGBAIca7SP30GwxvjIejUpB
Omc1MQ1z8WsYvfJWEvG7aDa/uDpU3sclFNEs3gPYpyoMOlJr2EzqpCBkQDCt/zzK
mdyIOOc+CZMhWDNkHWvRerHywvqzWkvN//zAkkoPlNU/9sOWIc1U1BCYnte9v8OF
pFGDL5VHXkN5wfx1/o2C8pUL
-----END PRIVATE KEY-----"""

let private fixedNow = DateTimeOffset.UtcNow.AddHours(1.0)

[<Fact>]
let ``generateJwtAt returns Ok with a valid JWT for a real RSA key`` () =
    match generateJwtAt fixedNow "my-app-123" testPem with
    | Error e  -> Assert.Fail($"Expected Ok but got Error: {e}")
    | Ok token ->
        // A JWT has three dot-separated base64url segments
        let parts = token.Split('.')
        Assert.Equal(3, parts.Length)

[<Fact>]
let ``generateJwtAt JWT contains correct issuer claim`` () =
    match generateJwtAt fixedNow "my-app-123" testPem with
    | Error e  -> Assert.Fail($"Expected Ok: {e}")
    | Ok token ->
        let handler = JwtSecurityTokenHandler()
        let jwt     = handler.ReadJwtToken(token)
        Assert.Equal("my-app-123", jwt.Issuer)

[<Fact>]
let ``generateJwtAt JWT iat is 60 seconds before now`` () =
    match generateJwtAt fixedNow "my-app-123" testPem with
    | Error e  -> Assert.Fail($"Expected Ok: {e}")
    | Ok token ->
        let handler  = JwtSecurityTokenHandler()
        let jwt      = handler.ReadJwtToken(token)
        let expected = fixedNow.AddSeconds(-60.0).ToUnixTimeSeconds()
        let actual   = DateTimeOffset(jwt.IssuedAt, TimeSpan.Zero).ToUnixTimeSeconds()
        Assert.Equal(expected, actual)

[<Fact>]
let ``generateJwtAt JWT exp is 10 minutes after now`` () =
    match generateJwtAt fixedNow "my-app-123" testPem with
    | Error e  -> Assert.Fail($"Expected Ok: {e}")
    | Ok token ->
        let handler  = JwtSecurityTokenHandler()
        let jwt      = handler.ReadJwtToken(token)
        let expected = fixedNow.AddMinutes(10.0).ToUnixTimeSeconds()
        let actual   = DateTimeOffset(jwt.ValidTo, TimeSpan.Zero).ToUnixTimeSeconds()
        Assert.Equal(expected, actual)

[<Fact>]
let ``generateJwtAt returns error for invalid PEM`` () =
    match generateJwtAt fixedNow "app-id" "not-a-valid-pem" with
    | Ok _    -> Assert.Fail("Expected Error for invalid PEM")
    | Error e -> Assert.Contains("Failed to generate JWT", e)

// ---------------------------------------------------------------------------
// AppAuthContext.GetToken — getEnv injection tests (no file I/O, no env mutation)
// ---------------------------------------------------------------------------

[<Fact>]
let ``AppAuthContext GetToken uses ORCAI_APP_PRIVATE_KEY from getEnv instead of key file`` () =
    // Provide an invalid PEM via getEnv — should fail at JWT generation, not file I/O.
    let getEnv name =
        match name with
        | "ORCAI_APP_PRIVATE_KEY" -> Some "not-a-real-pem"
        | _                      -> None
    let config = { AppId = "app-123"; PrivateKeyPath = "/nonexistent/key.pem"; InstallationId = "install-456" }
    // readFile should never be called when the env var is set
    let readFile _ = failwith "readFile should not be called"
    let ctx = AppAuthContext(config, getEnv, readFile) :> OrcAI.Core.AuthContext.IAuthContext
    let result = ctx.GetToken() |> Async.RunSynchronously
    match result with
    | Ok _    -> Assert.Fail("Expected error with invalid PEM content")
    | Error e -> Assert.DoesNotContain("nonexistent", e)

[<Fact>]
let ``AppAuthContext GetToken falls back to readFile when env var absent`` () =
    let getEnv _ = None  // no env var
    let config = { AppId = "app-123"; PrivateKeyPath = "/some/key.pem"; InstallationId = "install-456" }
    let mutable readFileCalled = false
    let readFile _ =
        readFileCalled <- true
        "not-a-real-pem"  // will fail at JWT generation
    let ctx = AppAuthContext(config, getEnv, readFile) :> OrcAI.Core.AuthContext.IAuthContext
    let _ = ctx.GetToken() |> Async.RunSynchronously
    Assert.True(readFileCalled, "Expected readFile to be called when env var is absent")

// ---------------------------------------------------------------------------
// storeConfigTo / loadConfigFrom — round-trip and error cases using a temp dir
// ---------------------------------------------------------------------------

let private withTempHome (f: string -> unit) =
    let dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), System.Guid.NewGuid().ToString())
    try f dir
    finally
        if System.IO.Directory.Exists(dir) then
            System.IO.Directory.Delete(dir, true)

[<Fact>]
let ``storeConfigTo then loadConfigFrom round-trips all fields`` () =
    withTempHome (fun home ->
        let config = { AppId = "app-999"; PrivateKeyPath = "/keys/my.pem"; InstallationId = "install-777" }
        match storeConfigTo home "my-app" config with
        | Error e -> Assert.Fail($"storeConfigTo failed: {e}")
        | Ok ()   ->
            match loadConfigFrom home with
            | Error e  -> Assert.Fail($"loadConfigFrom failed: {e}")
            | Ok loaded ->
                Assert.Equal("app-999",       loaded.AppId)
                Assert.Equal("/keys/my.pem",  loaded.PrivateKeyPath)
                Assert.Equal("install-777",   loaded.InstallationId))

[<Fact>]
let ``storeConfigTo stores profile under the given name`` () =
    withTempHome (fun home ->
        let config = { AppId = "app-999"; PrivateKeyPath = "/keys/my.pem"; InstallationId = "install-777" }
        match storeConfigTo home "my-profile" config with
        | Error e -> Assert.Fail($"storeConfigTo failed: {e}")
        | Ok ()   ->
            match OrcAI.Auth.AuthConfig.readConfigFrom home with
            | Error e  -> Assert.Fail($"readConfigFrom failed: {e}")
            | Ok cfg ->
                Assert.Equal("my-profile", cfg.Active)
                Assert.True(cfg.Profiles.ContainsKey("my-profile")))

[<Fact>]
let ``loadConfigFrom returns error when config file does not exist`` () =
    withTempHome (fun home ->
        match loadConfigFrom home with
        | Ok _    -> Assert.Fail("Expected Error when no config file exists")
        | Error e -> Assert.Contains("No auth config found", e))

[<Fact>]
let ``loadConfigFrom returns error when active profile is type pat`` () =
    withTempHome (fun home ->
        // Write a config with a PAT profile set as active.
        let profiles = System.Collections.Generic.Dictionary<string, OrcAI.Auth.AuthConfig.ProfileEntry>()
        profiles.["pat"] <- { Type = "pat"; Token = Some "ghp_test"; AppId = None; KeyPath = None; InstallationId = None }
        let cfg : OrcAI.Auth.AuthConfig.AuthConfigFile = { Active = "pat"; Profiles = profiles }
        OrcAI.Auth.AuthConfig.writeConfigTo home cfg |> ignore
        match loadConfigFrom home with
        | Ok _    -> Assert.Fail("Expected Error when active profile type is pat")
        | Error e -> Assert.Contains("not an App config", e))

[<Fact>]
let ``loadConfigFrom returns error when app config fields are incomplete`` () =
    withTempHome (fun home ->
        // Write an app profile with missing InstallationId.
        let profiles = System.Collections.Generic.Dictionary<string, OrcAI.Auth.AuthConfig.ProfileEntry>()
        profiles.["my-app"] <- { Type = "app"; Token = None; AppId = Some "app-123"; KeyPath = Some "/key.pem"; InstallationId = None }
        let cfg : OrcAI.Auth.AuthConfig.AuthConfigFile = { Active = "my-app"; Profiles = profiles }
        OrcAI.Auth.AuthConfig.writeConfigTo home cfg |> ignore
        match loadConfigFrom home with
        | Ok _    -> Assert.Fail("Expected Error for incomplete app config")
        | Error e -> Assert.Contains("incomplete", e))

[<Fact>]
let ``loadConfigFrom returns error when active profile does not exist`` () =
    withTempHome (fun home ->
        // Write a config where active points to a missing profile.
        let profiles = System.Collections.Generic.Dictionary<string, OrcAI.Auth.AuthConfig.ProfileEntry>()
        profiles.["other-app"] <- { Type = "app"; Token = None; AppId = Some "app-123"; KeyPath = Some "/key.pem"; InstallationId = Some "install-1" }
        let cfg : OrcAI.Auth.AuthConfig.AuthConfigFile = { Active = "missing-profile"; Profiles = profiles }
        OrcAI.Auth.AuthConfig.writeConfigTo home cfg |> ignore
        match loadConfigFrom home with
        | Ok _    -> Assert.Fail("Expected Error when active profile does not exist")
        | Error e -> Assert.Contains("missing-profile", e))
