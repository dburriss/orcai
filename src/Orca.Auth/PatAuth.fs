module Orca.Auth.PatAuth

open Orca.Core.AuthContext

// ---------------------------------------------------------------------------
// PAT (Personal Access Token) authentication.
//
// The token is stored securely on disk (platform keychain or encrypted file)
// and retrieved on each command execution.
// ---------------------------------------------------------------------------

/// Store a PAT token for future use.
let storeToken (token: string) : Result<unit, string> =
    failwith "not implemented"

/// Load the previously stored PAT token.
let loadToken () : Result<string, string> =
    failwith "not implemented"

/// IAuthContext implementation backed by a stored PAT.
type PatAuthContext() =
    interface IAuthContext with
        member _.GetToken() = async {
            return loadToken()
        }
