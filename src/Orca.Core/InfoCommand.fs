module Orca.Core.InfoCommand

open Orca.Core.Domain
open Orca.Core.GhClient
open Orca.Core.AuthContext

// ---------------------------------------------------------------------------
// Implements the `orca info` command.
//
// Displays the current state of a job:
//   - By default reads from the lock file if one exists.
//   - With --no-lock, fetches live state from GitHub.
//   - With --save-lock, writes a new lock file after fetching live state.
// ---------------------------------------------------------------------------

/// Dependencies injected by the CLI entry point.
type InfoDeps =
    { GhClient   : IGhClient
      AuthContext: IAuthContext }

/// Input parameters derived from parsed CLI arguments.
type InfoInput =
    { YamlPath : string
      NoLock   : bool
      SaveLock : bool }

/// The result returned to the CLI for display.
type InfoResult =
    { Lock   : LockFile
      Source : InfoSource }

and InfoSource = | FromLockFile | FromGitHub

/// Execute the info command.
/// Returns an InfoResult on success, or an error string.
let execute (deps: InfoDeps) (input: InfoInput) : Result<InfoResult, string> =
    failwith "not implemented"
