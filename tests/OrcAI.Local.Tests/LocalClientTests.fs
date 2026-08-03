module OrcAI.Local.Tests.LocalClientTests

open System.IO.Abstractions
open Testably.Abstractions.Testing
open Xunit
open OrcAI.Core.Domain
open OrcAI.Core.Provider
open OrcAI.Local.LocalClient

let private root = "/store"

let private newClientWithFs () : MockFileSystem * IIssueTracker =
    let fs = MockFileSystem()
    fs, (LocalClient(fs :> IFileSystem, root) :> IIssueTracker)

let private newClient () : IIssueTracker =
    newClientWithFs () |> snd

let private org  = OrgName "dburriss"
let private repo = RepoName "dburriss/wye"

/// The on-disk issue path per plans/local-provider.md's storage layout
/// (repos/<org>/<repo>/issues/<id>.md) — used to simulate state changes
/// IIssueTracker has no method for (e.g. an issue closed outside this
/// abstraction, the same way a human or PR-merge closes a GitHub issue).
let private issuePath (issue: IssueId) =
    let (IssueId id) = issue
    $"{root}/repos/dburriss/wye/issues/{id}.md"

let private closeIssueOnDisk (fs: MockFileSystem) (issue: IssueId) =
    let path = issuePath issue
    fs.File.WriteAllText(path, fs.File.ReadAllText(path).Replace("state: open", "state: closed"))

[<Fact>]
let ``FindProject returns None when no project exists`` () =
    let tracker = newClient ()
    Assert.Equal(None, tracker.FindProject org "Add AGENTS.md" |> Async.RunSynchronously)

[<Fact>]
let ``CreateProject creates a new project and FindProject then finds it`` () =
    let tracker = newClient ()
    match tracker.CreateProject org "Add AGENTS.md" |> Async.RunSynchronously with
    | Error e -> Assert.Fail($"expected Ok, got Error {e}")
    | Ok created ->
        Assert.Equal("Add AGENTS.md", created.Title)
        Assert.Equal(org, created.Org)
        Assert.StartsWith("local://", created.Url)
        match tracker.FindProject org "Add AGENTS.md" |> Async.RunSynchronously with
        | None -> Assert.Fail("expected the created project to be found")
        | Some found -> Assert.Equal(created.Id, found.Id)

[<Fact>]
let ``CreateProject twice converges on the same project (idempotent, no counter race)`` () =
    let tracker = newClient ()
    let first  = tracker.CreateProject org "Add AGENTS.md" |> Async.RunSynchronously
    let second = tracker.CreateProject org "Add AGENTS.md" |> Async.RunSynchronously
    match first, second with
    | Ok p1, Ok p2 -> Assert.Equal(p1.Id, p2.Id)
    | _ -> Assert.Fail("expected both creates to succeed")

[<Fact>]
let ``DeleteProject removes the project file`` () =
    let tracker = newClient ()
    let created = tracker.CreateProject org "Add AGENTS.md" |> Async.RunSynchronously |> function Ok p -> p | Error e -> failwith e
    tracker.DeleteProject created |> Async.RunSynchronously |> ignore
    Assert.Equal(None, tracker.FindProject org "Add AGENTS.md" |> Async.RunSynchronously)

[<Fact>]
let ``CreateIssue writes an issue and FindIssue finds it by title`` () =
    let tracker = newClient ()
    match tracker.CreateIssue repo "Fix the thing" "body text" [ "bug" ] |> Async.RunSynchronously with
    | Error e -> Assert.Fail($"expected Ok, got Error {e}")
    | Ok created ->
        Assert.Equal(repo, created.Repo)
        Assert.StartsWith("local://", created.Url)
        match tracker.FindIssue repo "Fix the thing" |> Async.RunSynchronously with
        | Ok (Some found) -> Assert.Equal(created.Id, found.Id)
        | other -> Assert.Fail($"expected the created issue to be found, got {other}")

[<Fact>]
let ``FindIssue returns Ok None for an unknown title`` () =
    let tracker = newClient ()
    Assert.Equal(Ok None, tracker.FindIssue repo "Does not exist" |> Async.RunSynchronously)

[<Fact>]
let ``GetIssueState returns None for an unknown issue`` () =
    let tracker = newClient ()
    Assert.Equal(None, tracker.GetIssueState repo (IssueId "01UNKNOWN") |> Async.RunSynchronously)

[<Fact>]
let ``GetIssueState returns open for a freshly created issue`` () =
    let tracker = newClient ()
    let created = tracker.CreateIssue repo "Task" "body" [] |> Async.RunSynchronously |> function Ok i -> i | Error e -> failwith e
    Assert.Equal(Some "open", tracker.GetIssueState repo created.Id |> Async.RunSynchronously)

[<Fact>]
let ``ReopenIssue flips a closed issue back to open`` () =
    let fs, tracker = newClientWithFs ()
    let created = tracker.CreateIssue repo "Task" "body" [] |> Async.RunSynchronously |> function Ok i -> i | Error e -> failwith e
    closeIssueOnDisk fs created.Id
    Assert.Equal(Some "closed", tracker.GetIssueState repo created.Id |> Async.RunSynchronously)
    match tracker.ReopenIssue repo created.Id |> Async.RunSynchronously with
    | Error e -> Assert.Fail($"expected Ok, got Error {e}")
    | Ok reopened -> Assert.Equal(Some "open", tracker.GetIssueState repo reopened.Id |> Async.RunSynchronously)

[<Fact>]
let ``FindClosedIssue finds an issue in closed state, FindIssue no longer does`` () =
    let fs, tracker = newClientWithFs ()
    let created = tracker.CreateIssue repo "Closed task" "body" [] |> Async.RunSynchronously |> function Ok i -> i | Error e -> failwith e
    closeIssueOnDisk fs created.Id
    match tracker.FindClosedIssue repo "Closed task" |> Async.RunSynchronously with
    | Ok (Some found) -> Assert.Equal(created.Id, found.Id)
    | other -> Assert.Fail($"expected to find the closed issue, got {other}")
    Assert.Equal(Ok None, tracker.FindIssue repo "Closed task" |> Async.RunSynchronously)

[<Fact>]
let ``UpdateIssue changes title and body without changing id`` () =
    let tracker = newClient ()
    let created = tracker.CreateIssue repo "Old title" "old body" [] |> Async.RunSynchronously |> function Ok i -> i | Error e -> failwith e
    match tracker.UpdateIssue repo created.Id "New title" "new body" |> Async.RunSynchronously with
    | Error e -> Assert.Fail($"expected Ok, got Error {e}")
    | Ok () ->
        match tracker.FindIssue repo "New title" |> Async.RunSynchronously with
        | Ok (Some found) -> Assert.Equal(created.Id, found.Id)
        | other -> Assert.Fail($"expected the renamed issue to be found, got {other}")

[<Fact>]
let ``DeleteIssue removes the issue file`` () =
    let tracker = newClient ()
    let created = tracker.CreateIssue repo "Doomed" "body" [] |> Async.RunSynchronously |> function Ok i -> i | Error e -> failwith e
    tracker.DeleteIssue repo created.Id |> Async.RunSynchronously |> ignore
    Assert.Equal(None, tracker.GetIssueState repo created.Id |> Async.RunSynchronously)

[<Fact>]
let ``AssignIssue adds an assignee and is idempotent`` () =
    let tracker = newClient ()
    let created = tracker.CreateIssue repo "Task" "body" [] |> Async.RunSynchronously |> function Ok i -> i | Error e -> failwith e
    tracker.AssignIssue repo created.Id "copilot" |> Async.RunSynchronously |> ignore
    tracker.AssignIssue repo created.Id "copilot" |> Async.RunSynchronously |> ignore
    match tracker.FindIssue repo "Task" |> Async.RunSynchronously with
    | Ok (Some found) -> Assert.Equal<string list>([ "copilot" ], found.Assignees)
    | other -> Assert.Fail($"expected to find the issue, got {other}")

[<Fact>]
let ``UnassignIssue removes an assignee`` () =
    let tracker = newClient ()
    let created = tracker.CreateIssue repo "Task" "body" [] |> Async.RunSynchronously |> function Ok i -> i | Error e -> failwith e
    tracker.AssignIssue repo created.Id "copilot" |> Async.RunSynchronously |> ignore
    tracker.UnassignIssue repo created.Id "copilot" |> Async.RunSynchronously |> ignore
    match tracker.FindIssue repo "Task" |> Async.RunSynchronously with
    | Ok (Some found) -> Assert.Equal<string list>([], found.Assignees)
    | other -> Assert.Fail($"expected to find the issue, got {other}")

[<Fact>]
let ``PostComment appends a Comment section to the issue body`` () =
    let fs, tracker = newClientWithFs ()
    let created = tracker.CreateIssue repo "Task" "original body" [] |> Async.RunSynchronously |> function Ok i -> i | Error e -> failwith e
    match tracker.PostComment repo created.Id "a comment" |> Async.RunSynchronously with
    | Error e -> Assert.Fail($"expected Ok, got Error {e}")
    | Ok () ->
        let content = fs.File.ReadAllText(issuePath created.Id)
        Assert.Contains("original body", content)
        Assert.Contains("### Comment", content)
        Assert.Contains("a comment", content)

[<Fact>]
let ``AddIssueToProject appends a repo#id entry and is idempotent`` () =
    let tracker = newClient ()
    let project = tracker.CreateProject org "Add AGENTS.md" |> Async.RunSynchronously |> function Ok p -> p | Error e -> failwith e
    let issue   = tracker.CreateIssue repo "Task" "body" [] |> Async.RunSynchronously |> function Ok i -> i | Error e -> failwith e
    Assert.Equal(Ok (), tracker.AddIssueToProject project issue |> Async.RunSynchronously)
    Assert.Equal(Ok (), tracker.AddIssueToProject project issue |> Async.RunSynchronously)

[<Fact>]
let ``AddIssueToProject errors when the project does not exist`` () =
    let tracker = newClient ()
    let issue   = tracker.CreateIssue repo "Task" "body" [] |> Async.RunSynchronously |> function Ok i -> i | Error e -> failwith e
    let missingProject = { Org = org; Id = ProjectId "does-not-exist"; Title = "x"; Url = "local://x" }
    match tracker.AddIssueToProject missingProject issue |> Async.RunSynchronously with
    | Error _ -> ()
    | Ok () -> Assert.Fail("expected an error for a missing project")

[<Fact>]
let ``ListLabels is empty until CreateLabel is called, then round-trips`` () =
    let tracker = newClient ()
    Assert.Equal(Ok [], tracker.ListLabels repo |> Async.RunSynchronously)
    tracker.CreateLabel repo "automated" |> Async.RunSynchronously |> ignore
    tracker.CreateLabel repo "automated" |> Async.RunSynchronously |> ignore // idempotent
    Assert.Equal(Ok [ "automated" ], tracker.ListLabels repo |> Async.RunSynchronously)
