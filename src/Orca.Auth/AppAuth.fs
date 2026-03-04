module Orca.Auth.AppAuth

open System
open System.IO
open System.Net.Http
open System.Net.Http.Headers
open System.Text.Json
open Microsoft.IdentityModel.Tokens
open System.IdentityModel.Tokens.Jwt
open Orca.Core.AuthContext
open Orca.Auth.AuthConfig

// ---------------------------------------------------------------------------
// GitHub App authentication.
//
// Flow:
//   1. Load the App ID, private key path, and installation ID from config.
//   2. Generate a JWT signed with RS256, valid for ≤10 minutes.
//   3. Exchange the JWT for an installation token via:
//        POST /app/installations/{installation_id}/access_tokens
//   4. Return the installation token as the active GH_TOKEN.
//
// The JWT is generated using System.IdentityModel.Tokens.Jwt.
// The token exchange is a direct HTTPS call (not via gh CLI) because
// gh does not natively support GitHub App auth.
// ---------------------------------------------------------------------------

type AppAuthConfig =
    { AppId          : string
      PrivateKeyPath : string
      InstallationId : string }

/// Store GitHub App auth configuration for future use.
let storeConfig (config: AppAuthConfig) : Result<unit, string> =
    writeConfig
        { Type           = "app"
          Token          = None
          AppId          = Some config.AppId
          KeyPath        = Some config.PrivateKeyPath
          InstallationId = Some config.InstallationId }

/// Store GitHub App auth configuration under a given home directory (useful for testing).
let storeConfigTo (homeDir: string) (config: AppAuthConfig) : Result<unit, string> =
    writeConfigTo homeDir
        { Type           = "app"
          Token          = None
          AppId          = Some config.AppId
          KeyPath        = Some config.PrivateKeyPath
          InstallationId = Some config.InstallationId }

/// Load the previously stored App auth configuration.
let loadConfig () : Result<AppAuthConfig, string> =
    readConfig ()
    |> Result.bind (fun cfg ->
        if cfg.Type <> "app" then
            Error "Auth config is not an App config. Run 'orca auth app ...' first."
        else
            match cfg.AppId, cfg.KeyPath, cfg.InstallationId with
            | Some appId, Some keyPath, Some installId ->
                Ok { AppId = appId; PrivateKeyPath = keyPath; InstallationId = installId }
            | _ ->
                Error "App auth config is incomplete. Re-run 'orca auth app ...'.")

/// Load the App auth configuration from a given home directory (useful for testing).
let loadConfigFrom (homeDir: string) : Result<AppAuthConfig, string> =
    readConfigFrom homeDir
    |> Result.bind (fun cfg ->
        if cfg.Type <> "app" then
            Error "Auth config is not an App config. Run 'orca auth app ...' first."
        else
            match cfg.AppId, cfg.KeyPath, cfg.InstallationId with
            | Some appId, Some keyPath, Some installId ->
                Ok { AppId = appId; PrivateKeyPath = keyPath; InstallationId = installId }
            | _ ->
                Error "App auth config is incomplete. Re-run 'orca auth app ...'.") 

/// Resolve the App auth configuration, overlaying a `getEnv` lookup on top of any stored
/// file config. The following env var names are checked (each silently overrides the
/// corresponding stored value when present and non-empty):
///
///   ORCA_APP_ID               — GitHub App ID
///   ORCA_APP_INSTALLATION_ID  — Installation ID for the target organisation
///   ORCA_APP_KEY_PATH         — Path to the PEM private key file
///
/// Note: ORCA_APP_PRIVATE_KEY (raw PEM content) is handled separately in
/// AppAuthContext.GetToken and takes precedence over ORCA_APP_KEY_PATH.
///
/// Pass `Environment.GetEnvironmentVariable >> Option.ofObj` for production.
let resolveConfigWith (getEnv: string -> string option) (loadCfg: unit -> Result<AppAuthConfig, string>) : Result<AppAuthConfig, string> =
    let env name =
        getEnv name |> Option.bind (fun s -> if s.Length > 0 then Some s else None)
    let base_ =
        match loadCfg () with
        | Ok cfg  -> cfg
        | Error _ -> { AppId = ""; PrivateKeyPath = ""; InstallationId = "" }
    let appId     = env "ORCA_APP_ID"              |> Option.defaultValue base_.AppId
    let installId = env "ORCA_APP_INSTALLATION_ID" |> Option.defaultValue base_.InstallationId
    let keyPath   = env "ORCA_APP_KEY_PATH"         |> Option.defaultValue base_.PrivateKeyPath
    match appId, installId, keyPath with
    | "", _, _ -> Error "App ID is missing. Set ORCA_APP_ID or run 'orca auth app ...'."
    | _, "", _ -> Error "Installation ID is missing. Set ORCA_APP_INSTALLATION_ID or run 'orca auth app ...'."
    | _, _, "" -> Error "Private key is missing. Set ORCA_APP_KEY_PATH / ORCA_APP_PRIVATE_KEY or run 'orca auth app ...'."
    | _        -> Ok { AppId = appId; PrivateKeyPath = keyPath; InstallationId = installId }

/// Resolve the App auth configuration, overlaying environment variables on top of
/// any stored file config. The following env vars are checked (each silently
/// overrides the corresponding stored value when set):
///
///   ORCA_APP_ID               — GitHub App ID
///   ORCA_APP_INSTALLATION_ID  — Installation ID for the target organisation
///   ORCA_APP_KEY_PATH         — Path to the PEM private key file
///
/// Note: ORCA_APP_PRIVATE_KEY (raw PEM content) is handled separately in
/// AppAuthContext.GetToken and takes precedence over ORCA_APP_KEY_PATH.
let resolveConfig () : Result<AppAuthConfig, string> =
    resolveConfigWith (Environment.GetEnvironmentVariable >> Option.ofObj) loadConfig

/// Generate a signed RS256 JWT for the given App ID, PEM-encoded private key, and
/// reference time. Pure: no I/O, no env var access.
/// Pass `DateTimeOffset.UtcNow` for production.
let generateJwtAt (now: DateTimeOffset) (appId: string) (privateKeyPem: string) : Result<string, string> =
    try
        // Strip PEM headers/footers and decode the base64 key bytes.
        let pemBody =
            privateKeyPem
                .Replace("-----BEGIN RSA PRIVATE KEY-----", "")
                .Replace("-----END RSA PRIVATE KEY-----",   "")
                .Replace("-----BEGIN PRIVATE KEY-----",     "")
                .Replace("-----END PRIVATE KEY-----",       "")
                .Replace("\r", "")
                .Replace("\n", "")
                .Trim()
        let keyBytes = Convert.FromBase64String(pemBody)
        let rsa = System.Security.Cryptography.RSA.Create()
        // Try PKCS#8 first, fall back to PKCS#1.
        try
            rsa.ImportPkcs8PrivateKey(keyBytes, ref 0) |> ignore
        with _ ->
            rsa.ImportRSAPrivateKey(keyBytes, ref 0) |> ignore

        let securityKey   = RsaSecurityKey(rsa)
        let credentials   = SigningCredentials(securityKey, SecurityAlgorithms.RsaSha256)

        let issuedAt  = now.AddSeconds(-60.0)   // 60s in the past to allow clock skew
        let expiresAt = now.AddMinutes(10.0)     // GitHub allows up to 10 minutes

        let descriptor =
            SecurityTokenDescriptor(
                Issuer             = appId,
                IssuedAt           = Nullable(issuedAt.UtcDateTime),
                Expires            = Nullable(expiresAt.UtcDateTime),
                SigningCredentials = credentials)

        let handler = JwtSecurityTokenHandler()
        let token   = handler.CreateToken(descriptor)
        Ok (handler.WriteToken(token))
    with ex ->
        Error $"Failed to generate JWT: {ex.Message}"

/// Generate a signed RS256 JWT for the given App ID and PEM-encoded private key.
let generateJwt (appId: string) (privateKeyPem: string) : Result<string, string> =
    generateJwtAt DateTimeOffset.UtcNow appId privateKeyPem

/// Exchange a JWT for a GitHub App installation token.
let exchangeForInstallationToken (jwt: string) (installationId: string) : Async<Result<string, string>> =
    async {
        use client = new HttpClient()
        client.DefaultRequestHeaders.UserAgent.Add(ProductInfoHeaderValue("orca-cli", "1.0"))
        client.DefaultRequestHeaders.Authorization <- AuthenticationHeaderValue("Bearer", jwt)
        client.DefaultRequestHeaders.Accept.Add(MediaTypeWithQualityHeaderValue("application/vnd.github+json"))
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28")

        let url = $"https://api.github.com/app/installations/{installationId}/access_tokens"
        let content = new StringContent("{}", Text.Encoding.UTF8, "application/json")
        let! response = client.PostAsync(url, content) |> Async.AwaitTask
        let! body     = response.Content.ReadAsStringAsync() |> Async.AwaitTask

        if not response.IsSuccessStatusCode then
            return Error $"GitHub API returned {int response.StatusCode}: {body}"
        else
            try
                let doc   = JsonDocument.Parse(body)
                let token = doc.RootElement.GetProperty("token").GetString() |> Option.ofObj
                match token with
                | None   -> return Error "Installation token response did not contain a 'token' field."
                | Some t -> return Ok t
            with ex ->
                return Error $"Failed to parse installation token response: {ex.Message}"
    }

/// IAuthContext implementation backed by a GitHub App.
/// `getEnv` is used to check ORCA_APP_PRIVATE_KEY for raw PEM content first;
/// falls back to reading config.PrivateKeyPath via `readFile`.
/// Pass `Environment.GetEnvironmentVariable >> Option.ofObj` and `File.ReadAllText`
/// for production.
type AppAuthContext(config: AppAuthConfig, getEnv: string -> string option, readFile: string -> string) =
    new(config: AppAuthConfig) =
        AppAuthContext(
            config,
            (System.Environment.GetEnvironmentVariable >> Option.ofObj),
            System.IO.File.ReadAllText)
    interface IAuthContext with
        member _.GetToken() = async {
            let pemResult =
                match getEnv "ORCA_APP_PRIVATE_KEY" |> Option.bind (fun s -> if s.Length > 0 then Some s else None) with
                | Some pem -> Ok pem
                | None     ->
                    try Ok (readFile config.PrivateKeyPath)
                    with ex -> Error $"Failed to read private key file '{config.PrivateKeyPath}': {ex.Message}"
            match pemResult with
            | Error e -> return Error e
            | Ok pem  ->
                match generateJwt config.AppId pem with
                | Error e -> return Error e
                | Ok jwt  -> return! exchangeForInstallationToken jwt config.InstallationId
        }
