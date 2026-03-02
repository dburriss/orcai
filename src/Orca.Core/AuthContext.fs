module Orca.Core.AuthContext

// ---------------------------------------------------------------------------
// Abstraction for resolving a GH_TOKEN to pass into gh subprocess calls.
//
// Defined here in Orca.Core so command modules can depend on the interface
// without creating a circular reference. Implementations live in Orca.Auth.
// ---------------------------------------------------------------------------

/// Contract used by command modules to obtain an active GitHub token.
type IAuthContext =
    /// Resolve the current GitHub token.
    /// For PAT auth this is immediate; for App auth this may make an HTTP call.
    abstract GetToken : unit -> Async<Result<string, string>>
