module OrcAI.Core.Tests.CleanupCommandTests

open Xunit
open Testably.Abstractions.Testing
open OrcAI.Core.Domain
open OrcAI.Core.CleanupCommand
open OrcAI.Core.Tests.TestData

let private cleanupYaml =
    "job:\n" +
    "  title: \"Add AGENTS.md\"\n" +
    "  org: \"myorg\"\n" +
    "repos:\n" +
    "  - \"repo-a\"\n" +
    "issue:\n" +
    "  template: \"TEMPLATE_PLACEHOLDER\"\n"

let private writeLock (fs: MockFileSystem) (yamlPath: string) (lock: LockFile) =
    OrcAI.Core.LockFile.write (fs :> System.IO.Abstractions.IFileSystem) yamlPath lock

let private lockWithIssue () =
    let repo = RepoName "myorg/repo-a"
    A.LockFile.defaults ()
    |> A.LockFile.withRepos [ repo ]
    |> A.LockFile.withIssues [ A.IssueRef.defaults repo 7 ]
    |> fun lf -> { lf with PullRequests = [] }

[<Fact>]
let ``Prs=None skips PR lookup/close and deletes the issue directly`` () =
    let fs   = MockFileSystem()
    let yaml = Given.yamlFile fs cleanupYaml "# body"
    writeLock fs yaml (lockWithIssue ())

    let client =
        FakeGhClient.from
            { FakeGhClient.defaults with
                FindPrsForIssue = fun _ _ -> async { return failwith "FindPrsForIssue must not be called when Prs=None" }
                ClosePr         = fun _ _ -> async { return failwith "ClosePr must not be called when Prs=None" }
                DeleteIssue     = fun _ _ -> async { return Ok () }
                DeleteProject   = fun _   -> async { return Ok () } }
    let deps =
        Given.deps fs client
        |> Given.mapProviderClients (fun pc -> { pc with Prs = None })
    let input : CleanupInput = { YamlPath = yaml; DryRun = false }

    let result = execute deps input

    match result with
    | Error e -> Assert.Fail($"Expected Ok but got Error: {e}")
    | Ok cleanupResult ->
        Assert.DoesNotContain(cleanupResult.Resources, function CleanedPr _ -> true | _ -> false)
        Assert.Contains(cleanupResult.Resources, function CleanedIssue("myorg/repo-a", "7") -> true | _ -> false)

[<Fact>]
let ``execute never calls AuthContext.GetToken for a provider: local job`` () =
    let fs   = MockFileSystem()
    let yaml = Given.yamlFile fs (cleanupYaml + "provider:\n  type: local\n") "# body"
    writeLock fs yaml (lockWithIssue ())

    let client =
        FakeGhClient.from
            { FakeGhClient.defaults with
                DeleteIssue   = fun _ _ -> async { return Ok () }
                DeleteProject = fun _   -> async { return Ok () } }
    // A Local provider has no IPullRequestLinker (Prs = None) — same as production.
    let deps =
        Given.deps fs client
        |> Given.mapProviderClients (fun pc -> { pc with Prs = None })
        |> Given.withNeverCalledAuth
    let input : CleanupInput = { YamlPath = yaml; DryRun = false }

    match execute deps input with
    | Error e -> Assert.Fail($"Expected Ok but got Error: {e}")
    | Ok _ -> ()
