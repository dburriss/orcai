module Orca.Auth.CreateAppCommand

open System
open System.IO
open System.Net
open System.Net.Http
open System.Net.Http.Headers
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Orca.Auth.AuthConfig

// ---------------------------------------------------------------------------
// GitHub App Manifest flow.
//
// Flow:
//   1. Build a hardcoded manifest (permissions required by Orca).
//   2. Start a temporary HttpListener on localhost:<port>.
//      - GET /        → serve an auto-submitting HTML form that POSTs the
//                        manifest to https://github.com/settings/apps/new
//                        (or the org variant).
//      - GET /callback → capture the ?code= query parameter, serve a
//                        "you may close this tab" page, signal completion.
//   3. Open the default browser to http://localhost:<port>/.
//   4. Wait (up to 120 s) for GitHub to redirect back with the code.
//   5. Exchange the code for app credentials via the GitHub API.
//   6. Save the PEM private key to ~/.config/orca/app.pem.
//   7. Write auth.json with type=app, appId, keyPath (installationId left
//      empty until the user installs the app and provides it).
// ---------------------------------------------------------------------------

// ---------------------------------------------------------------------------
// Types
// ---------------------------------------------------------------------------

type CreateAppInput =
    { AppName : string
      Org     : string option
      Port    : int }

type CreatedApp =
    { Id            : string
      Name          : string
      PemPath       : string
      WebhookSecret : string option }

// ---------------------------------------------------------------------------
// Pure helpers (testable without I/O)
// ---------------------------------------------------------------------------

/// Build the JSON manifest string for the Orca GitHub App.
/// Pure: no I/O, no side effects.
let buildManifest (appName: string) (redirectUrl: string) : string =
    let manifest =
        $"""{{
  "name": "{appName}",
  "url": "https://github.com/dburriss/orca",
  "description": "Orca automation app for creating GitHub Projects and issues at scale.",
  "public": false,
  "default_permissions": {{
    "issues": "write",
    "pull_requests": "read",
    "metadata": "read",
    "repository_projects": "write"
  }},
  "hook_attributes": {{
    "url": "https://example.com",
    "active": false
  }},
  "default_events": [],
  "redirect_url": "{redirectUrl}"
}}"""
    manifest

/// Build the GitHub app-registration URL for a personal account or org.
/// Pure.
let buildGitHubUrl (org: string option) (state: string) : string =
    match org with
    | None      -> $"https://github.com/settings/apps/new?state={state}"
    | Some org  -> $"https://github.com/organizations/{org}/settings/apps/new?state={state}"

/// Build the HTML page that auto-submits the manifest to GitHub.
/// Pure.
let buildFormPage (githubUrl: string) (manifest: string) : string =
    // Escape the manifest for embedding in an HTML attribute value.
    let escaped =
        manifest
            .Replace("&", "&amp;")
            .Replace("\"", "&quot;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
    $"""<!DOCTYPE html>
<html>
<head><title>Registering Orca GitHub App...</title></head>
<body>
<p>Redirecting to GitHub to register your app&hellip;</p>
<form id="f" action="{githubUrl}" method="post">
  <input type="hidden" name="manifest" value="{escaped}">
</form>
<script>document.getElementById('f').submit();</script>
</body>
</html>"""

/// Parse the app ID, PEM, name, and webhook secret from the GitHub manifest
/// conversion API response JSON.
/// Pure.
let parseConversionResponse (json: string) : Result<{| Id: string; Name: string; Slug: string; Pem: string; WebhookSecret: string option |}, string> =
    try
        let doc = JsonDocument.Parse(json)
        let root = doc.RootElement
        let str (name: string) =
            match root.TryGetProperty(name) with
            | true, el -> el.GetString() |> Option.ofObj
            | _        -> None
        let idNum =
            match root.TryGetProperty("id") with
            | true, el ->
                match el.ValueKind with
                | JsonValueKind.Number -> Some (string (el.GetInt64()))
                | JsonValueKind.String -> el.GetString() |> Option.ofObj
                | _                   -> None
            | _ -> None
        let webhookSecret =
            match root.TryGetProperty("webhook_secret") with
            | true, el when el.ValueKind = JsonValueKind.String -> el.GetString() |> Option.ofObj
            | _ -> None
        match idNum, str "name", str "pem" with
        | Some id, Some name, Some pem ->
            let slug = str "slug" |> Option.defaultValue name
            Ok {| Id = id; Name = name; Slug = slug; Pem = pem; WebhookSecret = webhookSecret |}
        | None, _, _ -> Error $"Missing 'id' in conversion response: {json}"
        | _, None, _ -> Error $"Missing 'name' in conversion response: {json}"
        | _, _, None -> Error $"Missing 'pem' in conversion response: {json}"
    with ex ->
        Error $"Failed to parse conversion response: {ex.Message}"

// ---------------------------------------------------------------------------
// HTTP: exchange the temporary code for app credentials
// ---------------------------------------------------------------------------

let exchangeCode (code: string) : Async<Result<string, string>> =
    async {
        use client = new HttpClient()
        client.DefaultRequestHeaders.UserAgent.Add(ProductInfoHeaderValue("orca-cli", "1.0"))
        client.DefaultRequestHeaders.Accept.Add(MediaTypeWithQualityHeaderValue("application/vnd.github+json"))
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28")

        let url = $"https://api.github.com/app-manifests/{code}/conversions"
        let content = new StringContent("{}", Encoding.UTF8, "application/json")
        let! response = client.PostAsync(url, content) |> Async.AwaitTask
        let! body     = response.Content.ReadAsStringAsync() |> Async.AwaitTask

        if not response.IsSuccessStatusCode then
            return Error $"GitHub API returned {int response.StatusCode}: {body}"
        else
            return Ok body
    }

// ---------------------------------------------------------------------------
// Local HTTP listener
// ---------------------------------------------------------------------------

/// Serve one HTTP request and write a plain text/html response.
let private reply (ctx: HttpListenerContext) (statusCode: int) (contentType: string) (body: string) =
    let bytes = Encoding.UTF8.GetBytes(body)
    ctx.Response.StatusCode      <- statusCode
    ctx.Response.ContentType     <- contentType
    ctx.Response.ContentLength64 <- int64 bytes.Length
    ctx.Response.OutputStream.Write(bytes, 0, bytes.Length)
    ctx.Response.OutputStream.Close()

/// Run the local HTTP listener. Blocks until the callback code is received or
/// the cancellation token fires. Returns Ok code or Error message.
let runListener
        (port     : int)
        (formHtml : string)
        (ct       : CancellationToken)
    : Async<Result<string, string>> =
    async {
        let prefix = $"http://localhost:{port}/"
        let listener = new HttpListener()
        listener.Prefixes.Add(prefix)
        let startResult =
            try
                listener.Start()
                Ok ()
            with ex ->
                Error $"Could not start local HTTP listener on port {port}: {ex.Message}"

        match startResult with
        | Error e -> return Error e
        | Ok () ->

        let tcs = TaskCompletionSource<Result<string, string>>()

        use _ = ct.Register(fun () ->
            tcs.TrySetResult(Error "Timed out waiting for GitHub callback.") |> ignore)

        // Process requests on a background thread until we get the callback.
        let rec loop () =
            async {
                if tcs.Task.IsCompleted then
                    ()
                else
                    let! ctx =
                        listener.GetContextAsync()
                        |> Async.AwaitTask
                    let path =
                        match ctx.Request.Url with
                        | null -> ""
                        | url  -> url.AbsolutePath
                    if path = "/" || path = "" then
                        reply ctx 200 "text/html; charset=utf-8" formHtml
                        return! loop ()
                    elif path.StartsWith("/callback") then
                        let code = ctx.Request.QueryString.["code"]
                        if String.IsNullOrWhiteSpace(code) then
                            reply ctx 400 "text/plain" "Missing 'code' parameter."
                            tcs.TrySetResult(Error "GitHub did not send a 'code' in the callback URL.") |> ignore
                        else
                            let successHtml =
                                """<!DOCTYPE html><html><head><title>Done</title></head>
<body><h2>GitHub App registered successfully.</h2>
<p>You may close this tab and return to your terminal.</p></body></html>"""
                            reply ctx 200 "text/html; charset=utf-8" successHtml
                            tcs.TrySetResult(Ok (code |> Option.ofObj |> Option.defaultValue "")) |> ignore
                    else
                        reply ctx 404 "text/plain" "Not found."
                        return! loop ()
            }

        Async.Start(loop (), ct)

        let! result = tcs.Task |> Async.AwaitTask
        listener.Stop()
        (listener :> IDisposable).Dispose()
        return result
    }

// ---------------------------------------------------------------------------
// Save PEM to disk
// ---------------------------------------------------------------------------

let private pemPath () =
    let home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
    Path.Combine(home, ".config", "orca", "app.pem")

let savePem (pem: string) : Result<string, string> =
    try
        let path = pemPath ()
        let dir  = Path.GetDirectoryName(path) |> Option.ofObj |> Option.defaultValue "."
        Directory.CreateDirectory(dir) |> ignore
        File.WriteAllText(path, pem)
        Ok path
    with ex ->
        Error $"Failed to save private key: {ex.Message}"

// ---------------------------------------------------------------------------
// Store credentials in auth.json (without installationId)
// ---------------------------------------------------------------------------

let storeAppCredentials (appId: string) (keyPath: string) : Result<unit, string> =
    writeConfig
        { Type           = "app"
          Token          = None
          AppId          = Some appId
          KeyPath        = Some keyPath
          InstallationId = None }

// ---------------------------------------------------------------------------
// Build the GitHub App permissions settings URL
// ---------------------------------------------------------------------------

/// Returns the URL where the user can edit the app's permissions to add
/// the organisation-level "Projects" permission that cannot be set via the
/// manifest flow.
/// Pure.
let buildPermissionsUrl (org: string option) (appSlug: string) : string =
    match org with
    | None      -> $"https://github.com/settings/apps/{appSlug}/permissions"
    | Some org  -> $"https://github.com/organizations/{org}/settings/apps/{appSlug}/permissions"

// ---------------------------------------------------------------------------
// Open browser (cross-platform)
// ---------------------------------------------------------------------------

let openBrowser (url: string) : unit =
    try
        System.Diagnostics.Process.Start(
            System.Diagnostics.ProcessStartInfo(url, UseShellExecute = true))
        |> ignore
    with _ ->
        // Non-fatal — user can open manually.
        ()

// ---------------------------------------------------------------------------
// Main execute function
// ---------------------------------------------------------------------------

let execute (input: CreateAppInput) : Async<Result<CreatedApp, string>> =
    async {
        let port        = input.Port
        let appName     = input.AppName
        let callbackUrl = $"http://localhost:{port}/callback"
        let state       = Guid.NewGuid().ToString("N").[..7]   // short random state

        let manifest    = buildManifest appName callbackUrl
        let githubUrl   = buildGitHubUrl input.Org state
        let formHtml    = buildFormPage githubUrl manifest

        use cts = new CancellationTokenSource(TimeSpan.FromSeconds(120.0))

        printfn "Opening browser to register the GitHub App..."
        printfn "If the browser does not open, navigate to: http://localhost:%d/" port

        openBrowser $"http://localhost:{port}/"

        match! runListener port formHtml cts.Token with
        | Error e -> return Error e
        | Ok code ->

        printfn "Received callback from GitHub. Exchanging code for credentials..."

        match! exchangeCode code with
        | Error e -> return Error e
        | Ok json ->

        match parseConversionResponse json with
        | Error e -> return Error e
        | Ok app  ->

        match savePem app.Pem with
        | Error e -> return Error e
        | Ok pemPath ->

        match storeAppCredentials app.Id pemPath with
        | Error e -> return Error e
        | Ok ()   ->

        let permissionsUrl = buildPermissionsUrl input.Org app.Slug

        printfn ""
        printfn "ACTION REQUIRED: Grant organisation project permissions"
        printfn "--------------------------------------------------------"
        printfn "The GitHub App manifest flow cannot set organisation-level"
        printfn "permissions. You must grant them manually:"
        printfn ""
        printfn "  1. The GitHub App permissions page will open in your browser."
        printfn "  2. Scroll to 'Organization permissions'."
        printfn "  3. Set 'Projects' to 'Read and write'."
        printfn "  4. Click 'Save changes' and confirm."
        printfn ""
        printfn "  URL: %s" permissionsUrl
        printfn ""

        openBrowser permissionsUrl

        return Ok
            { Id            = app.Id
              Name          = app.Name
              PemPath       = pemPath
              WebhookSecret = app.WebhookSecret }
    }
