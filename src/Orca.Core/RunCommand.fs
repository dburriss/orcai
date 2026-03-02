module Orca.Core.RunCommand

open Orca.Core.Domain
open Orca.Core.GhClient
open Orca.Core.AuthContext

// ---------------------------------------------------------------------------
// Implements the `orca run` command.
//
// For each repository in the job config:
//   1. Creates (or finds) the GitHub Project for the org (idempotent).
//   2. Creates (or finds) an issue in the repository (idempotent).
//   3. Adds the issue to the GitHub Project.
//   4. Assigns the issue to @copilot if it has no assignees.
//
// All GitHub API calls are delegated to the IGhClient abstraction so that
// this module stays pure and testable.
// ---------------------------------------------------------------------------

/// Dependencies injected by the CLI entry point.
type RunDeps =
    { GhClient   : IGhClient
      AuthContext: IAuthContext }

/// Input parameters derived from parsed CLI arguments.
type RunInput =
    { YamlPath : string
      Verbose  : bool }

/// Execute the run command.
/// Returns a LockFile snapshot on success, or an error string.
let execute (deps: RunDeps) (input: RunInput) : Result<LockFile, string> =
    failwith "not implemented"
