module Orca.Auth.PatAuth

open System
open Orca.Core.AuthContext
open Orca.Auth.AuthConfig

// ---------------------------------------------------------------------------
// PAT (Personal Access Token) authentication.
//
// The token is stored in ~/.config/orca/auth.json as:
//   { "type": "pat", "token": "ghp_..." }
//
// On each command execution the token is read from disk and injected as
// GH_TOKEN into gh subprocess calls.
// ---------------------------------------------------------------------------

/// Store a PAT token for future use.
let storeToken (token: string) : Result<unit, string> =
    writeConfig
        { Type           = "pat"
          Token          = Some token
          AppId          = None
          KeyPath        = None
          InstallationId = None }

/// Load the previously stored PAT token.
/// Checks the ORCA_PAT environment variable first; falls back to the stored config file.
let loadToken () : Result<string, string> =
    match Environment.GetEnvironmentVariable("ORCA_PAT") |> Option.ofObj with
    | Some t when t.Length > 0 -> Ok t
    | _ ->
        readConfig ()
        |> Result.bind (fun cfg ->
            if cfg.Type <> "pat" then
                Error "Auth config is not a PAT config. Run 'orca auth pat --token <tok>' first."
            else
                match cfg.Token with
                | Some t when t.Length > 0 -> Ok t
                | _ -> Error "PAT token is missing from auth config.")

/// IAuthContext implementation backed by a stored PAT.
type PatAuthContext() =
    interface IAuthContext with
        member _.GetToken() = async {
            return loadToken()
        }
