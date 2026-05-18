module OrcAI.Core.Tests.NudgeCommandTests

open System.Collections.Concurrent
open Xunit
open Testably.Abstractions.Testing
open OrcAI.Core.Domain
open OrcAI.Core.NudgeCommand
open OrcAI.Core.Tests.TestData

let private nudgeYaml =
    "job:\n" +
    "  title: \"Add AGENTS.md\"\n" +
    "  org: \"myorg\"\n" +
    "repos:\n" +
    "  - \"repo-a\"\n" +
    "issue:\n" +
    "  template: \"TEMPLATE_PLACEHOLDER\"\n"

let private defaultInput yamlPath : NudgeInput =
    { YamlPath         = yamlPath
      DryRun           = false
      Verbose          = false
      SaveLock         = false
      IsPrimaryAuthApp = false }

let private writeLock (fs: MockFileSystem) (yamlPath: string) (lock: LockFile) =
    OrcAI.Core.LockFile.write (fs :> System.IO.Abstractions.IFileSystem) yamlPath lock

let private lockWithUnpairedIssue () =
    let repo = RepoName "myorg/repo-a"
    A.LockFile.defaults ()
    |> A.LockFile.withRepos [ repo ]
    |> A.LockFile.withIssues [ A.IssueRef.defaults repo 7 ]
    |> fun lf -> { lf with PullRequests = [] }

[<Fact>]
let ``isAppTokenAssignError matches GitHub's App-token GraphQL error`` () =
    let stderr =
        "failed to update https://github.com/o/r/issues/1: GraphQL: " +
        "Assigning agents is not supported with GitHub App installation tokens. " +
        "Use a user token (personal access token or OAuth token) instead. (replaceActorsForAssignable)"
    Assert.True(isAppTokenAssignError stderr)

[<Fact>]
let ``isAppTokenAssignError ignores unrelated errors`` () =
    Assert.False(isAppTokenAssignError "API rate limit exceeded")
    Assert.False(isAppTokenAssignError "")

[<Fact>]
let ``pre-flight refuses when App auth + no PAT + reassign copilot`` () =
    // The destructive unassign loop must never run under these conditions.
    let fs   = MockFileSystem()
    let yaml = Given.yamlFile fs nudgeYaml "# body"
    writeLock fs yaml (lockWithUnpairedIssue ())

    let deps  = Given.deps fs (FakeGhClient.from FakeGhClient.neverCalledHandlers)
    let input = { defaultInput yaml with IsPrimaryAuthApp = true }

    let result = execute deps input

    match result with
    | Error e ->
        Assert.Contains("Cannot reassign @copilot", e)
        Assert.Contains("ORCAI_PAT", e)
    | Ok _ -> failwith "expected pre-flight Error"

[<Fact>]
let ``pre-flight does not trip when PAT is configured`` () =
    let fs   = MockFileSystem()
    let yaml = Given.yamlFile fs nudgeYaml "# body"
    writeLock fs yaml (lockWithUnpairedIssue ())

    let copilotClient = FakeGhClient.from FakeGhClient.defaults
    let primary =
        { FakeGhClient.defaults with
            FindPrsForIssue = fun _ _ -> async { return [] } }
        |> FakeGhClient.from
    let deps =
        { Given.deps fs primary with
            CopilotClient = Some copilotClient }
    let input = { defaultInput yaml with IsPrimaryAuthApp = true }

    let result = execute deps input

    match result with
    | Ok _ -> ()
    | Error e -> failwith $"unexpected pre-flight error: {e}"

[<Fact>]
let ``pre-flight does not trip in dry-run`` () =
    let fs   = MockFileSystem()
    let yaml = Given.yamlFile fs nudgeYaml "# body"
    writeLock fs yaml (lockWithUnpairedIssue ())

    // Dry-run skips writes entirely; we still want the report to render.
    let handlers =
        { FakeGhClient.defaults with
            FindPrsForIssue = fun _ _ -> async { return [] } }
    let deps  = Given.deps fs (FakeGhClient.from handlers)
    let input = { defaultInput yaml with IsPrimaryAuthApp = true; DryRun = true }

    let result = execute deps input

    match result with
    | Ok results ->
        Assert.Equal(1, results.Length)
        Assert.Equal(DryRunWouldNudge, results.[0].Outcome)
    | Error e -> failwith $"dry-run should not short-circuit, got: {e}"

[<Fact>]
let ``failed AssignIssue surfaces as NudgeFailed (not NudgeSent)`` () =
    let fs   = MockFileSystem()
    let yaml = Given.yamlFile fs nudgeYaml "# body"
    writeLock fs yaml (lockWithUnpairedIssue ())

    let appTokenError =
        "failed to update https://github.com/myorg/repo-a/issues/7: GraphQL: " +
        "Assigning agents is not supported with GitHub App installation tokens. " +
        "Use a user token (personal access token or OAuth token) instead."
    let assignCalls = ConcurrentBag<unit>()
    let unassignCalls = ConcurrentBag<unit>()
    let handlers =
        { FakeGhClient.defaults with
            FindPrsForIssue = fun _ _    -> async { return [] }
            UnassignIssue   = fun _ _ _  -> unassignCalls.Add(()); async { return Ok () }
            AssignIssue     = fun _ _ _  -> assignCalls.Add(()); async { return Error appTokenError } }
    let deps  = Given.deps fs (FakeGhClient.from handlers)
    let input = defaultInput yaml

    let result = execute deps input

    match result with
    | Ok results ->
        Assert.Equal(1, results.Length)
        match results.[0].Outcome with
        | NudgeFailed reason ->
            Assert.True(isAppTokenAssignError reason, $"expected App-token error to be classified, got: {reason}")
        | other -> failwith $"expected NudgeFailed, got {other}"
        Assert.Equal(1, unassignCalls.Count)
        Assert.Equal(1, assignCalls.Count)
    | Error e -> failwith $"expected Ok results, got Error: {e}"

[<Fact>]
let ``successful assign produces NudgeSent`` () =
    let fs   = MockFileSystem()
    let yaml = Given.yamlFile fs nudgeYaml "# body"
    writeLock fs yaml (lockWithUnpairedIssue ())

    let handlers =
        { FakeGhClient.defaults with
            FindPrsForIssue = fun _ _    -> async { return [] }
            UnassignIssue   = fun _ _ _  -> async { return Ok () }
            AssignIssue     = fun _ _ _  -> async { return Ok () } }
    let deps  = Given.deps fs (FakeGhClient.from handlers)
    let input = defaultInput yaml

    let result = execute deps input

    match result with
    | Ok results ->
        Assert.Equal(1, results.Length)
        Assert.Equal(NudgeSent, results.[0].Outcome)
    | Error e -> failwith $"expected Ok, got Error: {e}"

[<Fact>]
let ``failed UnassignIssue surfaces as NudgeFailed and skips assign`` () =
    let fs   = MockFileSystem()
    let yaml = Given.yamlFile fs nudgeYaml "# body"
    writeLock fs yaml (lockWithUnpairedIssue ())

    let assignCalls = ConcurrentBag<unit>()
    let handlers =
        { FakeGhClient.defaults with
            FindPrsForIssue = fun _ _    -> async { return [] }
            UnassignIssue   = fun _ _ _  -> async { return Error "boom" }
            AssignIssue     = fun _ _ _  -> assignCalls.Add(()); async { return Ok () } }
    let deps  = Given.deps fs (FakeGhClient.from handlers)
    let input = defaultInput yaml

    let result = execute deps input

    match result with
    | Ok results ->
        Assert.Equal(1, results.Length)
        match results.[0].Outcome with
        | NudgeFailed "boom" -> ()
        | other -> failwith $"expected NudgeFailed boom, got {other}"
        Assert.Equal(0, assignCalls.Count)
    | Error e -> failwith $"expected Ok, got Error: {e}"
