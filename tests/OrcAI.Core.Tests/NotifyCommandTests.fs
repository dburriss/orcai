module OrcAI.Core.Tests.NotifyCommandTests

open System.Collections.Concurrent
open Xunit
open Testably.Abstractions.Testing
open OrcAI.Core.Domain
open OrcAI.Core.NotifyCommand
open OrcAI.Core.Tests.TestData

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

let private notifyYaml =
    "job:\n" +
    "  title: \"Add AGENTS.md\"\n" +
    "  org: \"myorg\"\n" +
    "repos:\n" +
    "  - \"repo-a\"\n" +
    "issue:\n" +
    "  template: \"TEMPLATE_PLACEHOLDER\"\n" +
    "notify:\n" +
    "  comment: \"Hey {assignee}, please take a look.\"\n"

let private defaultInput yamlPath : NotifyInput =
    { YamlPath = yamlPath
      DryRun   = false
      Verbose  = false
      Target   = "issues"
      State    = "open" }

let private writeLock (fs: MockFileSystem) (yamlPath: string) (lock: LockFile) =
    OrcAI.Core.LockFile.write (fs :> System.IO.Abstractions.IFileSystem) yamlPath lock

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

[<Fact>]
let ``notify returns error when lock file is missing`` () =
    let fs      = MockFileSystem()
    let yaml    = Given.yamlFile fs notifyYaml "# body"
    let deps    = Given.deps fs (FakeGhClient.from FakeGhClient.defaults)
    let input   = defaultInput yaml

    let result = execute deps input

    Assert.True(match result with Error _ -> true | _ -> false)

[<Fact>]
let ``notify posts comment to open issue`` () =
    let fs      = MockFileSystem()
    let yaml    = Given.yamlFile fs notifyYaml "# body"
    let repo    = RepoName "myorg/repo-a"
    let lock    = A.LockFile.defaults () |> A.LockFile.withIssues [ A.IssueRef.defaults repo 7 ]
    writeLock fs yaml lock

    let comments = ConcurrentBag<IssueNumber>()
    let handlers =
        { FakeGhClient.defaults with
            GetIssueState = fun _ _ -> async { return Some "OPEN" }
            PostComment   = fun _ iss _ -> comments.Add(iss); async { return Ok () } }
    let deps  = Given.deps fs (FakeGhClient.from handlers)
    let input = defaultInput yaml

    let result = execute deps input

    Assert.True(match result with Ok _ -> true | _ -> false)
    let results = match result with Ok r -> r | _ -> []
    Assert.Equal(1, results.Length)
    Assert.Equal(Notified, results.[0].Outcome)
    Assert.Equal(1, comments.Count)

[<Fact>]
let ``notify skips closed issue when state is open`` () =
    let fs      = MockFileSystem()
    let yaml    = Given.yamlFile fs notifyYaml "# body"
    let repo    = RepoName "myorg/repo-a"
    let lock    = A.LockFile.defaults () |> A.LockFile.withIssues [ A.IssueRef.defaults repo 7 ]
    writeLock fs yaml lock

    let handlers =
        { FakeGhClient.defaults with
            GetIssueState = fun _ _ -> async { return Some "CLOSED" } }
    let deps  = Given.deps fs (FakeGhClient.from handlers)
    let input = defaultInput yaml

    let result = execute deps input

    let results = match result with Ok r -> r | _ -> []
    Assert.Equal(1, results.Length)
    Assert.Equal(Skipped, results.[0].Outcome)

[<Fact>]
let ``notify dry run does not post any comments`` () =
    let fs      = MockFileSystem()
    let yaml    = Given.yamlFile fs notifyYaml "# body"
    let repo    = RepoName "myorg/repo-a"
    let lock    = A.LockFile.defaults () |> A.LockFile.withIssues [ A.IssueRef.defaults repo 7 ]
    writeLock fs yaml lock

    let comments = ConcurrentBag<unit>()
    let handlers =
        { FakeGhClient.defaults with
            GetIssueState = fun _ _ -> async { return Some "OPEN" }
            PostComment   = fun _ _ _ -> comments.Add(()); async { return Ok () } }
    let deps  = Given.deps fs (FakeGhClient.from handlers)
    let input = { defaultInput yaml with DryRun = true }

    let result = execute deps input

    let results = match result with Ok r -> r | _ -> []
    Assert.Equal(DryRunWouldNotify, results.[0].Outcome)
    Assert.Empty(comments)

[<Fact>]
let ``notify state all does not call GetIssueState`` () =
    let fs      = MockFileSystem()
    let yaml    = Given.yamlFile fs notifyYaml "# body"
    let repo    = RepoName "myorg/repo-a"
    let lock    = A.LockFile.defaults () |> A.LockFile.withIssues [ A.IssueRef.defaults repo 7 ]
    writeLock fs yaml lock

    let stateCalls = ConcurrentBag<unit>()
    let handlers =
        { FakeGhClient.defaults with
            GetIssueState = fun _ _ -> stateCalls.Add(()); async { return Some "OPEN" }
            PostComment   = fun _ _ _ -> async { return Ok () } }
    let deps  = Given.deps fs (FakeGhClient.from handlers)
    let input = { defaultInput yaml with State = "all" }

    let _ = execute deps input

    Assert.Empty(stateCalls)

[<Fact>]
let ``notify targets prs when target is prs`` () =
    let fs      = MockFileSystem()
    let yaml    = Given.yamlFile fs notifyYaml "# body"
    let repo    = RepoName "myorg/repo-a"
    let pr      = A.PullRequestRef.defaults repo 3 7
    let lock    = { A.LockFile.defaults () with PullRequests = [ pr ] }
                  |> A.LockFile.withIssues []
    writeLock fs yaml lock

    let comments = ConcurrentBag<int>()
    let handlers =
        { FakeGhClient.defaults with
            GetPrState  = fun _ _ -> async { return Some "OPEN" }
            PostComment = fun _ (IssueNumber n) _ -> comments.Add(n); async { return Ok () } }
    let deps  = Given.deps fs (FakeGhClient.from handlers)
    let input = { defaultInput yaml with Target = "prs" }

    let result = execute deps input

    let results = match result with Ok r -> r | _ -> []
    Assert.Equal(1, results.Length)
    Assert.Equal(Notified, results.[0].Outcome)
    Assert.Equal("pr", results.[0].Kind)
    Assert.Equal(1, comments.Count)

[<Fact>]
let ``notify targets both issues and prs when target is both`` () =
    let fs      = MockFileSystem()
    let yaml    = Given.yamlFile fs notifyYaml "# body"
    let repo    = RepoName "myorg/repo-a"
    let issue   = A.IssueRef.defaults repo 7
    let pr      = A.PullRequestRef.defaults repo 3 7
    let lock    = { A.LockFile.defaults () with PullRequests = [ pr ] }
                  |> A.LockFile.withIssues [ issue ]
    writeLock fs yaml lock

    let handlers =
        { FakeGhClient.defaults with
            GetIssueState = fun _ _ -> async { return Some "OPEN" }
            GetPrState    = fun _ _ -> async { return Some "OPEN" }
            PostComment   = fun _ _ _ -> async { return Ok () } }
    let deps  = Given.deps fs (FakeGhClient.from handlers)
    let input = { defaultInput yaml with Target = "both"; State = "open" }

    let result = execute deps input

    let results = match result with Ok r -> r | _ -> []
    Assert.Equal(2, results.Length)
    Assert.True(results |> List.forall (fun r -> r.Outcome = Notified))

[<Fact>]
let ``notify notified outcome when state is closed and item is closed`` () =
    let fs      = MockFileSystem()
    let yaml    = Given.yamlFile fs notifyYaml "# body"
    let repo    = RepoName "myorg/repo-a"
    let lock    = A.LockFile.defaults () |> A.LockFile.withIssues [ A.IssueRef.defaults repo 7 ]
    writeLock fs yaml lock

    let handlers =
        { FakeGhClient.defaults with
            GetIssueState = fun _ _ -> async { return Some "CLOSED" }
            PostComment   = fun _ _ _ -> async { return Ok () } }
    let deps  = Given.deps fs (FakeGhClient.from handlers)
    let input = { defaultInput yaml with State = "closed" }

    let result = execute deps input

    let results = match result with Ok r -> r | _ -> []
    Assert.Equal(Notified, results.[0].Outcome)
