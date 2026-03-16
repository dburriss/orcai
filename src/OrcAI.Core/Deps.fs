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
    { GhClient      : IGhClient
      /// Secondary client authenticated with a PAT, used only for Copilot assignment
      /// when the primary auth is a GitHub App (Apps cannot assign @copilot).
      /// None when primary auth is already PAT-based, or when no PAT could be resolved.
      CopilotClient : IGhClient option
      AuthContext   : IAuthContext
      FileSystem    : IFileSystem
      Config        : OrcAIConfig }
