module Orca.GitHub.GhClient

// ---------------------------------------------------------------------------
// Production implementation of Orca.Core.GhClient.IGhClient.
//
// All GitHub API calls are delegated to the `gh` CLI via SimpleExec.
// GH_TOKEN is injected into each subprocess environment by the caller
// (resolved from IAuthContext before construction).
// ---------------------------------------------------------------------------

open Orca.Core.Domain
open Orca.Core.GhClient

/// Production implementation that shells out to `gh` via SimpleExec.
type GhCliClient(ghToken: string) =
    interface IGhClient with
        member _.FindProject org title           = failwith "not implemented"
        member _.CreateProject org title         = failwith "not implemented"
        member _.DeleteProject project           = failwith "not implemented"
        member _.FindIssue repo title            = failwith "not implemented"
        member _.CreateIssue repo title body     = failwith "not implemented"
        member _.CloseIssue repo issue           = failwith "not implemented"
        member _.AddIssueToProject project issue = failwith "not implemented"
        member _.AssignIssue repo issue assignee = failwith "not implemented"
        member _.FindPrsForIssue repo issue      = failwith "not implemented"
        member _.ClosePr repo pr                 = failwith "not implemented"
