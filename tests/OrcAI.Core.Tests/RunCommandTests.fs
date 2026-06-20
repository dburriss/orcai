module OrcAI.Core.Tests.RunCommandTests

open System.Collections.Concurrent
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
// Copilot assignment / mixed-auth tests
// ---------------------------------------------------------------------------

[<Fact>]
let ``processRepo uses CopilotClient when Some for AssignIssue`` () =
    let fs          = MockFileSystem()
    let path        = Given.namedYamlFile fs "job.yml"
    let assignCalls = ConcurrentBag<string>()
    let primary     = FakeGhClient.from { FakeGhClient.defaults with AssignIssue = FakeGhClient.trackingAssign "primary" assignCalls }
    let copilot     = FakeGhClient.from { FakeGhClient.defaults with AssignIssue = FakeGhClient.trackingAssign "copilot"  assignCalls }
    let deps        = { Given.deps fs primary with CopilotClient = Some copilot }
    let input       = A.RunInput.defaults () |> A.RunInput.withIsPrimaryAuthApp true

    let results = execute deps [path] input |> Async.RunSynchronously

    Assert.True(results |> Map.forall (fun _ r -> match r with Ok _ -> true | Error _ -> false))
    Assert.Contains("copilot", assignCalls)
    Assert.DoesNotContain("primary", assignCalls)

[<Fact>]
let ``processRepo uses primary client for AssignIssue when CopilotClient is None and IsPrimaryAuthApp=false`` () =
    let fs          = MockFileSystem()
    let path        = Given.namedYamlFile fs "job.yml"
    let assignCalls = ConcurrentBag<string>()
    let primary     = FakeGhClient.from { FakeGhClient.defaults with AssignIssue = FakeGhClient.trackingAssign "primary" assignCalls }
    let deps        = Given.deps fs primary
    let input       = A.RunInput.defaults ()

    let results = execute deps [path] input |> Async.RunSynchronously

    Assert.True(results |> Map.forall (fun _ r -> match r with Ok _ -> true | Error _ -> false))
    Assert.Contains("primary", assignCalls)

[<Fact>]
let ``processRepo skips assignment and does not call AssignIssue when CopilotClient=None and IsPrimaryAuthApp=true`` () =
    let fs          = MockFileSystem()
    let path        = Given.namedYamlFile fs "job.yml"
    let assignCalls = ConcurrentBag<string>()
    let primary     = FakeGhClient.from { FakeGhClient.defaults with AssignIssue = FakeGhClient.trackingAssign "primary" assignCalls }
    let deps        = Given.deps fs primary
    let input       = A.RunInput.defaults () |> A.RunInput.withIsPrimaryAuthApp true

    let results = execute deps [path] input |> Async.RunSynchronously

    Assert.True(results |> Map.forall (fun _ r -> match r with Ok _ -> true | Error _ -> false))
    Assert.Empty(assignCalls)

[<Fact>]
let ``processRepo skips assignment entirely when action is noop regardless of CopilotClient`` () =
    let fs          = MockFileSystem()
    let path        = Given.namedNoopYamlFile fs "job.yml"
    let assignCalls = ConcurrentBag<string>()
    let primary     = FakeGhClient.from { FakeGhClient.defaults with AssignIssue = FakeGhClient.trackingAssign "primary" assignCalls }
    let copilot     = FakeGhClient.from { FakeGhClient.defaults with AssignIssue = FakeGhClient.trackingAssign "copilot"  assignCalls }
    let deps        = { Given.deps fs primary with CopilotClient = Some copilot }
    let input       = A.RunInput.defaults () |> A.RunInput.withIsPrimaryAuthApp true

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
          Number = IssueNumber issueNum
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
          Number = IssueNumber issueNum
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
        Assert.Equal(IssueNumber 99, issue.Number)

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
        Assert.Equal(0, result.Lock.Project.Number)

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
    let project = { Org = OrgName "myorg"; Number = 1; Title = "Upstream"; Url = "" }
    let lock : LockFile =
        { LockedAt     = System.DateTimeOffset.MinValue
          YamlHash     = "h"
          TemplateHash = "h"
          Project      = project
          Repos        = repos
          Issues       = issues |> List.map (fun (repo, num) ->
                             let (RepoName r) = repo
                             { Repo = repo; Number = IssueNumber num
                               Url  = $"https://github.com/{r}/issues/{num}"
                               Assignees = [] })
          PullRequests  = prs |> List.map (fun (repo, prNum, issueNum, state) ->
                              let (RepoName r) = repo
                              { Repo        = repo
                                Number      = PrNumber prNum
                                Url         = $"https://github.com/{r}/pull/{prNum}"
                                ClosesIssue = IssueNumber issueNum
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
    let fs = MockFileSystem()
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
