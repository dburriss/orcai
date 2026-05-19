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
    let input       = A.RunInput.defaults () |> A.RunInput.withSkipCopilot false |> A.RunInput.withIsPrimaryAuthApp true

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
    let input       = A.RunInput.defaults () |> A.RunInput.withSkipCopilot false

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
    let input       = A.RunInput.defaults () |> A.RunInput.withSkipCopilot false |> A.RunInput.withIsPrimaryAuthApp true

    let results = execute deps [path] input |> Async.RunSynchronously

    Assert.True(results |> Map.forall (fun _ r -> match r with Ok _ -> true | Error _ -> false))
    Assert.Empty(assignCalls)

[<Fact>]
let ``processRepo skips assignment entirely when skipCopilot=true regardless of CopilotClient`` () =
    let fs          = MockFileSystem()
    let path        = Given.namedYamlFile fs "job.yml"
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
            FindClosedIssue = fun repo _ -> async { return Ok (Some (FakeGhClient.issueFor repo 7)) }
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
                FindClosedIssue   = fun repo _ -> async { return Ok (Some (FakeGhClient.issueFor repo 7)) }
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
                FindClosedIssue   = fun repo _ -> async { return Ok (Some (FakeGhClient.issueFor repo 7)) }
                CreateIssue       = fun _ _ _ _ -> async { return failwith "CreateIssue not expected" }
                AddIssueToProject = FakeGhClient.trackingAddIssue pc
                AssignIssue       = FakeGhClient.trackingAssignUnit ac }
    let deps  = Given.deps fs client
    let input =
        A.RunInput.defaults ()
        |> A.RunInput.withOnClosedIssue (Some Skip)
        |> A.RunInput.withSkipCopilot false

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
                FindClosedIssue   = fun repo _ -> async { return Ok (Some (FakeGhClient.issueFor repo 7)) }
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
                FindIssue       = fun _ _ -> async { return Error "API rate limit exceeded" }
                FindClosedIssue = fun _ _ -> async { return failwith "FindClosedIssue should not be called when FindIssue errors" }
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
                FindIssue       = fun _ _ -> async { return Ok None }
                FindClosedIssue = fun _ _ -> async { return Error "secondary rate limit" }
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
                IsArchived  = fun _ -> async { return Ok true }
                FindIssue   = fun _ _ -> async { return failwith "FindIssue not expected for archived repo" }
                CreateIssue = fun _ _ _ _ -> async { return failwith "CreateIssue not expected for archived repo" }
                UpdateIssue = fun _ _ _ _ -> async { return failwith "UpdateIssue not expected for archived repo" } }
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
                IsArchived = fun _ -> async { return Ok true } }
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
                IsArchived  = fun _ -> async { return Error "transient network error" }
                CreateIssue = fun repo _ _ _ ->
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
/// is stale, so executeSingle takes the `updateBodies` branch.
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

[<Fact>]
let ``updateBodies recreates issue when UpdateIssue returns stale error`` () =
    let fs   = MockFileSystem()
    let path = Given.namedYamlFile fs "job.yml"
    let repo = RepoName "myorg/repo-a"
    givenStaleTemplateLock fs path repo 42

    let createCalls = ConcurrentBag<unit>()
    let client =
        FakeGhClient.from
            { FakeGhClient.defaults with
                UpdateIssue = fun _ _ _ _ ->
                    async { return Error "GraphQL: Could not resolve to an issue or pull request with the number of 42. (updateIssue)" }
                FindIssue   = fun _ _ -> async { return Ok None }
                FindClosedIssue = fun _ _ -> async { return Ok None }
                CreateIssue = fun repo _ _ _ ->
                    createCalls.Add(())
                    async { return Ok (FakeGhClient.issueFor repo 99) } }
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

[<Fact>]
let ``updateBodies leaves non-stale issues unaffected when one repo is stale`` () =
    let fs   = MockFileSystem()
    let path = Given.namedYamlFile fs "job.yml"
    let repoA = RepoName "myorg/repo-a"
    let repoB = RepoName "myorg/repo-b"
    let yamlHash = OrcAI.Core.YamlConfig.computeHash (fs :> System.IO.Abstractions.IFileSystem) path
    let lock =
        { A.LockFile.defaults () with
            YamlHash     = yamlHash
            TemplateHash = "stale-template-hash"
            Repos        = [ repoA; repoB ]
            Issues       =
                [ { Repo = repoA; Number = IssueNumber 7
                    Url = "https://github.com/myorg/repo-a/issues/7"; Assignees = [] }
                  { Repo = repoB; Number = IssueNumber 8
                    Url = "https://github.com/myorg/repo-b/issues/8"; Assignees = [] } ]
            PullRequests = [] }
    OrcAI.Core.LockFile.write (fs :> System.IO.Abstractions.IFileSystem) path lock

    let client =
        FakeGhClient.from
            { FakeGhClient.defaults with
                UpdateIssue = fun repo (IssueNumber n) _ _ ->
                    if n = 7 then
                        async { return Error "GraphQL: Could not resolve to an issue or pull request with the number of 7." }
                    else
                        async { return Ok () }
                FindIssue   = fun _ _ -> async { return Ok None }
                FindClosedIssue = fun _ _ -> async { return Ok None }
                CreateIssue = fun repo _ _ _ ->
                    async { return Ok (FakeGhClient.issueFor repo 77) } }
    let deps  = Given.deps fs client
    let input = { A.RunInput.defaults () with SkipLock = false }

    let results = execute deps [path] input |> Async.RunSynchronously

    match results.[path] with
    | Error e -> Assert.Fail($"Expected Ok but got: {e}")
    | Ok result ->
        let byRepo = result.Lock.Issues |> List.map (fun i -> i.Repo, i) |> Map.ofList
        // Stale repo got new issue number; non-stale repo kept original number.
        Assert.Equal(IssueNumber 77, byRepo.[repoA].Number)
        Assert.Equal(IssueNumber 8,  byRepo.[repoB].Number)

[<Fact>]
let ``stale-issue recreate failure surfaces as UpdateFailed`` () =
    let fs   = MockFileSystem()
    let path = Given.namedYamlFile fs "job.yml"
    let repo = RepoName "myorg/repo-a"
    givenStaleTemplateLock fs path repo 42

    let client =
        FakeGhClient.from
            { FakeGhClient.defaults with
                UpdateIssue = fun _ _ _ _ ->
                    async { return Error "GraphQL: Could not resolve to an issue or pull request with the number of 42." }
                FindIssue   = fun _ _ -> async { return Ok None }
                FindClosedIssue = fun _ _ -> async { return Ok None }
                CreateIssue = fun _ _ _ _ ->
                    async { return Error "boom: create failed" } }
    let deps  = Given.deps fs client
    let input = { A.RunInput.defaults () with SkipLock = false }

    let results = execute deps [path] input |> Async.RunSynchronously

    match results.[path] with
    | Error e -> Assert.Fail($"Expected Ok but got: {e}")
    | Ok result ->
        Assert.Contains(result.Results, fun r ->
            match r.Outcome with UpdateFailed _ -> true | _ -> false)

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
