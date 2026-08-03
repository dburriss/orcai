module OrcAI.Core.Deps

open OrcAI.Core.Domain
open OrcAI.Core.Provider
open OrcAI.Core.AuthContext
open OrcAI.Core.OrcAIConfig
open System.IO.Abstractions

// ---------------------------------------------------------------------------
// Shared dependencies record injected into all command modules.
// ---------------------------------------------------------------------------

/// Dependencies injected by the CLI entry point into every command.
type OrcAIDeps =
    { /// Resolves the provider bundle for a given job config. Token resolution is
      /// deferred to first use (via a Lazy closed over by the CLI entry point).
      ResolveProvider : JobConfig -> Result<ProviderClients, string>
      AuthContext     : IAuthContext
      FileSystem      : IFileSystem
      Config          : OrcAIConfig }
