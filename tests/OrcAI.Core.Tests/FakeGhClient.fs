module OrcAI.Core.Tests.FakeGhClient

open System.Collections.Concurrent
open OrcAI.Core.Domain
open OrcAI.Core.GhClient

/// Injectable handler functions for a configurable fake IGhClient.
/// Start from `defaults` or `neverCalledHandlers` and override only what the test needs.
type Handlers =
    { FindProject      : OrgName     -> string      -> Async<ProjectInfo option>
      CreateProject    : OrgName     -> string      -> Async<Result<ProjectInfo, string>>
      DeleteProject    : ProjectInfo               -> Async<Result<unit, string>>
      ListLabels       : RepoName                  -> Async<Result<string list, string>>
      CreateLabel      : RepoName    -> string      -> Async<Result<unit, string>>
      FindIssue        : RepoName    -> string      -> Async<IssueRef option>
      FindClosedIssue  : RepoName    -> string      -> Async<IssueRef option>
      ReopenIssue      : RepoName    -> IssueNumber -> Async<Result<IssueRef, string>>
      CreateIssue      : RepoName    -> string -> string -> string list -> Async<Result<IssueRef, string>>
      DeleteIssue      : RepoName    -> IssueNumber -> Async<Result<unit, string>>
      AddIssueToProject: ProjectInfo -> IssueRef    -> Async<Result<unit, string>>
      AssignIssue      : RepoName    -> IssueNumber -> string -> Async<Result<unit, string>>
      UnassignIssue    : RepoName    -> IssueNumber -> string -> Async<Result<unit, string>>
      FindPrsForIssue  : RepoName    -> IssueNumber -> Async<PullRequestRef list>
      ClosePr          : RepoName    -> PrNumber    -> Async<Result<unit, string>>
      ListRepos        : OrgName                   -> Async<Result<string list, string>>
      RepoExists       : RepoName                  -> Async<Result<unit, string>> }

/// Returns a default-shaped IssueRef for repo + issue number.
let issueFor (repo: RepoName) num : IssueRef =
    let (RepoName r) = repo
    { Repo = repo; Number = IssueNumber num
      Url  = $"https://github.com/{r}/issues/{num}"; Assignees = [] }

let private defaultProject () =
    { Org = OrgName "myorg"; Number = 1; Title = "My Project"
      Url = "https://github.com/orgs/myorg/projects/1" }

/// Happy-path defaults: project and issue operations succeed; FindIssue/FindClosedIssue
/// return None (triggers fresh creation). Destructive/side-effectful methods throw.
let defaults : Handlers =
    { FindProject       = fun _ _        -> async { return Some (defaultProject ()) }
      CreateProject     = fun _ _        -> async { return Ok (defaultProject ()) }
      DeleteProject     = fun _          -> async { return failwith "DeleteProject not expected" }
      ListLabels        = fun _          -> async { return Ok [] }
      CreateLabel       = fun _ _        -> async { return Ok () }
      FindIssue         = fun _ _        -> async { return None }
      FindClosedIssue   = fun _ _        -> async { return None }
      ReopenIssue       = fun _ _        -> async { return failwith "ReopenIssue not expected" }
      CreateIssue       = fun repo _ _ _ -> async { return Ok (issueFor repo 42) }
      DeleteIssue       = fun _ _        -> async { return failwith "DeleteIssue not expected" }
      AddIssueToProject = fun _ _        -> async { return Ok () }
      AssignIssue       = fun _ _ _      -> async { return Ok () }
      UnassignIssue     = fun _ _ _      -> async { return Ok () }
      FindPrsForIssue   = fun _ _        -> async { return failwith "FindPrsForIssue not expected" }
      ClosePr           = fun _ _        -> async { return failwith "ClosePr not expected" }
      ListRepos         = fun _          -> async { return failwith "ListRepos not expected" }
      RepoExists        = fun _          -> async { return Ok () } }

/// Handlers where every method throws — use for tests that assert GitHub is never called.
let neverCalledHandlers : Handlers =
    { defaults with
        FindProject       = fun _ _        -> async { return failwith "GhClient should not be called" }
        CreateProject     = fun _ _        -> async { return failwith "GhClient should not be called" }
        ListLabels        = fun _          -> async { return failwith "GhClient should not be called" }
        CreateLabel       = fun _ _        -> async { return failwith "GhClient should not be called" }
        FindIssue         = fun _ _        -> async { return failwith "GhClient should not be called" }
        FindClosedIssue   = fun _ _        -> async { return failwith "GhClient should not be called" }
        CreateIssue       = fun _ _ _ _    -> async { return failwith "GhClient should not be called" }
        AddIssueToProject = fun _ _        -> async { return failwith "GhClient should not be called" }
        AssignIssue       = fun _ _ _      -> async { return failwith "GhClient should not be called" }
        UnassignIssue     = fun _ _ _      -> async { return failwith "GhClient should not be called" }
        RepoExists        = fun _          -> async { return failwith "GhClient should not be called" } }

/// Wraps a Handlers record in an IGhClient interface.
let from (h: Handlers) : IGhClient =
    { new IGhClient with
        member _.FindProject org title      = h.FindProject org title
        member _.CreateProject org title    = h.CreateProject org title
        member _.DeleteProject proj         = h.DeleteProject proj
        member _.ListLabels repo            = h.ListLabels repo
        member _.CreateLabel repo name      = h.CreateLabel repo name
        member _.FindIssue repo title       = h.FindIssue repo title
        member _.FindClosedIssue repo title = h.FindClosedIssue repo title
        member _.ReopenIssue repo issue     = h.ReopenIssue repo issue
        member _.CreateIssue repo t b ls    = h.CreateIssue repo t b ls
        member _.DeleteIssue repo issue      = h.DeleteIssue repo issue
        member _.AddIssueToProject proj iss = h.AddIssueToProject proj iss
        member _.AssignIssue repo iss asgn   = h.AssignIssue repo iss asgn
        member _.UnassignIssue repo iss asgn = h.UnassignIssue repo iss asgn
        member _.FindPrsForIssue repo iss    = h.FindPrsForIssue repo iss
        member _.ClosePr repo pr            = h.ClosePr repo pr
        member _.ListRepos org              = h.ListRepos org
        member _.RepoExists repo            = h.RepoExists repo }

/// Returns a handler for AssignIssue that records calls by `label`.
let trackingAssign (label: string) (calls: ConcurrentBag<string>) =
    fun (_ : RepoName) (_ : IssueNumber) (_ : string) ->
        calls.Add(label)
        async { return Ok () }

/// Returns a handler for AddIssueToProject that records calls.
let trackingAddIssue (calls: ConcurrentBag<unit>) =
    fun (_ : ProjectInfo) (_ : IssueRef) ->
        calls.Add(())
        async { return Ok () }

/// Returns a handler for AssignIssue that records calls as unit.
let trackingAssignUnit (calls: ConcurrentBag<unit>) =
    fun (_ : RepoName) (_ : IssueNumber) (_ : string) ->
        calls.Add(())
        async { return Ok () }
