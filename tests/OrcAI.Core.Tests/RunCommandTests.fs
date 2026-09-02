module OrcAI.Core.Tests.RunCommandTests

open System.Collections.Concurrent
open System.Diagnostics
open System.IO
open Xunit
open Testably.Abstractions.Testing
open OrcAI.Core.Domain
open OrcAI.Core.RunCommand
open OrcAI.Core.Tests.TestData

// ---------------------------------------------------------------------------
// labelsToCreate — pure helper
// ---------------------------------------------------------------------------

[<Fact>]
let ``labelsToCreate returns empty when no labels requested`` () =
    Assert.Empty(labelsToCreate ["bug"; "documentation"] [])

[<Fact>]
let ``labelsToCreate returns empty when all requested labels already exist`` () =
    Assert.Empty(labelsToCreate ["bug"; "documentation"] ["bug"; "documentation"])

[<Fact>]
let ``labelsToCreate returns missing labels`` () =
    Assert.Equal<string list>(["new-label"], labelsToCreate ["bug"] ["bug"; "new-label"])

[<Fact>]
let ``labelsToCreate returns all labels when none exist`` () =
    Assert.Equal<string list>(["alpha"; "beta"], labelsToCreate [] ["alpha"; "beta"])

[<Fact>]
let ``labelsToCreate is case-insensitive for existing labels`` () =
    Assert.Empty(labelsToCreate ["BUG"; "Documentation"] ["bug"; "documentation"])

[<Fact>]
let ``labelsToCreate is case-insensitive for requested labels`` () =
    Assert.Empty(labelsToCreate ["bug"; "documentation"] ["BUG"; "Documentation"])

[<Fact>]
let ``labelsToCreate preserves original casing of missing labels`` () =
    Assert.Equal<string list>(["My-Label"; "Another Label"], labelsToCreate [] ["My-Label"; "Another Label"])

[<Fact>]
let ``labelsToCreate returns empty when existing labels list is empty and no labels requested`` () =
    Assert.Empty(labelsToCreate [] [])

// ---------------------------------------------------------------------------
// isLabelAlreadyExists — detects gh stderr indicating idempotent success
// ---------------------------------------------------------------------------

[<Theory>]
[<InlineData("label with name \"marge\" already exists; use `--force` to update its color and description")>]
[<InlineData("GraphQL: Name has already been taken (createLabel)")>]
[<InlineData("ALREADY EXISTS")>]
let ``isLabelAlreadyExists matches gh CLI duplicate-label errors`` (msg: string) =
    Assert.True(isLabelAlreadyExists msg)

[<Theory>]
[<InlineData("HTTP 403: Repository was archived so is read-only.")>]
[<InlineData("HTTP 404: not found")>]
[<InlineData("")>]
let ``isLabelAlreadyExists does not match unrelated errors`` (msg: string) =
    Assert.False(isLabelAlreadyExists msg)

[<Fact>]
let ``ensureLabelsExist treats 'already exists' from CreateLabel as success`` () =
    let fs    = MockFileSystem()
    let yaml  =
        "job:\n  title: \"T\"\n  org: \"myorg\"\n" +
        "repos:\n  - \"repo-a\"\n" +
        "issue:\n  template: \"./template.md\"\n  labels: [\"marge\"]\n"
    let path =
        let dir = "/work"
        (fs :> System.IO.Abstractions.IFileSystem).Directory.CreateDirectory(dir) |> ignore
        (fs :> System.IO.Abstractions.IFileSystem).File.WriteAllText(dir + "/template.md", "# body")
        let p = dir + "/job.yml"
        (fs :> System.IO.Abstractions.IFileSystem).File.WriteAllText(p, yaml)
        p
    let createCalls = ConcurrentBag<string>()
    let client =
        FakeGhClient.from
            { FakeGhClient.defaults with
                ListLabels  = fun _ -> async { return Ok [] }
                CreateLabel = fun _ name ->
                    createCalls.Add(name)
                    async { return Error "label with name \"marge\" already exists; use --force to update its color and description" } }
    let deps  = Given.deps fs client
    let input = { A.RunInput.defaults () with AutoCreateLabels = true }

    let results = execute deps [path] input |> Async.RunSynchronously

    Assert.True(results |> Map.forall (fun _ r -> match r with Ok _ -> true | Error _ -> false))
    Assert.Contains("marge", createCalls)

// ---------------------------------------------------------------------------
// Multi-file execute tests
// ---------------------------------------------------------------------------

[<Fact>]
let ``execute processes all files in a resolved path list`` () =
    let fs    = MockFileSystem()
    let path1 = Given.namedYamlFile fs "job1.yml"
    let path2 = Given.namedYamlFile fs "job2.yml"
    let deps  = Given.deps fs (FakeGhClient.from FakeGhClient.defaults)

    let results = execute deps [path1; path2] (A.RunInput.defaults ()) |> Async.RunSynchronously

    Assert.Equal(2, results.Count)
    Assert.True(results |> Map.forall (fun _ r -> match r with Ok _ -> true | Error _ -> false))

[<Fact>]
let ``execute result is always a filename-keyed dictionary`` () =
    let fs   = MockFileSystem()
    let path = Given.namedYamlFile fs "job.yml"
    let deps = Given.deps fs (FakeGhClient.from FakeGhClient.defaults)

    let results = execute deps [path] (A.RunInput.defaults ()) |> Async.RunSynchronously

    Assert.True(results.ContainsKey(path))

[<Fact>]
let ``execute stops on first failure without ContinueOnError (sequential)`` () =
    let fs    = MockFileSystem()
    let path1 = Given.namedYamlFile fs "job1.yml"
    let path2 = Given.namedYamlFile fs "job2.yml"
    (fs :> System.IO.Abstractions.IFileSystem).File.WriteAllText(path1, "not: valid: yaml: at: all\n!!!")
    let deps  = Given.deps fs (FakeGhClient.from FakeGhClient.defaults)
    let input = A.RunInput.defaults () |> A.RunInput.withNoParallel true

    let results = execute deps [path1; path2] input |> Async.RunSynchronously

    Assert.True(results.ContainsKey(path1))
    match results.[path1] with
    | Error _ -> ()
    | Ok _    -> Assert.Fail("Expected path1 to fail")
    Assert.False(results.ContainsKey(path2), "path2 should not have been processed")

[<Fact>]
let ``execute continues past failures with ContinueOnError`` () =
    let fs    = MockFileSystem()
    let path1 = Given.namedYamlFile fs "job1.yml"
    let path2 = Given.namedYamlFile fs "job2.yml"
    (fs :> System.IO.Abstractions.IFileSystem).File.WriteAllText(path1, "not: valid: yaml: at: all\n!!!")
    let deps  = Given.deps fs (FakeGhClient.from FakeGhClient.defaults)
    let input =
        A.RunInput.defaults ()
        |> A.RunInput.withNoParallel true
        |> A.RunInput.withContinueOnError true

    let results = execute deps [path1; path2] input |> Async.RunSynchronously

    Assert.True(results.ContainsKey(path1))
    Assert.True(results.ContainsKey(path2))
    match results.[path1] with
    | Error _ -> ()
    | Ok _    -> Assert.Fail("Expected path1 to fail")
    match results.[path2] with
    | Ok _    -> ()
    | Error e -> Assert.Fail($"Expected path2 to succeed but got: {e}")

[<Fact>]
let ``execute with NoParallel=true processes files sequentially and returns correct results`` () =
    let fs    = MockFileSystem()
    let path1 = Given.namedYamlFile fs "job1.yml"
    let path2 = Given.namedYamlFile fs "job2.yml"
    let deps  = Given.deps fs (FakeGhClient.from FakeGhClient.defaults)
    let input =
        A.RunInput.defaults ()
        |> A.RunInput.withNoParallel true
        |> A.RunInput.withContinueOnError true

    let results = execute deps [path1; path2] input |> Async.RunSynchronously

    Assert.Equal(2, results.Count)
    Assert.True(results |> Map.forall (fun _ r -> match r with Ok _ -> true | Error _ -> false))

// ---------------------------------------------------------------------------
// Assignment tests
// ---------------------------------------------------------------------------
// Copilot-vs-primary token selection is now an internal GhCliClient concern
// (see OrcAI.GitHub.Tests), invisible to RunCommand — it always calls
// tracker.AssignIssue directly, same as any other assignee.

[<Fact>]
let ``processRepo calls AssignIssue via the tracker`` () =
    let fs          = MockFileSystem()
    let path        = Given.namedYamlFile fs "job.yml"
    let assignCalls = ConcurrentBag<string>()
    let client      = FakeGhClient.from { FakeGhClient.defaults with AssignIssue = FakeGhClient.trackingAssign "primary" assignCalls }
    let deps        = Given.deps fs client
    let input       = A.RunInput.defaults ()

    let results = execute deps [path] input |> Async.RunSynchronously

    Assert.True(results |> Map.forall (fun _ r -> match r with Ok _ -> true | Error _ -> false))
    Assert.Contains("primary", assignCalls)

[<Fact>]
let ``processRepo skips assignment entirely when action is noop`` () =
    let fs          = MockFileSystem()
    let path        = Given.namedNoopYamlFile fs "job.yml"
    let assignCalls = ConcurrentBag<string>()
    let client      = FakeGhClient.from { FakeGhClient.defaults with AssignIssue = FakeGhClient.trackingAssign "primary" assignCalls }
    let deps        = Given.deps fs client
    let input       = A.RunInput.defaults ()

    let results = execute deps [path] input |> Async.RunSynchronously

    Assert.True(results |> Map.forall (fun _ r -> match r with Ok _ -> true | Error _ -> false))
    Assert.Empty(assignCalls)

// ---------------------------------------------------------------------------
// ClosedIssueAction tests
// ---------------------------------------------------------------------------

let private closedIssueClient () =
    FakeGhClient.from
        { FakeGhClient.defaults with
            FetchReposState = FakeGhClient.fetchReposStateReturning (fun r -> FakeGhClient.repoStateWithClosed r 7)
            ReopenIssue     = fun repo _ -> async { return Ok (FakeGhClient.issueFor repo 7) }
            CreateIssue     = fun _ _ _ _ -> async { return failwith "CreateIssue should not be called" } }

[<Fact>]
let ``reopen action reopens closed issue and returns Reopened outcome`` () =
    let fs    = MockFileSystem()
    let path  = Given.namedYamlFile fs "job.yml"
    let deps  = Given.deps fs (closedIssueClient ())
    let input = A.RunInput.defaults () |> A.RunInput.withOnClosedIssue (Some Reopen)

    let results = execute deps [path] input |> Async.RunSynchronously

    Assert.True(results.ContainsKey(path))
    match results.[path] with
    | Error e -> Assert.Fail($"Expected Ok but got Error: {e}")
    | Ok result ->
        Assert.True(result.Results |> List.forall (fun r -> r.Outcome = Reopened))

[<Fact>]
let ``reopen action does not call CreateIssue when closed issue exists`` () =
    let fs    = MockFileSystem()
    let path  = Given.namedYamlFile fs "job.yml"
    let deps  = Given.deps fs (closedIssueClient ())
    let input = A.RunInput.defaults () |> A.RunInput.withOnClosedIssue (Some Reopen)

    let results = execute deps [path] input |> Async.RunSynchronously

    Assert.True(results |> Map.forall (fun _ r -> match r with Ok _ -> true | Error _ -> false))

[<Fact>]
let ``skip action returns Skipped outcome without creating or reopening`` () =
    let fs    = MockFileSystem()
    let path  = Given.namedYamlFile fs "job.yml"
    let pc    = ConcurrentBag<unit>()
    let ac    = ConcurrentBag<unit>()
    let client =
        FakeGhClient.from
            { FakeGhClient.defaults with
                FetchReposState   = FakeGhClient.fetchReposStateReturning (fun r -> FakeGhClient.repoStateWithClosed r 7)
                CreateIssue       = fun _ _ _ _ -> async { return failwith "CreateIssue not expected" }
                AddIssueToProject = FakeGhClient.trackingAddIssue pc
                AssignIssue       = FakeGhClient.trackingAssignUnit ac }
    let deps  = Given.deps fs client
    let input = A.RunInput.defaults () |> A.RunInput.withOnClosedIssue (Some Skip)

    let results = execute deps [path] input |> Async.RunSynchronously

    Assert.True(results.ContainsKey(path))
    match results.[path] with
    | Error e -> Assert.Fail($"Expected Ok but got Error: {e}")
    | Ok result ->
        Assert.True(result.Results |> List.forall (fun r -> r.Outcome = Skipped))

[<Fact>]
let ``skip action does not add issue to project or assign copilot`` () =
    let fs    = MockFileSystem()
    let path  = Given.namedYamlFile fs "job.yml"
    let pc    = ConcurrentBag<unit>()
    let ac    = ConcurrentBag<unit>()
    let client =
        FakeGhClient.from
            { FakeGhClient.defaults with
                FetchReposState   = FakeGhClient.fetchReposStateReturning (fun r -> FakeGhClient.repoStateWithClosed r 7)
                CreateIssue       = fun _ _ _ _ -> async { return failwith "CreateIssue not expected" }
                AddIssueToProject = FakeGhClient.trackingAddIssue pc
                AssignIssue       = FakeGhClient.trackingAssignUnit ac }
    let deps  = Given.deps fs client
    let input =
        A.RunInput.defaults ()
        |> A.RunInput.withOnClosedIssue (Some Skip)

    execute deps [path] input |> Async.RunSynchronously |> ignore

    Assert.Empty(pc)
    Assert.Empty(ac)

[<Fact>]
let ``fail action returns error and does not create or reopen`` () =
    let fs    = MockFileSystem()
    let path  = Given.namedYamlFile fs "job.yml"
    let client =
        FakeGhClient.from
            { FakeGhClient.defaults with
                FetchReposState   = FakeGhClient.fetchReposStateReturning (fun r -> FakeGhClient.repoStateWithClosed r 7)
                CreateIssue       = fun _ _ _ _ -> async { return failwith "CreateIssue not expected" }
                AddIssueToProject = fun _ _     -> async { return failwith "AddIssueToProject not expected" }
                AssignIssue       = fun _ _ _   -> async { return failwith "AssignIssue not expected" } }
    let deps  = Given.deps fs client
    let input = A.RunInput.defaults () |> A.RunInput.withOnClosedIssue (Some Fail)

    let results = execute deps [path] input |> Async.RunSynchronously

    Assert.True(results.ContainsKey(path))
    match results.[path] with
    | Ok result -> Assert.Empty(result.Results)
    | Error _   -> ()

// ---------------------------------------------------------------------------
// Lookup error handling — must NOT fall through to CreateIssue
// ---------------------------------------------------------------------------

[<Fact>]
let ``FindIssue Error does not create a new issue`` () =
    let fs   = MockFileSystem()
    let path = Given.namedYamlFile fs "job.yml"
    let client =
        FakeGhClient.from
            { FakeGhClient.defaults with
                FetchReposState = fun repos _ -> async { return repos |> List.map (fun r -> r, Error "API rate limit exceeded") |> Map.ofList }
                FindIssue       = fun _ _     -> async { return Error "API rate limit exceeded" }
                CreateIssue     = fun _ _ _ _ -> async { return failwith "CreateIssue must not be called on lookup error" } }
    let deps = Given.deps fs client

    let results = execute deps [path] (A.RunInput.defaults ()) |> Async.RunSynchronously

    Assert.True(results.ContainsKey(path))
    match results.[path] with
    | Ok result -> Assert.Empty(result.Results)
    | Error _   -> ()

[<Fact>]
let ``FindClosedIssue Error does not create a new issue`` () =
    let fs   = MockFileSystem()
    let path = Given.namedYamlFile fs "job.yml"
    let client =
        FakeGhClient.from
            { FakeGhClient.defaults with
                FetchReposState = fun repos _ -> async { return repos |> List.map (fun r -> r, Error "secondary rate limit") |> Map.ofList }
                FindIssue       = fun _ _     -> async { return Ok None }
                FindClosedIssue = fun _ _     -> async { return Error "secondary rate limit" }
                CreateIssue     = fun _ _ _ _ -> async { return failwith "CreateIssue must not be called on closed-issue lookup error" } }
    let deps  = Given.deps fs client
    let input = A.RunInput.defaults () |> A.RunInput.withOnClosedIssue (Some Reopen)

    let results = execute deps [path] input |> Async.RunSynchronously

    Assert.True(results.ContainsKey(path))
    match results.[path] with
    | Ok result -> Assert.Empty(result.Results)
    | Error _   -> ()

// ---------------------------------------------------------------------------
// Archived repo handling
// ---------------------------------------------------------------------------

[<Fact>]
let ``processRepo returns SkippedArchived outcome when IsArchived=true`` () =
    let fs   = MockFileSystem()
    let path = Given.namedYamlFile fs "job.yml"
    let client =
        FakeGhClient.from
            { FakeGhClient.defaults with
                FetchReposState = FakeGhClient.fetchReposStateReturning (fun _ -> FakeGhClient.repoStateArchived)
                FindIssue       = fun _ _ -> async { return failwith "FindIssue not expected for archived repo" }
                CreateIssue     = fun _ _ _ _ -> async { return failwith "CreateIssue not expected for archived repo" }
                UpdateIssue     = fun _ _ _ _ -> async { return failwith "UpdateIssue not expected for archived repo" } }
    let deps = Given.deps fs client

    let results = execute deps [path] (A.RunInput.defaults ()) |> Async.RunSynchronously

    match results.[path] with
    | Error e -> Assert.Fail($"Expected Ok but got: {e}")
    | Ok result ->
        Assert.True(result.Results |> List.forall (fun r -> r.Outcome = SkippedArchived))

[<Fact>]
let ``runFull writes SkippedArchived repos to lock.SkippedRepos and not lock.Issues`` () =
    let fs   = MockFileSystem()
    let path = Given.namedYamlFile fs "job.yml"
    let client =
        FakeGhClient.from
            { FakeGhClient.defaults with
                FetchReposState = FakeGhClient.fetchReposStateReturning (fun _ -> FakeGhClient.repoStateArchived) }
    let deps  = Given.deps fs client
    let input = { A.RunInput.defaults () with SkipLock = false }

    let results = execute deps [path] input |> Async.RunSynchronously

    match results.[path] with
    | Error e -> Assert.Fail($"Expected Ok but got: {e}")
    | Ok result ->
        Assert.Empty(result.Lock.Issues)
        Assert.NotEmpty(result.Lock.SkippedRepos)
        Assert.Equal<string list>(
            [ "myorg/repo-a" ],
            result.Lock.SkippedRepos |> List.map (fun (RepoName r) -> r))

[<Fact>]
let ``IsArchived error is non-fatal and processRepo proceeds`` () =
    let fs   = MockFileSystem()
    let path = Given.namedYamlFile fs "job.yml"
    let createCalls = ConcurrentBag<unit>()
    let client =
        FakeGhClient.from
            { FakeGhClient.defaults with
                FetchReposState = fun repos _ -> async { return repos |> List.map (fun r -> r, Error "transient network error") |> Map.ofList }
                IsArchived      = fun _       -> async { return Error "transient network error" }
                CreateIssue     = fun repo _ _ _ ->
                    createCalls.Add(())
                    async { return Ok (FakeGhClient.issueFor repo 42) } }
    let deps = Given.deps fs client

    let results = execute deps [path] (A.RunInput.defaults ()) |> Async.RunSynchronously

    match results.[path] with
    | Error e -> Assert.Fail($"Expected Ok but got: {e}")
    | Ok result ->
        Assert.True(result.Results |> List.forall (fun r -> r.Outcome = Created))
        Assert.NotEmpty(createCalls)

// ---------------------------------------------------------------------------
// Stale-issue detection and recovery
// ---------------------------------------------------------------------------

[<Theory>]
[<InlineData("GraphQL: Could not resolve to an issue or pull request with the number of 42. (updateIssue)")>]
[<InlineData("could not resolve to an issue OR PULL REQUEST")>]
let ``isStaleIssue matches gh CLI stale-issue errors`` (msg: string) =
    Assert.True(isStaleIssue msg)

[<Theory>]
[<InlineData("HTTP 403: Repository was archived so is read-only.")>]
[<InlineData("HTTP 404: not found")>]
[<InlineData("")>]
let ``isStaleIssue does not match unrelated errors`` (msg: string) =
    Assert.False(isStaleIssue msg)

/// Set up a lock file whose YAML hash matches the current YAML file but template hash
/// is stale, so executeSingle re-runs runFull and then refreshBodies.
let private givenStaleTemplateLock (fs: MockFileSystem) (yamlPath: string) (issueRepo: RepoName) (issueNum: int) =
    let yamlHash = OrcAI.Core.YamlConfig.computeHash (fs :> System.IO.Abstractions.IFileSystem) yamlPath
    let issue =
        let (RepoName r) = issueRepo
        { Repo = issueRepo
          Id = IssueId (string issueNum)
          Url = $"https://github.com/{r}/issues/{issueNum}"
          Assignees = [] }
    let lock =
        { A.LockFile.defaults () with
            YamlHash     = yamlHash
            TemplateHash = "stale-template-hash"
            Repos        = [ issueRepo ]
            Issues       = [ issue ]
            PullRequests = [] }
    OrcAI.Core.LockFile.write (fs :> System.IO.Abstractions.IFileSystem) yamlPath lock

/// Set up a lock file whose template hash matches the current template but YAML hash
/// is stale, so executeSingle re-runs runFull but skips refreshBodies.
let private givenStaleYamlLock (fs: MockFileSystem) (yamlPath: string) (issueRepo: RepoName) (issueNum: int) =
    let templateHash =
        match OrcAI.Core.YamlConfig.resolveTemplatePath (fs :> System.IO.Abstractions.IFileSystem) yamlPath with
        | Some p -> OrcAI.Core.YamlConfig.computeTemplateHash (fs :> System.IO.Abstractions.IFileSystem) p
        | None   -> ""
    let issue =
        let (RepoName r) = issueRepo
        { Repo = issueRepo
          Id = IssueId (string issueNum)
          Url = $"https://github.com/{r}/issues/{issueNum}"
          Assignees = [] }
    let lock =
        { A.LockFile.defaults () with
            YamlHash     = "stale-yaml-hash"
            TemplateHash = templateHash
            Repos        = [ issueRepo ]
            Issues       = [ issue ]
            PullRequests = [] }
    OrcAI.Core.LockFile.write (fs :> System.IO.Abstractions.IFileSystem) yamlPath lock

[<Fact>]
let ``template change triggers runFull and refreshes body of existing open issue`` () =
    let fs   = MockFileSystem()
    let path = Given.namedYamlFile fs "job.yml"
    let repo = RepoName "myorg/repo-a"
    givenStaleTemplateLock fs path repo 42

    let updateCalls = ConcurrentBag<unit>()
    let client =
        FakeGhClient.from
            { FakeGhClient.defaults with
                FetchReposState = FakeGhClient.fetchReposStateReturning (fun r -> FakeGhClient.repoStateWithOpen r 42)
                UpdateIssue     = fun _ _ _ _ ->
                    updateCalls.Add(())
                    async { return Ok () }
                CreateIssue     = fun _ _ _ _ -> async { return failwith "CreateIssue not expected" } }
    let deps  = Given.deps fs client
    let input = { A.RunInput.defaults () with SkipLock = false }

    let results = execute deps [path] input |> Async.RunSynchronously

    Assert.NotEmpty(updateCalls)
    match results.[path] with
    | Error e -> Assert.Fail($"Expected Ok but got: {e}")
    | Ok result ->
        Assert.Contains(result.Results, fun r -> r.Outcome = Updated)

[<Fact>]
let ``YAML-only change runs runFull but does NOT refresh issue bodies`` () =
    let fs   = MockFileSystem()
    let path = Given.namedYamlFile fs "job.yml"
    let repo = RepoName "myorg/repo-a"
    givenStaleYamlLock fs path repo 42

    let updateCalls = ConcurrentBag<unit>()
    let client =
        FakeGhClient.from
            { FakeGhClient.defaults with
                FetchReposState = FakeGhClient.fetchReposStateReturning (fun r -> FakeGhClient.repoStateWithOpen r 42)
                UpdateIssue     = fun _ _ _ _ ->
                    updateCalls.Add(())
                    async { return Ok () } }
    let deps  = Given.deps fs client
    let input = { A.RunInput.defaults () with SkipLock = false }

    let results = execute deps [path] input |> Async.RunSynchronously

    Assert.Empty(updateCalls)
    match results.[path] with
    | Error e -> Assert.Fail($"Expected Ok but got: {e}")
    | Ok result ->
        Assert.Contains(result.Results, fun r -> r.Outcome = AlreadyExisted)

[<Fact>]
let ``--skip-lock refreshes bodies of existing open issues even with no edits`` () =
    let fs   = MockFileSystem()
    let path = Given.namedYamlFile fs "job.yml"

    let updateCalls = ConcurrentBag<unit>()
    let client =
        FakeGhClient.from
            { FakeGhClient.defaults with
                FetchReposState = FakeGhClient.fetchReposStateReturning (fun r -> FakeGhClient.repoStateWithOpen r 42)
                UpdateIssue     = fun _ _ _ _ ->
                    updateCalls.Add(())
                    async { return Ok () }
                CreateIssue     = fun _ _ _ _ -> async { return failwith "CreateIssue not expected" } }
    let deps  = Given.deps fs client
    let input = { A.RunInput.defaults () with SkipLock = true }

    let results = execute deps [path] input |> Async.RunSynchronously

    Assert.NotEmpty(updateCalls)
    match results.[path] with
    | Error e -> Assert.Fail($"Expected Ok but got: {e}")
    | Ok result ->
        Assert.Contains(result.Results, fun r -> r.Outcome = Updated)

[<Fact>]
let ``template change + onClosedIssue=skip does not edit closed issue body`` () =
    let fs   = MockFileSystem()
    let path = Given.namedYamlFile fs "job.yml"
    let repo = RepoName "myorg/repo-a"
    givenStaleTemplateLock fs path repo 42

    let updateCalls = ConcurrentBag<unit>()
    let client =
        FakeGhClient.from
            { FakeGhClient.defaults with
                FetchReposState = FakeGhClient.fetchReposStateReturning (fun r -> FakeGhClient.repoStateWithClosed r 7)
                UpdateIssue     = fun _ _ _ _ ->
                    updateCalls.Add(())
                    async { return Ok () }
                CreateIssue     = fun _ _ _ _ -> async { return failwith "CreateIssue not expected" } }
    let deps  = Given.deps fs client
    let input =
        A.RunInput.defaults ()
        |> A.RunInput.withOnClosedIssue (Some Skip)
    let input = { input with SkipLock = false }

    let results = execute deps [path] input |> Async.RunSynchronously

    Assert.Empty(updateCalls)
    match results.[path] with
    | Error e -> Assert.Fail($"Expected Ok but got: {e}")
    | Ok result ->
        Assert.Contains(result.Results, fun r -> r.Outcome = Skipped)

[<Fact>]
let ``template change + onClosedIssue=reopen reopens and refreshes body`` () =
    let fs   = MockFileSystem()
    let path = Given.namedYamlFile fs "job.yml"
    let repo = RepoName "myorg/repo-a"
    givenStaleTemplateLock fs path repo 42

    let updateCalls = ConcurrentBag<unit>()
    let client =
        FakeGhClient.from
            { FakeGhClient.defaults with
                FetchReposState = FakeGhClient.fetchReposStateReturning (fun r -> FakeGhClient.repoStateWithClosed r 7)
                ReopenIssue     = fun r _ -> async { return Ok (FakeGhClient.issueFor r 7) }
                UpdateIssue     = fun _ _ _ _ ->
                    updateCalls.Add(())
                    async { return Ok () }
                CreateIssue     = fun _ _ _ _ -> async { return failwith "CreateIssue not expected" } }
    let deps  = Given.deps fs client
    let input =
        A.RunInput.defaults ()
        |> A.RunInput.withOnClosedIssue (Some Reopen)
    let input = { input with SkipLock = false }

    let results = execute deps [path] input |> Async.RunSynchronously

    Assert.NotEmpty(updateCalls)
    match results.[path] with
    | Error e -> Assert.Fail($"Expected Ok but got: {e}")
    | Ok result ->
        Assert.Contains(result.Results, fun r -> r.Outcome = Reopened)

[<Fact>]
let ``refreshBodies recreates issue when UpdateIssue returns stale error`` () =
    let fs   = MockFileSystem()
    let path = Given.namedYamlFile fs "job.yml"
    let repo = RepoName "myorg/repo-a"
    givenStaleTemplateLock fs path repo 42

    // FetchReposState returns the open issue for the runFull pass (→ AlreadyExisted).
    // recreateStaleIssues uses the individual fallback path (processRepo None) where
    // FindIssue/FindClosedIssue return None, falling through to CreateIssue.
    let createCalls = ConcurrentBag<unit>()
    let client =
        FakeGhClient.from
            { FakeGhClient.defaults with
                FetchReposState = FakeGhClient.fetchReposStateReturning (fun r -> FakeGhClient.repoStateWithOpen r 42)
                FindIssue       = fun _ _ -> async { return Ok None }
                FindClosedIssue = fun _ _ -> async { return Ok None }
                UpdateIssue     = fun _ _ _ _ ->
                    async { return Error "GraphQL: Could not resolve to an issue or pull request with the number of 42. (updateIssue)" }
                CreateIssue     = fun r _ _ _ ->
                    createCalls.Add(())
                    async { return Ok (FakeGhClient.issueFor r 99) } }
    let deps  = Given.deps fs client
    let input = { A.RunInput.defaults () with SkipLock = false }

    let results = execute deps [path] input |> Async.RunSynchronously

    Assert.NotEmpty(createCalls)
    match results.[path] with
    | Error e -> Assert.Fail($"Expected Ok but got: {e}")
    | Ok result ->
        Assert.Contains(result.Results, fun r -> r.Outcome = StaleIssueRecreated)
        let issue = List.head result.Lock.Issues
        Assert.Equal(IssueId "99", issue.Id)

// ---------------------------------------------------------------------------

[<Fact>]
let ``create action (default) creates new issue even when closed issue exists`` () =
    let fs              = MockFileSystem()
    let path            = Given.namedYamlFile fs "job.yml"
    let createCallCount = ConcurrentBag<unit>()
    let client =
        FakeGhClient.from
            { FakeGhClient.defaults with
                FindClosedIssue = fun repo _ -> async { return Ok (Some (FakeGhClient.issueFor repo 7)) }
                CreateIssue     = fun repo _ _ _ ->
                    createCallCount.Add(())
                    async { return Ok (FakeGhClient.issueFor repo 99) } }
    let deps  = Given.deps fs client
    let input = A.RunInput.defaults () |> A.RunInput.withOnClosedIssue (Some Create)

    let results = execute deps [path] input |> Async.RunSynchronously

    Assert.True(results |> Map.forall (fun _ r -> match r with Ok _ -> true | Error _ -> false))
    Assert.NotEmpty(createCallCount)
    match results.[path] with
    | Error e -> Assert.Fail($"Expected Ok but got Error: {e}")
    | Ok result ->
        Assert.True(result.Results |> List.forall (fun r -> r.Outcome = Created))

// ---------------------------------------------------------------------------
// Dry-run — must not perform any GitHub mutations or write the lock file
// ---------------------------------------------------------------------------

[<Fact>]
let ``dry-run does not call CreateIssue, AddIssueToProject, or AssignIssue`` () =
    let fs          = MockFileSystem()
    let path        = Given.namedYamlFile fs "job.yml"
    let createCalls = ConcurrentBag<unit>()
    let addCalls    = ConcurrentBag<unit>()
    let assignCalls = ConcurrentBag<unit>()
    let client =
        FakeGhClient.from
            { FakeGhClient.defaults with
                CreateIssue       = fun _ _ _ _ ->
                    createCalls.Add(())
                    async { return failwith "CreateIssue must not be called in dry-run" }
                AddIssueToProject = FakeGhClient.trackingAddIssue addCalls
                AssignIssue       = FakeGhClient.trackingAssignUnit assignCalls }
    let deps  = Given.deps fs client
    let input =
        A.RunInput.defaults ()
        |> A.RunInput.withDryRun true

    let results = execute deps [path] input |> Async.RunSynchronously

    Assert.Empty(createCalls)
    Assert.Empty(addCalls)
    Assert.Empty(assignCalls)
    match results.[path] with
    | Error e -> Assert.Fail($"Expected Ok but got: {e}")
    | Ok result ->
        Assert.Contains(result.Results, fun r -> r.Outcome = DryRunWouldCreate)

[<Fact>]
let ``dry-run does not call CreateProject when project missing`` () =
    let fs                = MockFileSystem()
    let path              = Given.namedYamlFile fs "job.yml"
    let createProjectCalls = ConcurrentBag<unit>()
    let client =
        FakeGhClient.from
            { FakeGhClient.defaults with
                FindProject   = fun _ _ -> async { return None }
                CreateProject = fun _ _ ->
                    createProjectCalls.Add(())
                    async { return failwith "CreateProject must not be called in dry-run" } }
    let deps  = Given.deps fs client
    let input = A.RunInput.defaults () |> A.RunInput.withDryRun true

    let results = execute deps [path] input |> Async.RunSynchronously

    Assert.Empty(createProjectCalls)
    match results.[path] with
    | Error e -> Assert.Fail($"Expected Ok but got: {e}")
    | Ok result ->
        Assert.Equal(ProjectId "0", result.Lock.Project.Id)

[<Fact>]
let ``dry-run skips ReopenIssue and returns DryRunWouldReopen outcome`` () =
    let fs           = MockFileSystem()
    let path         = Given.namedYamlFile fs "job.yml"
    let reopenCalls  = ConcurrentBag<unit>()
    let client =
        FakeGhClient.from
            { FakeGhClient.defaults with
                FetchReposState = FakeGhClient.fetchReposStateReturning (fun r -> FakeGhClient.repoStateWithClosed r 7)
                ReopenIssue     = fun _ _ ->
                    reopenCalls.Add(())
                    async { return failwith "ReopenIssue must not be called in dry-run" } }
    let deps  = Given.deps fs client
    let input =
        A.RunInput.defaults ()
        |> A.RunInput.withDryRun true
        |> A.RunInput.withOnClosedIssue (Some Reopen)

    let results = execute deps [path] input |> Async.RunSynchronously

    Assert.Empty(reopenCalls)
    match results.[path] with
    | Error e -> Assert.Fail($"Expected Ok but got: {e}")
    | Ok result ->
        Assert.Contains(result.Results, fun r -> r.Outcome = DryRunWouldReopen)

[<Fact>]
let ``dry-run skips UpdateIssue in refreshBodies and returns DryRunWouldUpdate`` () =
    let fs   = MockFileSystem()
    let path = Given.namedYamlFile fs "job.yml"
    let repo = RepoName "myorg/repo-a"
    givenStaleTemplateLock fs path repo 42

    let updateCalls = ConcurrentBag<unit>()
    let client =
        FakeGhClient.from
            { FakeGhClient.defaults with
                FetchReposState = FakeGhClient.fetchReposStateReturning (fun r -> FakeGhClient.repoStateWithOpen r 42)
                UpdateIssue     = fun _ _ _ _ ->
                    updateCalls.Add(())
                    async { return failwith "UpdateIssue must not be called in dry-run" } }
    let deps  = Given.deps fs client
    let input =
        { A.RunInput.defaults () with SkipLock = false }
        |> A.RunInput.withDryRun true

    let results = execute deps [path] input |> Async.RunSynchronously

    Assert.Empty(updateCalls)
    match results.[path] with
    | Error e -> Assert.Fail($"Expected Ok but got: {e}")
    | Ok result ->
        Assert.Contains(result.Results, fun r -> r.Outcome = DryRunWouldUpdate)

[<Fact>]
let ``dry-run does not write the lock file`` () =
    let fs   = MockFileSystem()
    let path = Given.namedYamlFile fs "job.yml"
    let client = FakeGhClient.from FakeGhClient.defaults
    let deps  = Given.deps fs client
    let input =
        { A.RunInput.defaults () with SkipLock = false }
        |> A.RunInput.withDryRun true

    execute deps [path] input |> Async.RunSynchronously |> ignore

    let lockPath = path.Replace(".yml", ".lock.json")
    Assert.False((fs :> System.IO.Abstractions.IFileSystem).File.Exists(lockPath))

[<Fact>]
let ``dry-run still performs read-only lookups (FetchReposState, ListLabels)`` () =
    let fs              = MockFileSystem()
    let yaml =
        "job:\n  title: \"T\"\n  org: \"myorg\"\n" +
        "repos:\n  - \"repo-a\"\n" +
        "issue:\n  template: \"./template.md\"\n  labels: [\"bug\"]\n"
    let path            = Given.yamlFile fs yaml "# body"
    let fetchStateCalls = ConcurrentBag<unit>()
    let listLabelsCalls = ConcurrentBag<unit>()
    let client =
        FakeGhClient.from
            { FakeGhClient.defaults with
                FetchReposState = fun repos _ ->
                    fetchStateCalls.Add(())
                    async { return repos |> List.map (fun r -> r, Ok FakeGhClient.repoStateDefault) |> Map.ofList }
                ListLabels      = fun _ ->
                    listLabelsCalls.Add(())
                    async { return Ok [] } }
    let deps  = Given.deps fs client
    let input =
        A.RunInput.defaults ()
        |> A.RunInput.withDryRun true
        |> A.RunInput.withAutoCreateLabels true

    execute deps [path] input |> Async.RunSynchronously |> ignore

    Assert.NotEmpty(fetchStateCalls)
    Assert.NotEmpty(listLabelsCalls)

// ---------------------------------------------------------------------------
// Two-run template-update flow (mirrors the integration test scenario)
// ---------------------------------------------------------------------------

[<Fact>]
let ``re-run after template file content changes calls UpdateIssue with new body`` () =
    let fs   = MockFileSystem()
    let path = Given.namedYamlFile fs "job.yml"

    // First run: no existing issue, so CreateIssue is called and the lock is written.
    let firstClient =
        FakeGhClient.from
            { FakeGhClient.defaults with
                FetchReposState = FakeGhClient.fetchReposStateReturning (fun _ -> FakeGhClient.repoStateDefault)
                CreateIssue     = fun r _ _ _ -> async { return Ok (FakeGhClient.issueFor r 42) } }
    execute (Given.deps fs firstClient) [path] { A.RunInput.defaults () with SkipLock = false }
    |> Async.RunSynchronously |> ignore

    // Change the template content on disk — simulates what the integration test does.
    (fs :> System.IO.Abstractions.IFileSystem).File.WriteAllText("/work/template.md", "## Updated body")

    // Second run: issue #42 is open; the template hash now differs from the lock.
    let updateBodies = System.Collections.Concurrent.ConcurrentBag<string>()
    let secondClient =
        FakeGhClient.from
            { FakeGhClient.defaults with
                FetchReposState = FakeGhClient.fetchReposStateReturning (fun r -> FakeGhClient.repoStateWithOpen r 42)
                CreateIssue     = fun _ _ _ _ -> async { return failwith "CreateIssue not expected on second run" }
                UpdateIssue     = fun _ _ _ body ->
                    updateBodies.Add(body)
                    async { return Ok () } }
    let results =
        execute (Given.deps fs secondClient) [path] { A.RunInput.defaults () with SkipLock = false }
        |> Async.RunSynchronously

    Assert.NotEmpty(updateBodies)
    Assert.Contains("Updated body", updateBodies |> Seq.head)
    match results.[path] with
    | Error e -> Assert.Fail($"Expected Ok but got: {e}")
    | Ok result -> Assert.Contains(result.Results, fun r -> r.Outcome = Updated)

[<Fact>]
let ``re-run without template change does not call UpdateIssue`` () =
    let fs   = MockFileSystem()
    let path = Given.namedYamlFile fs "job.yml"

    // First run: creates issue and writes lock.
    let firstClient =
        FakeGhClient.from
            { FakeGhClient.defaults with
                FetchReposState = FakeGhClient.fetchReposStateReturning (fun _ -> FakeGhClient.repoStateDefault)
                CreateIssue     = fun r _ _ _ -> async { return Ok (FakeGhClient.issueFor r 42) } }
    execute (Given.deps fs firstClient) [path] { A.RunInput.defaults () with SkipLock = false }
    |> Async.RunSynchronously |> ignore

    // Second run: template unchanged, so hashes match — UpdateIssue must not be called.
    let updateCalls = System.Collections.Concurrent.ConcurrentBag<unit>()
    let secondClient =
        FakeGhClient.from
            { FakeGhClient.defaults with
                FetchReposState = FakeGhClient.fetchReposStateReturning (fun r -> FakeGhClient.repoStateWithOpen r 42)
                UpdateIssue     = fun _ _ _ _ ->
                    updateCalls.Add(())
                    async { return failwith "UpdateIssue must not be called when template is unchanged" } }
    let results =
        execute (Given.deps fs secondClient) [path] { A.RunInput.defaults () with SkipLock = false }
        |> Async.RunSynchronously

    Assert.Empty(updateCalls)
    match results.[path] with
    | Error e -> Assert.Fail($"Expected Ok but got: {e}")
    | Ok result -> Assert.Contains(result.Results, fun r -> r.Outcome = AlreadyExisted)

// ---------------------------------------------------------------------------
// dependsOn integration tests
// ---------------------------------------------------------------------------

let private repoA = RepoName "myorg/repo-a"
let private repoB = RepoName "myorg/repo-b"

/// Write a bare-minimum valid YAML (no deps) and return its path.
let private writeUpstream (fs: MockFileSystem) (name: string) =
    let dir = "/work"
    fs.Directory.CreateDirectory(dir) |> ignore
    if not ((fs :> System.IO.Abstractions.IFileSystem).File.Exists($"{dir}/template.md")) then
        fs.File.WriteAllText($"{dir}/template.md", "# body")
    let yaml =
        "job:\n  title: \"Upstream\"\n  org: \"myorg\"\n" +
        "repos:\n  - \"repo-a\"\n  - \"repo-b\"\n" +
        "issue:\n  template: \"./template.md\"\n  labels: []\n"
    let path = $"{dir}/{name}"
    fs.File.WriteAllText(path, yaml)
    path

/// Write a downstream YAML that depends on `upstreamRelPath` with the given scope.
let private writeDownstream (fs: MockFileSystem) (name: string) (upstreamRelPath: string) (scope: string) =
    let dir = "/work"
    let yaml =
        "job:\n  title: \"Downstream\"\n  org: \"myorg\"\n" +
        "repos:\n  - \"repo-a\"\n  - \"repo-b\"\n" +
        "issue:\n  template: \"./template.md\"\n  labels: []\n" +
        "dependsOn:\n" +
        $"  - job: {upstreamRelPath}\n" +
        "    condition: pr_merged\n" +
        $"    scope: {scope}\n"
    let path = $"{dir}/{name}"
    fs.File.WriteAllText(path, yaml)
    path

/// Write a lock file for an upstream job. Returns the YAML path for convenience.
let private writeLockFor
    (fs: MockFileSystem)
    (yamlPath: string)
    (repos: RepoName list)
    (issues: (RepoName * int) list)
    (prs: (RepoName * int * int * string) list)
    =
    let project = { Org = OrgName "myorg"; Id = ProjectId "1"; Title = "Upstream"; Url = "" }
    let lock : LockFile =
        { LockedAt     = System.DateTimeOffset.MinValue
          YamlHash     = "h"
          TemplateHash = "h"
          Project      = project
          Repos        = repos
          Issues       = issues |> List.map (fun (repo, num) ->
                             let (RepoName r) = repo
                             { Repo = repo; Id = IssueId (string num)
                               Url  = $"https://github.com/{r}/issues/{num}"
                               Assignees = [] })
          PullRequests  = prs |> List.map (fun (repo, prNum, issueNum, state) ->
                              let (RepoName r) = repo
                              { Repo        = repo
                                Number      = PrNumber prNum
                                Url         = $"https://github.com/{r}/pull/{prNum}"
                                ClosesIssue = IssueId (string issueNum)
                                State       = state })
          SkippedRepos  = []
          Failures      = [] }
    OrcAI.Core.LockFile.write (fs :> System.IO.Abstractions.IFileSystem) yamlPath lock

[<Fact>]
let ``execute per_repo dep filter runs only eligible repos`` () =
    let fs       = MockFileSystem()
    let upPath   = writeUpstream fs "upstream.yml"
    // Lock shows repo-a has a merged PR; repo-b does not.
    writeLockFor fs upPath
        [ repoA; repoB ]
        [ (repoA, 10); (repoB, 20) ]
        [ (repoA, 1, 10, "MERGED") ]
    let downPath = writeDownstream fs "downstream.yml" "./upstream.yml" "per_repo"
    let client =
        FakeGhClient.from
            { FakeGhClient.defaults with
                FindPrsForIssue = fun _ _ -> async { return [] } }
    let deps  = Given.deps fs client
    // DryRun = true prevents the chain from overwriting the pre-written upstream lock.
    let input = A.RunInput.defaults () |> A.RunInput.withDryRun true

    let results = execute deps [downPath] input |> Async.RunSynchronously

    match results.[downPath] with
    | Error e -> Assert.Fail($"Expected Ok but got Error: {e}")
    | Ok result ->
        Assert.Null(result.BlockedBy |> Option.toObj)
        // Only repo-a should be processed; repo-b is filtered by the dep condition.
        let processedRepos = result.Results |> List.map (fun r -> r.Issue.Repo)
        Assert.Contains(repoA, processedRepos)
        Assert.DoesNotContain(repoB, processedRepos)

[<Fact>]
let ``execute all_repos dep gate sets BlockedBy when condition not met`` () =
    let fs       = MockFileSystem()
    let upPath   = writeUpstream fs "upstream.yml"
    // Lock shows repo-a exists but has no merged PR → all_repos gate fails.
    writeLockFor fs upPath
        [ repoA ]
        [ (repoA, 10) ]
        []   // no merged PRs
    let downPath = writeDownstream fs "downstream.yml" "./upstream.yml" "all_repos"
    let client =
        FakeGhClient.from
            { FakeGhClient.defaults with
                FindPrsForIssue = fun _ _ -> async { return [] } }
    let deps  = Given.deps fs client
    let input = A.RunInput.defaults ()

    let results = execute deps [downPath] input |> Async.RunSynchronously

    match results.[downPath] with
    | Error e -> Assert.Fail($"Expected Ok but got Error: {e}")
    | Ok result ->
        Assert.True(result.BlockedBy.IsSome, "Expected BlockedBy to be set")
        Assert.Empty(result.Results)

[<Fact>]
let ``execute dependency chain runs dep before downstream`` () =
    // `writeUpstream`/`writeDownstream` build Unix-style paths ("/work/...")
    // and this test asserts against the literal `depPath` they return, so the
    // filesystem must simulate Linux regardless of the host/CI runner OS —
    // otherwise Windows hosts resolve the dependency to a drive-rooted path
    // (e.g. "D:\work\dep.yml") that no longer matches `depPath`.
    let fs = new MockFileSystem(fun o -> o.SimulatingOperatingSystem(SimulationMode.Linux))
    let depPath  = writeUpstream fs "dep.yml"
    let mainPath = writeDownstream fs "main.yml" "./dep.yml" "per_repo"
    let client =
        FakeGhClient.from
            { FakeGhClient.defaults with
                FindPrsForIssue = fun _ _ -> async { return [] } }
    let deps  = Given.deps fs client
    let input = A.RunInput.defaults ()

    // Pass only the downstream YAML; the dep should be resolved automatically.
    let results = execute deps [mainPath] input |> Async.RunSynchronously

    // Both the dep and the downstream should appear in the result map.
    Assert.True(results.ContainsKey(depPath),  "dep.yml should appear in results")
    Assert.True(results.ContainsKey(mainPath), "main.yml should appear in results")

// ---------------------------------------------------------------------------
// provider: local — the auth precheck must be skipped entirely
// ---------------------------------------------------------------------------

[<Fact>]
let ``execute never calls AuthContext.GetToken for a provider: local job`` () =
    let fs   = MockFileSystem()
    let yaml = A.Yaml.valid + "provider:\n  type: local\n"
    let path = Given.yamlFile fs yaml "# body"
    let deps = Given.deps fs (FakeGhClient.from FakeGhClient.defaults) |> Given.withNeverCalledAuth

    let results = execute deps [path] (A.RunInput.defaults ()) |> Async.RunSynchronously

    Assert.True(results |> Map.forall (fun _ r -> match r with Ok _ -> true | Error _ -> false))

// ---------------------------------------------------------------------------
// buildPrCreatePsi — the `gh pr create` ProcessStartInfo builder used by openPr.
// GH_TOKEN must be set on the child process iff the resolved token is non-empty,
// so App/PAT auth reaches `gh` without relying on ambient credentials.
// ---------------------------------------------------------------------------

[<Fact>]
let ``buildPrCreatePsi sets GH_TOKEN when the token is non-empty`` () =
    let psi = buildPrCreatePsi "resolved-token" "myorg/repo-a" "head-branch" "title" "body" "/work"
    Assert.Equal("resolved-token", psi.Environment["GH_TOKEN"])

[<Fact>]
let ``buildPrCreatePsi sets no GH_TOKEN when the token is empty`` () =
    let psi = buildPrCreatePsi "" "myorg/repo-a" "head-branch" "title" "body" "/work"
    Assert.False(psi.Environment.ContainsKey("GH_TOKEN"))

[<Fact>]
let ``buildPrCreatePsi passes repo, head, title and body as gh pr create arguments`` () =
    let psi = buildPrCreatePsi "t" "myorg/repo-a" "head-branch" "My Title" "My Body" "/work"
    let args = psi.ArgumentList |> List.ofSeq
    Assert.Equal<string list>(
        ["pr"; "create"; "--repo"; "myorg/repo-a"; "--head"; "head-branch"; "--title"; "My Title"; "--body"; "My Body"],
        args)

// ---------------------------------------------------------------------------
// appendClosesIssue — cmd-to-github PRs must reference the issue with a closing
// keyword or GitHub never links them (no `closingPullRequests` entry, no
// auto-close on merge), regardless of orcai's own in-memory ClosesIssue bookkeeping.
// ---------------------------------------------------------------------------

[<Fact>]
let ``appendClosesIssue adds a Closes line to an empty body`` () =
    Assert.Equal("Closes #42", appendClosesIssue "42" "")

[<Fact>]
let ``appendClosesIssue appends a Closes line after an existing body`` () =
    Assert.Equal("My PR description.\n\nCloses #42", appendClosesIssue "42" "My PR description.")

[<Fact>]
let ``appendClosesIssue does not duplicate when the body already references the issue`` () =
    Assert.Equal("See #42 for context.", appendClosesIssue "42" "See #42 for context.")

// ---------------------------------------------------------------------------
// decideCmdToGithubAction — pure idempotency decision table for cmd-to-github
// (plans/cmd-to-github-idempotency.md). No I/O: correctness here holds
// regardless of whether the lock file exists, is stale, or was deleted —
// every row is driven only by the passed-in live state.
// ---------------------------------------------------------------------------

let private prWith state = A.PullRequestRef.defaults (RepoName "myorg/repo-a") 3 7 |> A.PullRequestRef.withState state

[<Fact>]
let ``decideCmdToGithubAction skips entirely when an open PR exists and hashes are unchanged`` () =
    let pr = prWith "OPEN"
    let result = decideCmdToGithubAction "orcai/job" [pr] SkipClosedPr false false
    Assert.Equal(SkipIdempotent (Some pr), result)

[<Fact>]
let ``decideCmdToGithubAction redoes the full run when an open PR exists but hashes changed`` () =
    let pr = prWith "OPEN"
    let result = decideCmdToGithubAction "orcai/job" [pr] SkipClosedPr true false
    Assert.Equal(ProceedFullRun, result)

[<Fact>]
let ``decideCmdToGithubAction skips when a merged PR exists, regardless of hash state`` () =
    let pr = prWith "MERGED"
    Assert.Equal(SkipIdempotent (Some pr), decideCmdToGithubAction "orcai/job" [pr] SkipClosedPr false false)
    Assert.Equal(SkipIdempotent (Some pr), decideCmdToGithubAction "orcai/job" [pr] SkipClosedPr true false)

[<Fact>]
let ``decideCmdToGithubAction skips a closed PR by default (onClosedPr skip)`` () =
    let pr = prWith "CLOSED"
    let result = decideCmdToGithubAction "orcai/job" [pr] SkipClosedPr true false
    Assert.Equal(SkipIdempotent (Some pr), result)

[<Fact>]
let ``decideCmdToGithubAction redoes the full run for a closed PR when onClosedPr is recreate`` () =
    let pr = prWith "CLOSED"
    let result = decideCmdToGithubAction "orcai/job" [pr] RecreatePr false false
    Assert.Equal(ProceedFullRun, result)

[<Fact>]
let ``decideCmdToGithubAction reopens for a closed PR when onClosedPr is reopen`` () =
    let pr = prWith "CLOSED"
    let result = decideCmdToGithubAction "orcai/job" [pr] ReopenPr false false
    Assert.Equal(ReopenAndRedo pr, result)

[<Fact>]
let ``decideCmdToGithubAction fails for a closed PR when onClosedPr is fail`` () =
    let pr = prWith "CLOSED"
    let result = decideCmdToGithubAction "orcai/job" [pr] FailOnClosedPr false false
    Assert.Equal(FailClosedPr pr, result)

[<Fact>]
let ``decideCmdToGithubAction retries pr create only when no PR is found but the branch exists and hashes are unchanged`` () =
    let result = decideCmdToGithubAction "orcai/job" [] SkipClosedPr false true
    Assert.Equal(RetryPrCreateOnly "orcai/job", result)

[<Fact>]
let ``decideCmdToGithubAction redoes the full run when no PR is found and the branch exists but hashes changed`` () =
    let result = decideCmdToGithubAction "orcai/job" [] SkipClosedPr true true
    Assert.Equal(ProceedFullRun, result)

[<Fact>]
let ``decideCmdToGithubAction redoes the full run when no PR is found and no branch exists`` () =
    Assert.Equal(ProceedFullRun, decideCmdToGithubAction "orcai/job" [] SkipClosedPr false false)
    Assert.Equal(ProceedFullRun, decideCmdToGithubAction "orcai/job" [] SkipClosedPr true false)

// ---------------------------------------------------------------------------
// runExecCommand — closes the child's stdin so processes that eagerly read
// stdin to EOF before doing anything (e.g. `opencode run` with no TTY) don't
// hang or silently no-op on an inherited, non-EOF-terminated stdin handle.
// `cat`/`more` (no args) block reading stdin until EOF as a portable stand-in.
// Note: whether this test actually demonstrates the hang pre-fix depends on
// the ambient stdin of the process running the test — if that's already at
// EOF (e.g. some CI runners redirect step stdin from /dev/null), the bug
// won't reproduce here even unfixed. It reliably hangs when run from an
// interactive shell with a real TTY stdin.
// ---------------------------------------------------------------------------

[<Fact>]
let ``runExecCommand closes child stdin so a process reading stdin to EOF exits immediately instead of hanging`` () =
    let exe, args =
        if System.OperatingSystem.IsWindows() then "cmd", ["/C"; "more"]
        else "cat", []
    let task = runExecCommand exe args (Path.GetTempPath()) |> Async.StartAsTask
    let finished = task.Wait(System.TimeSpan.FromSeconds(5.0))
    Assert.True(finished, "runExecCommand did not return within 5s — the child is blocked reading an inherited, non-EOF stdin handle")
    let exitCode, _ = task.Result
    Assert.Equal(0, exitCode)

// ---------------------------------------------------------------------------
// buildExecPsi — PWD must be overridden to match workingDir, because
// WorkingDirectory only chdir's the child; it doesn't update the inherited
// PWD env var. Confirmed against a real `opencode run` invocation that it
// otherwise resolves its project root from the stale inherited PWD instead of
// the real cwd, writing files outside the intended checkout entirely.
// ---------------------------------------------------------------------------

[<Fact>]
let ``buildExecPsi sets PWD to match workingDir so children that trust inherited PWD over the real cwd aren't misdirected`` () =
    let psi = buildExecPsi "opencode" ["run"] "/some/checkout/path"
    Assert.Equal("/some/checkout/path", psi.WorkingDirectory)
    Assert.Equal("/some/checkout/path", psi.Environment["PWD"])

// ---------------------------------------------------------------------------
// Regression: the resolved GitHub token now round-trips through ProcessParams
// into a cmd-checkout action (previously discarded via `Result.map ignore`).
// Uses a real local (non-network) bare git repo so ensureClone/getWorktree
// exercise the actual CheckoutManager plumbing without contacting github.com.
// ---------------------------------------------------------------------------

[<Fact>]
let ``execute round-trips the resolved token through a cmd-checkout action without regressing existing behaviour`` () =
    let fs   = MockFileSystem()
    let yaml =
        "job:\n  title: \"Checkout Job\"\n  org: \"myorg\"\n" +
        "repos:\n  - \"repo-a\"\n" +
        "issue:\n  template: \"TEMPLATE_PLACEHOLDER\"\n  labels: []\n" +
        "action:\n  type: cmd-checkout\n  execute: \"echo hi\"\n"
    let path   = Given.yamlFile fs yaml "# body"
    let deps   = Given.deps fs (FakeGhClient.from FakeGhClient.defaults) // AuthContext.GetToken() = Ok "fake-token"

    let guid         = System.Guid.NewGuid().ToString("N")
    let checkoutRoot = Path.Combine(Path.GetTempPath(), $"orcai-ckt-{guid}")
    let seedDir      = Path.Combine(Path.GetTempPath(), $"orcai-ckt-seed-{guid}")
    let baseDir      = OrcAI.Core.CheckoutManager.basePath checkoutRoot (RepoName "myorg/repo-a")
    let run (exe: string) (args: string list) (wd: string) =
        let psi = ProcessStartInfo(exe)
        psi.WorkingDirectory       <- wd
        psi.RedirectStandardOutput <- true
        psi.RedirectStandardError  <- true
        psi.UseShellExecute        <- false
        for a in args do psi.ArgumentList.Add(a)
        use p = Process.Start(psi)
        p.WaitForExit()
        p.ExitCode = 0
    try
        // Seed a real, network-free bare git repo at the exact path ensureClone
        // expects, so it short-circuits ("already cloned") and getWorktree/the
        // cmd exec run against a genuine local repo — proving CheckoutToken
        // flows through processRepo/ProcessParams without an arity regression.
        Directory.CreateDirectory(seedDir) |> ignore
        Assert.True(run "git" ["-c"; "init.defaultBranch=main"; "init"; seedDir] (Path.GetTempPath()), "git init seed")
        File.WriteAllText(Path.Combine(seedDir, "README.md"), "init")
        Assert.True(run "git" ["add"; "README.md"] seedDir, "git add in seed")
        Assert.True(run "git" ["-c"; "user.email=t@t.com"; "-c"; "user.name=t"; "commit"; "-m"; "init"] seedDir, "git commit in seed")
        Directory.CreateDirectory(Path.GetDirectoryName(baseDir)) |> ignore
        Assert.True(run "git" ["clone"; "--bare"; seedDir; baseDir] (Path.GetTempPath()), "git clone bare as base")

        let input   = { A.RunInput.defaults () with CheckoutRoot = Some checkoutRoot }
        let results = execute deps [path] input |> Async.RunSynchronously

        match results.TryFind path with
        | Some (Ok runResult) -> Assert.Empty(runResult.Lock.Failures)
        | Some (Error e)      -> Assert.Fail($"Expected Ok RunResult, got Error: {e}")
        | None                -> Assert.Fail("Expected an entry for the yaml path")
    finally
        try Directory.Delete(seedDir, true) with _ -> ()
        try Directory.Delete(checkoutRoot, true) with _ -> ()

// ---------------------------------------------------------------------------
// Regression: a cmd-to-github push that fails once must not fail forever.
// Success is never explicitly `record`ed for CmdToGithubPushFailed/OpenPrFailed,
// so a stale failure from an earlier run stuck around in the lock file even
// after a later run's push/PR genuinely succeeded — every subsequent `orcai run`
// kept reporting the old failure, forever, even though nothing was actually wrong.
// ---------------------------------------------------------------------------

[<Fact>]
let ``execute clears a stale CmdToGithubPushFailed failure once the push succeeds`` () =
    let fs   = MockFileSystem()
    let repo = RepoName "myorg/repo-a"
    let yaml =
        "job:\n  title: \"Push Job\"\n  org: \"myorg\"\n" +
        "repos:\n  - \"repo-a\"\n" +
        "issue:\n  template: \"TEMPLATE_PLACEHOLDER\"\n  labels: []\n" +
        "action:\n  type: cmd-to-github\n  execute: \"touch changed.txt\"\n" +
        "  writeBack: push-branch\n  branch: \"orcai/test-push-branch\"\n" +
        "  commitMessage: \"test commit\"\n  errorIfNoDiff: true\n"
    let path = Given.yamlFile fs yaml "# body"

    // Prior lock: same YAML/template hash (so the run takes the "prior failures —
    // retrying" path, not a hash-changed re-run) but with a stale push failure —
    // simulating an earlier run whose push genuinely failed.
    let yamlHash     = OrcAI.Core.YamlConfig.computeHash (fs :> System.IO.Abstractions.IFileSystem) path
    let templateHash =
        match OrcAI.Core.YamlConfig.resolveTemplatePath (fs :> System.IO.Abstractions.IFileSystem) path with
        | Some p -> OrcAI.Core.YamlConfig.computeTemplateHash (fs :> System.IO.Abstractions.IFileSystem) p
        | None   -> ""
    let staleFailure =
        { Repo          = repo
          Category      = CmdToGithubPushFailed
          Cause         = Unknown
          Attempts      = 1
          FirstFailedAt = System.DateTimeOffset(2026, 1, 1, 0, 0, 0, System.TimeSpan.Zero)
          LastFailedAt  = System.DateTimeOffset(2026, 1, 1, 0, 0, 0, System.TimeSpan.Zero)
          LastMessage   = "git push failed: Exit 1: ... (stale info)" }
    let priorLock =
        { A.LockFile.defaults () with
            YamlHash     = yamlHash
            TemplateHash = templateHash
            Repos        = [ repo ]
            Issues       = [ FakeGhClient.issueFor repo 42 ]
            PullRequests = []
            Failures     = [ staleFailure ] }
    OrcAI.Core.LockFile.write (fs :> System.IO.Abstractions.IFileSystem) path priorLock

    let client =
        FakeGhClient.from
            { FakeGhClient.defaults with
                FetchReposState = FakeGhClient.fetchReposStateReturning (fun r -> FakeGhClient.repoStateWithOpen r 42) }
    let deps = Given.deps fs client

    let guid         = System.Guid.NewGuid().ToString("N")
    let checkoutRoot = Path.Combine(Path.GetTempPath(), $"orcai-clr-{guid}")
    let seedDir      = Path.Combine(Path.GetTempPath(), $"orcai-clr-seed-{guid}")
    let baseDir      = OrcAI.Core.CheckoutManager.basePath checkoutRoot repo
    let run (exe: string) (args: string list) (wd: string) =
        let psi = ProcessStartInfo(exe)
        psi.WorkingDirectory       <- wd
        psi.RedirectStandardOutput <- true
        psi.RedirectStandardError  <- true
        psi.UseShellExecute        <- false
        for a in args do psi.ArgumentList.Add(a)
        use p = Process.Start(psi)
        p.WaitForExit()
        p.ExitCode = 0
    try
        // Seed a real, network-free "origin" so ensureClone short-circuits and
        // pushToOrigin has something local to push to (mirrors the cmd-checkout
        // round-trip test above).
        Directory.CreateDirectory(seedDir) |> ignore
        Assert.True(run "git" ["-c"; "init.defaultBranch=main"; "init"; seedDir] (Path.GetTempPath()), "git init seed")
        File.WriteAllText(Path.Combine(seedDir, "README.md"), "init")
        Assert.True(run "git" ["add"; "README.md"] seedDir, "git add in seed")
        Assert.True(run "git" ["-c"; "user.email=t@t.com"; "-c"; "user.name=t"; "commit"; "-m"; "init"] seedDir, "git commit in seed")
        Directory.CreateDirectory(Path.GetDirectoryName(baseDir)) |> ignore
        Assert.True(run "git" ["clone"; "--bare"; seedDir; baseDir] (Path.GetTempPath()), "git clone bare as base")

        let input   = { A.RunInput.defaults () with SkipLock = false; CheckoutRoot = Some checkoutRoot }
        let results = execute deps [path] input |> Async.RunSynchronously

        match results.TryFind path with
        | Some (Ok runResult) ->
            Assert.DoesNotContain(runResult.Lock.Failures, fun f -> f.Category = CmdToGithubPushFailed)
        | Some (Error e) -> Assert.Fail($"Expected Ok RunResult, got Error: {e}")
        | None           -> Assert.Fail("Expected an entry for the yaml path")
    finally
        try Directory.Delete(seedDir, true) with _ -> ()
        try Directory.Delete(checkoutRoot, true) with _ -> ()

// ---------------------------------------------------------------------------
// cmd-to-github idempotency — integration coverage for the decision table
// wiring end to end (see plans/cmd-to-github-idempotency.md). These assert no
// checkout ever happens (checkoutRoot is never even created) for the
// skip-entirely rows, proving the clone/execute/push pipeline is genuinely
// bypassed rather than merely producing the same end result.
// ---------------------------------------------------------------------------

[<Fact>]
let ``execute skips cmd-to-github entirely when the PR is merged, even with no lock file present`` () =
    let fs   = MockFileSystem()
    let repo = RepoName "myorg/repo-a"
    let yaml =
        "job:\n  title: \"Merged PR Job\"\n  org: \"myorg\"\n" +
        "repos:\n  - \"repo-a\"\n" +
        "issue:\n  template: \"TEMPLATE_PLACEHOLDER\"\n  labels: []\n" +
        "action:\n  type: cmd-to-github\n  execute: \"touch changed.txt\"\n"
    let path = Given.yamlFile fs yaml "# body"
    let mergedPr = A.PullRequestRef.defaults repo 3 42 |> A.PullRequestRef.withState "MERGED"
    let client =
        FakeGhClient.from
            { FakeGhClient.defaults with
                FetchReposState = FakeGhClient.fetchReposStateReturning (fun r -> FakeGhClient.repoStateWithOpen r 42)
                FindPrsForIssue = fun _ _ -> async { return [ mergedPr ] } }
    let deps = Given.deps fs client

    let guid         = System.Guid.NewGuid().ToString("N")
    let checkoutRoot = Path.Combine(Path.GetTempPath(), $"orcai-merged-{guid}")
    try
        let input   = { A.RunInput.defaults () with SkipLock = true; CheckoutRoot = Some checkoutRoot }
        let results = execute deps [path] input |> Async.RunSynchronously
        match results.TryFind path with
        | Some (Ok runResult) ->
            Assert.False(Directory.Exists(checkoutRoot), "cmd-to-github should never have cloned for a merged PR")
            Assert.Contains(runResult.Lock.PullRequests, fun pr -> pr.Repo = repo && pr.State = "MERGED")
        | Some (Error e) -> Assert.Fail($"Expected Ok RunResult, got Error: {e}")
        | None           -> Assert.Fail("Expected an entry for the yaml path")
    finally
        try Directory.Delete(checkoutRoot, true) with _ -> ()

[<Fact>]
let ``execute skips cmd-to-github entirely when an open PR exists and hashes are unchanged`` () =
    let fs   = MockFileSystem()
    let repo = RepoName "myorg/repo-a"
    let yaml =
        "job:\n  title: \"Open PR Job\"\n  org: \"myorg\"\n" +
        "repos:\n  - \"repo-a\"\n" +
        "issue:\n  template: \"TEMPLATE_PLACEHOLDER\"\n  labels: []\n" +
        "action:\n  type: cmd-to-github\n  execute: \"touch changed.txt\"\n"
    let path = Given.yamlFile fs yaml "# body"
    let openPrLive = A.PullRequestRef.defaults repo 3 42 |> A.PullRequestRef.withState "OPEN"

    let yamlHash     = OrcAI.Core.YamlConfig.computeHash (fs :> System.IO.Abstractions.IFileSystem) path
    let templateHash =
        match OrcAI.Core.YamlConfig.resolveTemplatePath (fs :> System.IO.Abstractions.IFileSystem) path with
        | Some p -> OrcAI.Core.YamlConfig.computeTemplateHash (fs :> System.IO.Abstractions.IFileSystem) p
        | None   -> ""
    let priorLock =
        { A.LockFile.defaults () with
            YamlHash     = yamlHash
            TemplateHash = templateHash
            Repos        = [ repo ]
            Issues       = [ FakeGhClient.issueFor repo 42 ]
            PullRequests = [ openPrLive ]
            Failures     = [] }
    OrcAI.Core.LockFile.write (fs :> System.IO.Abstractions.IFileSystem) path priorLock

    let client =
        FakeGhClient.from
            { FakeGhClient.defaults with
                FetchReposState = FakeGhClient.fetchReposStateReturning (fun r -> FakeGhClient.repoStateWithOpen r 42)
                FindPrsForIssue = fun _ _ -> async { return [ openPrLive ] } }
    let deps = Given.deps fs client

    let guid         = System.Guid.NewGuid().ToString("N")
    let checkoutRoot = Path.Combine(Path.GetTempPath(), $"orcai-openunchanged-{guid}")
    try
        let input   = { A.RunInput.defaults () with SkipLock = false; CheckoutRoot = Some checkoutRoot }
        let results = execute deps [path] input |> Async.RunSynchronously
        match results.TryFind path with
        | Some (Ok runResult) ->
            Assert.False(Directory.Exists(checkoutRoot), "cmd-to-github should never have cloned for an unchanged open PR")
            Assert.Contains(runResult.Lock.PullRequests, fun pr -> pr.Repo = repo && pr.State = "OPEN")
        | Some (Error e) -> Assert.Fail($"Expected Ok RunResult, got Error: {e}")
        | None           -> Assert.Fail("Expected an entry for the yaml path")
    finally
        try Directory.Delete(checkoutRoot, true) with _ -> ()

// ---------------------------------------------------------------------------
// Regression: deleting the lock file (or --skip-lock) forces yamlHashChanged/
// templateHashChanged to true for other purposes (shouldAttempt retry gating),
// but that must not force cmd-to-github to redo (and force-push) an already-open,
// content-unchanged PR just because there is no lock file to compare hashes
// against. Reproduces a real bug report: re-running with the lock file deleted,
// nothing else changed, still force-pushed a fresh AI-agent commit onto the PR.
// ---------------------------------------------------------------------------

[<Fact>]
let ``execute skips cmd-to-github entirely for an open PR when there is no lock file at all`` () =
    let fs   = MockFileSystem()
    let repo = RepoName "myorg/repo-a"
    let yaml =
        "job:\n  title: \"Open PR No Lock Job\"\n  org: \"myorg\"\n" +
        "repos:\n  - \"repo-a\"\n" +
        "issue:\n  template: \"TEMPLATE_PLACEHOLDER\"\n  labels: []\n" +
        "action:\n  type: cmd-to-github\n  execute: \"touch changed.txt\"\n"
    let path = Given.yamlFile fs yaml "# body"
    let openPrLive = A.PullRequestRef.defaults repo 3 42 |> A.PullRequestRef.withState "OPEN"

    // No prior lock file is written at all — this is the "deleted the lock file"
    // scenario, not just an unchanged one.
    let client =
        FakeGhClient.from
            { FakeGhClient.defaults with
                FetchReposState = FakeGhClient.fetchReposStateReturning (fun r -> FakeGhClient.repoStateWithOpen r 42)
                FindPrsForIssue = fun _ _ -> async { return [ openPrLive ] } }
    let deps = Given.deps fs client

    let guid         = System.Guid.NewGuid().ToString("N")
    let checkoutRoot = Path.Combine(Path.GetTempPath(), $"orcai-openunchanged-nolock-{guid}")
    try
        let input   = { A.RunInput.defaults () with SkipLock = false; CheckoutRoot = Some checkoutRoot }
        let results = execute deps [path] input |> Async.RunSynchronously
        match results.TryFind path with
        | Some (Ok runResult) ->
            Assert.False(Directory.Exists(checkoutRoot), "cmd-to-github should never have cloned for an unchanged open PR, even with no lock file present")
            Assert.Contains(runResult.Lock.PullRequests, fun pr -> pr.Repo = repo && pr.State = "OPEN")
        | Some (Error e) -> Assert.Fail($"Expected Ok RunResult, got Error: {e}")
        | None           -> Assert.Fail("Expected an entry for the yaml path")
    finally
        try Directory.Delete(checkoutRoot, true) with _ -> ()

[<Fact>]
let ``execute records a manual-intervention failure for a closed PR when onClosedPr is fail`` () =
    let fs   = MockFileSystem()
    let repo = RepoName "myorg/repo-a"
    let yaml =
        "job:\n  title: \"Closed PR Fail Job\"\n  org: \"myorg\"\n" +
        "repos:\n  - \"repo-a\"\n" +
        "issue:\n  template: \"TEMPLATE_PLACEHOLDER\"\n  labels: []\n" +
        "action:\n  type: cmd-to-github\n  execute: \"touch changed.txt\"\n  onClosedPr: fail\n"
    let path = Given.yamlFile fs yaml "# body"
    let closedPr = A.PullRequestRef.defaults repo 3 42 |> A.PullRequestRef.withState "CLOSED"
    let client =
        FakeGhClient.from
            { FakeGhClient.defaults with
                FetchReposState = FakeGhClient.fetchReposStateReturning (fun r -> FakeGhClient.repoStateWithOpen r 42)
                FindPrsForIssue = fun _ _ -> async { return [ closedPr ] } }
    let deps = Given.deps fs client

    let guid         = System.Guid.NewGuid().ToString("N")
    let checkoutRoot = Path.Combine(Path.GetTempPath(), $"orcai-closedfail-{guid}")
    try
        let input   = { A.RunInput.defaults () with SkipLock = true; CheckoutRoot = Some checkoutRoot }
        let results = execute deps [path] input |> Async.RunSynchronously
        match results.TryFind path with
        | Some (Ok runResult) ->
            Assert.False(Directory.Exists(checkoutRoot), "cmd-to-github should never have cloned when onClosedPr=fail")
            Assert.Contains(runResult.Lock.Failures, fun f -> f.Repo = repo && f.Category = CmdToGithubClosedPrFailed)
        | Some (Error e) -> Assert.Fail($"Expected Ok RunResult, got Error: {e}")
        | None           -> Assert.Fail("Expected an entry for the yaml path")
    finally
        try Directory.Delete(checkoutRoot, true) with _ -> ()

[<Fact>]
let ``execute reopens the PR and pushes fresh content when onClosedPr is reopen`` () =
    let fs   = MockFileSystem()
    let repo = RepoName "myorg/repo-a"
    let yaml =
        "job:\n  title: \"Closed PR Reopen Job\"\n  org: \"myorg\"\n" +
        "repos:\n  - \"repo-a\"\n" +
        "issue:\n  template: \"TEMPLATE_PLACEHOLDER\"\n  labels: []\n" +
        "action:\n  type: cmd-to-github\n  execute: \"touch changed.txt\"\n" +
        "  branch: \"orcai/reopen-test\"\n  onClosedPr: reopen\n"
    let path = Given.yamlFile fs yaml "# body"
    let closedPr = A.PullRequestRef.defaults repo 3 42 |> A.PullRequestRef.withState "CLOSED"
    let reopenCalls = ConcurrentBag<PrNumber>()
    let client =
        FakeGhClient.from
            { FakeGhClient.defaults with
                FetchReposState = FakeGhClient.fetchReposStateReturning (fun r -> FakeGhClient.repoStateWithOpen r 42)
                FindPrsForIssue = fun _ _ -> async { return [ closedPr ] }
                ReopenPr        = fun _ pr -> reopenCalls.Add(pr); async { return Ok () } }
    let deps = Given.deps fs client

    let guid         = System.Guid.NewGuid().ToString("N")
    let checkoutRoot = Path.Combine(Path.GetTempPath(), $"orcai-reopen-{guid}")
    let seedDir      = Path.Combine(Path.GetTempPath(), $"orcai-reopen-seed-{guid}")
    let baseDir      = OrcAI.Core.CheckoutManager.basePath checkoutRoot repo
    let run (exe: string) (args: string list) (wd: string) =
        let psi = ProcessStartInfo(exe)
        psi.WorkingDirectory       <- wd
        psi.RedirectStandardOutput <- true
        psi.RedirectStandardError  <- true
        psi.UseShellExecute        <- false
        for a in args do psi.ArgumentList.Add(a)
        use p = Process.Start(psi)
        p.WaitForExit()
        p.ExitCode = 0
    try
        Directory.CreateDirectory(seedDir) |> ignore
        Assert.True(run "git" ["-c"; "init.defaultBranch=main"; "init"; seedDir] (Path.GetTempPath()), "git init seed")
        File.WriteAllText(Path.Combine(seedDir, "README.md"), "init")
        Assert.True(run "git" ["add"; "README.md"] seedDir, "git add in seed")
        Assert.True(run "git" ["-c"; "user.email=t@t.com"; "-c"; "user.name=t"; "commit"; "-m"; "init"] seedDir, "git commit in seed")
        Directory.CreateDirectory(Path.GetDirectoryName(baseDir)) |> ignore
        Assert.True(run "git" ["clone"; "--bare"; seedDir; baseDir] (Path.GetTempPath()), "git clone bare as base")

        let input   = { A.RunInput.defaults () with SkipLock = true; CheckoutRoot = Some checkoutRoot }
        let results = execute deps [path] input |> Async.RunSynchronously
        match results.TryFind path with
        | Some (Ok runResult) ->
            Assert.Equal(1, reopenCalls.Count)
            Assert.Equal(PrNumber 3, reopenCalls |> Seq.head)
            Assert.Contains(runResult.Lock.PullRequests, fun pr -> pr.Repo = repo && pr.Number = PrNumber 3 && pr.State = "OPEN")
        | Some (Error e) -> Assert.Fail($"Expected Ok RunResult, got Error: {e}")
        | None           -> Assert.Fail("Expected an entry for the yaml path")
    finally
        try Directory.Delete(seedDir, true) with _ -> ()
        try Directory.Delete(checkoutRoot, true) with _ -> ()
