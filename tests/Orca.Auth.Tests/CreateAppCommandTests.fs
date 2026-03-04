module Orca.Auth.Tests.CreateAppCommandTests

open System.Text.Json
open Xunit
open Orca.Auth.CreateAppCommand

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
  "name": "Orca App",
  "slug": "orca-app",
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
        Assert.Equal("Orca App",   app.Name)
        Assert.Equal("orca-app",   app.Slug)
        Assert.Contains("PRIVATE", app.Pem)
        Assert.Equal(Some "abc123secret", app.WebhookSecret)

[<Fact>]
let ``parseConversionResponse falls back to name when slug is absent`` () =
    let json = """{"id":1,"name":"orca","pem":"key","webhook_secret":"s"}"""
    match parseConversionResponse json with
    | Error e -> Assert.Fail($"Expected Ok but got Error: {e}")
    | Ok app  -> Assert.Equal("orca", app.Slug)

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
