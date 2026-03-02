module Orca.Auth.AppAuth

open Orca.Core.AuthContext

// ---------------------------------------------------------------------------
// GitHub App authentication.
//
// Flow:
//   1. Load the App ID and private key (PEM file or env var).
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
      PrivateKeyPath : string }

/// Store GitHub App auth configuration for future use.
let storeConfig (config: AppAuthConfig) : Result<unit, string> =
    failwith "not implemented"

/// Load the previously stored App auth configuration.
let loadConfig () : Result<AppAuthConfig, string> =
    failwith "not implemented"

/// Generate a signed JWT for the given App ID and private key PEM content.
let generateJwt (appId: string) (privateKeyPem: string) : Result<string, string> =
    failwith "not implemented"

/// Exchange a JWT for a GitHub App installation token.
let exchangeForInstallationToken (jwt: string) : Async<Result<string, string>> =
    failwith "not implemented"

/// IAuthContext implementation backed by a GitHub App.
type AppAuthContext(config: AppAuthConfig) =
    interface IAuthContext with
        member _.GetToken() = async {
            match loadConfig() with
            | Error e -> return Error e
            | Ok cfg  ->
                let pemContent = System.IO.File.ReadAllText(cfg.PrivateKeyPath)
                match generateJwt cfg.AppId pemContent with
                | Error e   -> return Error e
                | Ok jwt    -> return! exchangeForInstallationToken jwt
        }
