module Orca.Core.CleanupCommand

open Orca.Core.Domain
open Orca.Core.GhClient
open Orca.Core.AuthContext

// ---------------------------------------------------------------------------
// Implements the `orca cleanup` command.
//
// For the job described in the YAML config (or its lock file):
//   1. Closes any open PRs that reference the managed issues.
//   2. Deletes the managed issues from each repository.
//   3. Deletes the GitHub Project.
//
// When --dryrun is set, the command lists what would be deleted but makes
// no API calls.
// ---------------------------------------------------------------------------

/// Dependencies injected by the CLI entry point.
type CleanupDeps =
    { GhClient   : IGhClient
      AuthContext: IAuthContext }

/// Input parameters derived from parsed CLI arguments.
type CleanupInput =
    { YamlPath : string
      DryRun   : bool }

/// Execute the cleanup command.
/// Returns unit on success, or an error string.
let execute (deps: CleanupDeps) (input: CleanupInput) : Result<unit, string> =
    failwith "not implemented"
