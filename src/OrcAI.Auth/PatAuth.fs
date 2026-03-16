module OrcAI.Auth.PatAuth

open System
open OrcAI.Core.AuthContext
open OrcAI.Auth.AuthConfig

// ---------------------------------------------------------------------------
// PAT (Personal Access Token) authentication.
//
// The token is stored in ~/.config/orcai/auth.json under the profile named
// "pat", which is also set as the active profile:
//   {
//     "active": "pat",
//     "profiles": {
//       "pat": { "type": "pat", "token": "ghp_..." }
//     }
//   }
//
// On each command execution the token is read from the active profile and
// injected as GH_TOKEN into gh subprocess calls.
// ---------------------------------------------------------------------------

/// Profile name used for PAT credentials.
let private patProfileName = "pat"

/// Store a PAT token for future use.
/// Written under the profile named "pat", set as active.
let storeToken (token: string) : Result<unit, string> =
    let entry : ProfileEntry =
        { Type           = "pat"
          Token          = Some token
          AppId          = None
          KeyPath        = None
          InstallationId = None }
    modifyConfig (fun cfg -> Ok (upsertProfile patProfileName entry cfg))

/// Load the previously stored PAT token.
/// `getEnv` is called with "ORCAI_PAT" first; falls back to the stored config file.
/// Pass `Environment.GetEnvironmentVariable >> Option.ofObj` for production.
let loadTokenWith (getEnv: string -> string option) (readCfg: unit -> Result<AuthConfigFile, string>) : Result<string, string> =
    match getEnv "ORCAI_PAT" |> Option.bind (fun s -> if s.Length > 0 then Some s else None) with
    | Some t -> Ok t
    | None   ->
        readCfg ()
        |> Result.bind getActiveProfile
        |> Result.bind (fun profile ->
            if profile.Type <> "pat" then
                Error "Auth config is not a PAT config. Run 'orcai auth pat --token <tok>' first."
            else
                match profile.Token with
                | Some t when t.Length > 0 -> Ok t
                | _ -> Error "PAT token is missing from auth config.")

/// Load the previously stored PAT token.
/// Checks the ORCAI_PAT environment variable first; falls back to the stored config file.
let loadToken () : Result<string, string> =
    loadTokenWith (Environment.GetEnvironmentVariable >> Option.ofObj) readConfig

/// IAuthContext implementation backed by a stored PAT.
type PatAuthContext() =
    interface IAuthContext with
        member _.GetToken() = async {
            return loadToken()
        }
