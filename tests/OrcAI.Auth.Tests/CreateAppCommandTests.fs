module OrcAI.Auth.Tests.CreateAppCommandTests

open System.Text.Json
open Xunit
open Testably.Abstractions.Testing
open OrcAI.Auth.CreateAppCommand

// ---------------------------------------------------------------------------
// buildManifest — pure, no I/O
// ---------------------------------------------------------------------------

[<Fact>]
let ``buildManifest includes the supplied app name`` () =
    let json = buildManifest "my-orca-app" "http://localhost:9876/callback"
    let doc  = JsonDocument.Parse(json)
    let name = doc.RootElement.GetProperty("name").GetString()
    Assert.Equal("my-orca-app", name)

[<Fact>]
let ``buildManifest includes the supplied redirect_url`` () =
    let json = buildManifest "orca" "http://localhost:1234/callback"
    let doc  = JsonDocument.Parse(json)
    let url  = doc.RootElement.GetProperty("redirect_url").GetString()
    Assert.Equal("http://localhost:1234/callback", url)

[<Fact>]
let ``buildManifest is private`` () =
    let json   = buildManifest "orca" "http://localhost:9876/callback"
    let doc    = JsonDocument.Parse(json)
    let public_ = doc.RootElement.GetProperty("public").GetBoolean()
    Assert.False(public_)

[<Fact>]
let ``buildManifest includes required permissions`` () =
    let json  = buildManifest "orca" "http://localhost:9876/callback"
    let doc   = JsonDocument.Parse(json)
    let perms = doc.RootElement.GetProperty("default_permissions")
    Assert.Equal("write", perms.GetProperty("issues").GetString())
    Assert.Equal("read",  perms.GetProperty("pull_requests").GetString())
    Assert.Equal("read",  perms.GetProperty("metadata").GetString())
    Assert.Equal("write", perms.GetProperty("repository_projects").GetString())

// ---------------------------------------------------------------------------
// buildGitHubUrl — pure
// ---------------------------------------------------------------------------

[<Fact>]
let ``buildGitHubUrl targets personal account when org is None`` () =
    let url = buildGitHubUrl None "abc123"
    Assert.StartsWith("https://github.com/settings/apps/new", url)
    Assert.Contains("state=abc123", url)

[<Fact>]
let ``buildGitHubUrl targets org when org is Some`` () =
    let url = buildGitHubUrl (Some "my-org") "xyz"
    Assert.Contains("/organizations/my-org/settings/apps/new", url)
    Assert.Contains("state=xyz", url)

// ---------------------------------------------------------------------------
// buildPermissionsUrl — pure
// ---------------------------------------------------------------------------

[<Fact>]
let ``buildPermissionsUrl targets personal account when org is None`` () =
    let url = buildPermissionsUrl None "my-app"
    Assert.Equal("https://github.com/settings/apps/my-app/permissions", url)

[<Fact>]
let ``buildPermissionsUrl targets org when org is Some`` () =
    let url = buildPermissionsUrl (Some "my-org") "my-app"
    Assert.Equal("https://github.com/organizations/my-org/settings/apps/my-app/permissions", url)

// ---------------------------------------------------------------------------
// buildFormPage — pure
// ---------------------------------------------------------------------------

[<Fact>]
let ``buildFormPage contains a form that posts to the GitHub URL`` () =
    let html = buildFormPage "https://github.com/settings/apps/new?state=s" "{}"
    Assert.Contains("""action="https://github.com/settings/apps/new?state=s" method="post">""", html)

[<Fact>]
let ``buildFormPage HTML-encodes double quotes in the manifest`` () =
    let manifest = """{"name":"orca"}"""
    let html     = buildFormPage "https://github.com/settings/apps/new" manifest
    // The double quotes inside the manifest must be entity-encoded when embedded in an attribute.
    Assert.Contains("&quot;name&quot;", html)
    // The raw unencoded double-quote must not appear inside the attribute value content.
    Assert.DoesNotContain("""value="{"name""", html)

// ---------------------------------------------------------------------------
// parseConversionResponse — pure
// ---------------------------------------------------------------------------

let private validJson = """
{
  "id": 123456,
  "name": "OrcAI App",
  "slug": "orcai-app",
  "owner": { "login": "my-org", "type": "Organization" },
  "pem": "-----BEGIN RSA PRIVATE KEY-----\nMIIEpAIBAAKCAQEA...\n-----END RSA PRIVATE KEY-----\n",
  "webhook_secret": "abc123secret"
}
"""

[<Fact>]
let ``parseConversionResponse extracts all fields from valid JSON`` () =
    match parseConversionResponse validJson with
    | Error e -> Assert.Fail($"Expected Ok but got Error: {e}")
    | Ok app  ->
        Assert.Equal("123456",     app.Id)
        Assert.Equal("OrcAI App",  app.Name)
        Assert.Equal("orcai-app",  app.Slug)
        Assert.Equal("my-org",     app.OwnerLogin)
        Assert.True(app.OwnerIsOrg)
        Assert.Contains("PRIVATE", app.Pem)
        Assert.Equal(Some "abc123secret", app.WebhookSecret)

[<Fact>]
let ``parseConversionResponse sets OwnerIsOrg false for User owner`` () =
    let json = """{"id":1,"name":"orca","slug":"orca","owner":{"login":"alice","type":"User"},"pem":"key","webhook_secret":"s"}"""
    match parseConversionResponse json with
    | Error e -> Assert.Fail($"Expected Ok but got Error: {e}")
    | Ok app  ->
        Assert.Equal("alice", app.OwnerLogin)
        Assert.False(app.OwnerIsOrg)

[<Fact>]
let ``parseConversionResponse falls back to name when slug is absent`` () =
    let json = """{"id":1,"name":"orca","pem":"key","webhook_secret":"s"}"""
    match parseConversionResponse json with
    | Error e -> Assert.Fail($"Expected Ok but got Error: {e}")
    | Ok app  -> Assert.Equal("orca", app.Slug)

[<Fact>]
let ``parseConversionResponse sets empty owner fields when owner is absent`` () =
    let json = """{"id":1,"name":"orca","pem":"key"}"""
    match parseConversionResponse json with
    | Error e -> Assert.Fail($"Expected Ok but got Error: {e}")
    | Ok app  ->
        Assert.Equal("", app.OwnerLogin)
        Assert.False(app.OwnerIsOrg)

[<Fact>]
let ``parseConversionResponse treats null webhook_secret as None`` () =
    let json = """{"id":1,"name":"orca","pem":"key","webhook_secret":null}"""
    match parseConversionResponse json with
    | Error e -> Assert.Fail($"Expected Ok but got Error: {e}")
    | Ok app  -> Assert.Equal(None, app.WebhookSecret)

[<Fact>]
let ``parseConversionResponse succeeds when webhook_secret is absent`` () =
    let json = """{"id":1,"name":"orca","pem":"key"}"""
    match parseConversionResponse json with
    | Error e -> Assert.Fail($"Expected Ok but got Error: {e}")
    | Ok app  -> Assert.Equal(None, app.WebhookSecret)

[<Fact>]
let ``parseConversionResponse returns Error when id is missing`` () =
    let json = """{"name":"orca","pem":"key","webhook_secret":"s"}"""
    match parseConversionResponse json with
    | Ok _    -> Assert.Fail("Expected Error but got Ok")
    | Error e -> Assert.Contains("id", e)

[<Fact>]
let ``parseConversionResponse returns Error when pem is missing`` () =
    let json = """{"id":1,"name":"orca","webhook_secret":"s"}"""
    match parseConversionResponse json with
    | Ok _    -> Assert.Fail("Expected Error but got Ok")
    | Error e -> Assert.Contains("pem", e)

[<Fact>]
let ``parseConversionResponse returns Error on malformed JSON`` () =
    match parseConversionResponse "not-json" with
    | Ok _    -> Assert.Fail("Expected Error but got Ok")
    | Error _ -> ()

[<Fact>]
let ``parseConversionResponse handles string id`` () =
    let json = """{"id":"789","name":"orca","pem":"key","webhook_secret":"s"}"""
    match parseConversionResponse json with
    | Error e -> Assert.Fail($"Expected Ok but got Error: {e}")
    | Ok app  -> Assert.Equal("789", app.Id)

// ---------------------------------------------------------------------------
// savePem — uses MockFileSystem (in-memory, no real disk I/O)
// ---------------------------------------------------------------------------

[<Fact>]
let ``savePem writes PEM content to the expected path`` () =
    let fs      = MockFileSystem(fun o -> o.SimulatingOperatingSystem(SimulationMode.Linux))
    let homeDir = "/home/user"
    let appName = "my-app"
    let pem     = "-----BEGIN RSA PRIVATE KEY-----\nfake\n-----END RSA PRIVATE KEY-----\n"
    match savePem fs homeDir appName pem with
    | Error e -> Assert.Fail($"Expected Ok but got Error: {e}")
    | Ok path ->
        Assert.Equal("/home/user/.config/orcai/my-app.pem", path)
        Assert.True(fs.File.Exists(path), "PEM file should exist")
        Assert.Equal(pem, fs.File.ReadAllText(path))

[<Fact>]
let ``savePem creates parent directories if they do not exist`` () =
    let fs      = MockFileSystem(fun o -> o.SimulatingOperatingSystem(SimulationMode.Linux))
    let homeDir = "/home/newuser"
    match savePem fs homeDir "app" "pem-content" with
    | Error e -> Assert.Fail($"Expected Ok but got Error: {e}")
    | Ok path ->
        Assert.True(fs.File.Exists(path), "PEM file should exist after directory creation")

[<Fact>]
let ``savePem returns the correct path for a different app name`` () =
    let fs      = MockFileSystem(fun o -> o.SimulatingOperatingSystem(SimulationMode.Linux))
    let homeDir = "/root"
    match savePem fs homeDir "other-app" "key" with
    | Error e -> Assert.Fail($"Expected Ok but got Error: {e}")
    | Ok path -> Assert.Equal("/root/.config/orcai/other-app.pem", path)

[<Fact>]
let ``savePem writes PEM to correct path on Linux-simulated filesystem`` () =
    let fs      = MockFileSystem(fun o -> o.SimulatingOperatingSystem(SimulationMode.Linux))
    let homeDir = "/home/user"
    let appName = "my-app"
    let pem     = "-----BEGIN RSA PRIVATE KEY-----\nfake\n-----END RSA PRIVATE KEY-----\n"
    match savePem fs homeDir appName pem with
    | Error e -> Assert.Fail($"Expected Ok but got Error: {e}")
    | Ok path ->
        Assert.Equal("/home/user/.config/orcai/my-app.pem", path)
        Assert.True(fs.File.Exists(path), "PEM file should exist")
        Assert.Equal(pem, fs.File.ReadAllText(path))

[<Fact>]
let ``savePem writes PEM to correct path on Windows-simulated filesystem`` () =
    let fs      = MockFileSystem(fun o -> o.SimulatingOperatingSystem(SimulationMode.Windows))
    let homeDir = @"C:\Users\user"
    let appName = "my-app"
    let pem     = "-----BEGIN RSA PRIVATE KEY-----\nfake\n-----END RSA PRIVATE KEY-----\n"
    match savePem fs homeDir appName pem with
    | Error e -> Assert.Fail($"Expected Ok but got Error: {e}")
    | Ok path ->
        Assert.Equal(@"C:\Users\user\.config\orcai\my-app.pem", path)
        Assert.True(fs.File.Exists(path), "PEM file should exist")
        Assert.Equal(pem, fs.File.ReadAllText(path))
