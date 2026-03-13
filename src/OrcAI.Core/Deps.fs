module OrcAI.Core.Deps

open OrcAI.Core.GhClient
open OrcAI.Core.AuthContext
open OrcAI.Core.OrcAIConfig
open System.IO.Abstractions

// ---------------------------------------------------------------------------
// Shared dependencies record injected into all command modules.
// ---------------------------------------------------------------------------

/// Dependencies injected by the CLI entry point into every command.
type OrcAIDeps =
    { GhClient   : IGhClient
      AuthContext: IAuthContext
      FileSystem : IFileSystem
      Config     : OrcAIConfig }
