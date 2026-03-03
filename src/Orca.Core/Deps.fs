module Orca.Core.Deps

open Orca.Core.GhClient
open Orca.Core.AuthContext

// ---------------------------------------------------------------------------
// Shared dependencies record injected into all command modules.
// ---------------------------------------------------------------------------

/// Dependencies injected by the CLI entry point into every command.
type OrcaDeps =
    { GhClient   : IGhClient
      AuthContext: IAuthContext }
