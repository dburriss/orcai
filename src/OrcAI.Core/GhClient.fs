module OrcAI.Core.GhClient

// ---------------------------------------------------------------------------
// Abstraction over the `gh` CLI subprocess.
//
// Defined here in OrcAI.Core so command modules can depend on the interface
// without creating a circular reference. The production implementation lives
// in OrcAI.GitHub.GhClient.
// ---------------------------------------------------------------------------

open OrcAI.Core.Domain

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
    abstract FindClosedIssue  : repo:RepoName -> title:string -> Async<IssueRef option>
    abstract ReopenIssue      : repo:RepoName -> issue:IssueNumber -> Async<Result<IssueRef, string>>
    abstract CreateIssue      : repo:RepoName -> title:string -> body:string -> labels:string list -> Async<Result<IssueRef, string>>
    abstract UpdateIssue      : repo:RepoName -> issue:IssueNumber -> title:string -> body:string  -> Async<Result<unit, string>>
    abstract DeleteIssue      : repo:RepoName -> issue:IssueNumber           -> Async<Result<unit, string>>
    abstract AddIssueToProject: project:ProjectInfo -> issue:IssueRef        -> Async<Result<unit, string>>
    abstract AssignIssue      : repo:RepoName -> issue:IssueNumber -> assignee:string -> Async<Result<unit, string>>
    abstract UnassignIssue    : repo:RepoName -> issue:IssueNumber -> assignee:string -> Async<Result<unit, string>>
    abstract PostComment      : repo:RepoName -> issue:IssueNumber -> body:string    -> Async<Result<unit, string>>

    // Pull requests
    abstract FindPrsForIssue  : repo:RepoName -> issue:IssueNumber -> Async<PullRequestRef list>
    abstract ClosePr          : repo:RepoName -> pr:PrNumber        -> Async<Result<unit, string>>
    abstract GetPrState       : repo:RepoName -> pr:PrNumber        -> Async<string option>

    // State
    abstract GetIssueState    : repo:RepoName -> issue:IssueNumber  -> Async<string option>

    // Repos
    abstract ListRepos        : org:OrgName -> Async<Result<string list, string>>
    abstract RepoExists       : repo:RepoName -> Async<Result<unit, string>>
    abstract IsArchived       : repo:RepoName -> Async<Result<bool, string>>
    abstract FetchCodeowners  : repo:RepoName -> Async<string option>
