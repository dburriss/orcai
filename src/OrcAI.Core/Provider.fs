module OrcAI.Core.Provider

// ---------------------------------------------------------------------------
// Abstraction over a job tracker provider (GitHub today; Local/Jira later).
//
// Defined here in OrcAI.Core so command modules can depend on the interfaces
// without creating a circular reference. The production GitHub implementation
// lives in OrcAI.GitHub.GhClient.
//
// Split into a mandatory tracker interface every provider implements, plus
// two optional capability interfaces that only GitHub-like providers support.
// ---------------------------------------------------------------------------

open OrcAI.Core.Domain

/// Mandatory — every provider (GitHub, Local, future Jira) implements this.
type IIssueTracker =
    // Projects
    abstract FindProject      : org:OrgName -> title:string -> Async<ProjectInfo option>
    abstract CreateProject    : org:OrgName -> title:string -> Async<Result<ProjectInfo, string>>
    abstract DeleteProject    : project:ProjectInfo         -> Async<Result<unit, string>>

    // Labels
    abstract ListLabels  : repo:RepoName -> Async<Result<string list, string>>
    abstract CreateLabel : repo:RepoName -> name:string -> Async<Result<unit, string>>

    // Issues
    abstract FindIssue        : repo:RepoName -> title:string -> Async<Result<IssueRef option, string>>
    abstract FindClosedIssue  : repo:RepoName -> title:string -> Async<Result<IssueRef option, string>>
    abstract ReopenIssue      : repo:RepoName -> issue:IssueId -> Async<Result<IssueRef, string>>
    abstract CreateIssue      : repo:RepoName -> title:string -> body:string -> labels:string list -> Async<Result<IssueRef, string>>
    abstract UpdateIssue      : repo:RepoName -> issue:IssueId -> title:string -> body:string  -> Async<Result<unit, string>>
    abstract DeleteIssue      : repo:RepoName -> issue:IssueId           -> Async<Result<unit, string>>
    abstract AddIssueToProject: project:ProjectInfo -> issue:IssueRef        -> Async<Result<unit, string>>
    abstract AssignIssue      : repo:RepoName -> issue:IssueId -> assignee:string -> Async<Result<unit, string>>
    abstract UnassignIssue    : repo:RepoName -> issue:IssueId -> assignee:string -> Async<Result<unit, string>>
    abstract PostComment      : repo:RepoName -> issue:IssueId -> body:string    -> Async<Result<unit, string>>

    // State
    abstract GetIssueState    : repo:RepoName -> issue:IssueId  -> Async<string option>

/// Optional — GitHub-API PR tracking. None for providers with no PR concept.
type IPullRequestLinker =
    abstract FindPrsForIssue  : repo:RepoName -> issue:IssueId -> Async<PullRequestRef list>
    abstract ClosePr          : repo:RepoName -> pr:PrNumber        -> Async<Result<unit, string>>
    abstract GetPrState       : repo:RepoName -> pr:PrNumber        -> Async<string option>

/// Optional — GitHub-API repo metadata. None for providers with no repo concept.
type IRepoInspector =
    abstract ListRepos        : org:OrgName -> Async<Result<string list, string>>
    abstract ReposExist       : repos:RepoName list -> Async<Map<RepoName, Result<unit, string>>>
    abstract IsArchived        : repo:RepoName -> Async<Result<bool, string>>
    abstract FetchReposState   : repos:RepoName list -> title:string -> Async<Map<RepoName, Result<RepoState, string>>>
    abstract FetchCodeowners   : repo:RepoName -> Async<string option>

/// What a resolved provider offers for one job.
type ProviderClients =
    { Tracker : IIssueTracker
      Prs     : IPullRequestLinker option
      Repos   : IRepoInspector option }
