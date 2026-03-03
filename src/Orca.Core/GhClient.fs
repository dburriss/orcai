module Orca.Core.GhClient

// ---------------------------------------------------------------------------
// Abstraction over the `gh` CLI subprocess.
//
// Defined here in Orca.Core so command modules can depend on the interface
// without creating a circular reference. The production implementation lives
// in Orca.GitHub.GhClient.
// ---------------------------------------------------------------------------

open Orca.Core.Domain

/// Contract for all GitHub operations used by command modules.
type IGhClient =
    // Projects
    abstract FindProject      : org:OrgName -> title:string -> Async<ProjectInfo option>
    abstract CreateProject    : org:OrgName -> title:string -> Async<Result<ProjectInfo, string>>
    abstract DeleteProject    : project:ProjectInfo         -> Async<Result<unit, string>>

    // Labels
    abstract ListLabels  : repo:RepoName -> Async<Result<string list, string>>
    abstract CreateLabel : repo:RepoName -> name:string -> Async<Result<unit, string>>

    // Issues
    abstract FindIssue        : repo:RepoName -> title:string -> Async<IssueRef option>
    abstract CreateIssue      : repo:RepoName -> title:string -> body:string -> labels:string list -> Async<Result<IssueRef, string>>
    abstract CloseIssue       : repo:RepoName -> issue:IssueNumber           -> Async<Result<unit, string>>
    abstract AddIssueToProject: project:ProjectInfo -> issue:IssueRef        -> Async<Result<unit, string>>
    abstract AssignIssue      : repo:RepoName -> issue:IssueNumber -> assignee:string -> Async<Result<unit, string>>

    // Pull requests
    abstract FindPrsForIssue  : repo:RepoName -> issue:IssueNumber -> Async<PullRequestRef list>
    abstract ClosePr          : repo:RepoName -> pr:PrNumber        -> Async<Result<unit, string>>
