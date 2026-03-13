module OrcAI.Core.Tests.RunCommandTests

open Xunit
open Testably.Abstractions.Testing
open OrcAI.Core.Domain
open OrcAI.Core.GhClient
open OrcAI.Core.Deps
open OrcAI.Core.RunCommand

// ---------------------------------------------------------------------------
// Unit tests for the pure labelsToCreate helper.
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
// Helpers for multi-file execute tests.
// ---------------------------------------------------------------------------

let private validYaml templatePath =
    "job:\n" +
    "  title: \"Add AGENTS.md\"\n" +
    "  org: \"myorg\"\n" +
    "repos:\n" +
    "  - \"repo-a\"\n" +
    "issue:\n" +
    "  template: \"" + templatePath + "\"\n" +
    "  labels: []\n"

/// Write a valid YAML file plus its template to a MockFileSystem and return the yaml path.
let private writeMockYaml (fs: MockFileSystem) (name: string) : string =
    let dir          = "/work"
    fs.Directory.CreateDirectory(dir) |> ignore
    let templatePath = $"{dir}/template.md"
    if not (fs.File.Exists(templatePath)) then
        fs.File.WriteAllText(templatePath, "# body")
    let yamlPath = $"{dir}/{name}"
    fs.File.WriteAllText(yamlPath, validYaml "./template.md")
    yamlPath

/// A fake IGhClient that returns a successful project, issue creation, etc.
type FakeGhClient(repoErrors: Map<string, string>) =
    let notImpl name = async { return failwith $"FakeGhClient.{name} not expected" }
    let fakeProject =
        { Title    = "My Project"
          Org      = OrgName "myorg"
          Number   = 1
          Url      = "https://github.com/orgs/myorg/projects/1" }
    let fakeIssue repo num =
        { Repo      = RepoName repo
          Number    = IssueNumber num
          Url       = $"https://github.com/{repo}/issues/{num}"
          Assignees = [] }
    interface IGhClient with
        member _.FindProject _ _       = async { return Some fakeProject }
        member _.CreateProject _ _     = async { return Ok fakeProject }
        member _.DeleteProject _       = notImpl "DeleteProject"
        member _.ListLabels _          = async { return Ok [] }
        member _.CreateLabel _ _       = async { return Ok () }
        member _.FindIssue repo _      =
            let (RepoName r) = repo
            match Map.tryFind r repoErrors with
            | Some _ -> async { return None }
            | None   -> async { return None }   // always create fresh
        member _.CreateIssue repo _ _ _ =
            let (RepoName r) = repo
            match Map.tryFind r repoErrors with
            | Some e -> async { return Error e }
            | None   -> async { return Ok (fakeIssue r 42) }
        member _.CloseIssue _ _        = notImpl "CloseIssue"
        member _.AddIssueToProject _ _ = async { return Ok () }
        member _.AssignIssue _ _ _     = async { return Ok () }
        member _.FindPrsForIssue _ _   = notImpl "FindPrsForIssue"
        member _.ClosePr _ _           = notImpl "ClosePr"
        member _.ListRepos _           = notImpl "ListRepos"
        member _.RepoExists _          = async { return Ok () }

let private makeDeps (fs: MockFileSystem) (client: IGhClient) : OrcAIDeps =
    { GhClient    = client
      AuthContext = { new OrcAI.Core.AuthContext.IAuthContext with
                         member _.GetToken() = async { return Ok "fake-token" } }
      FileSystem  = fs :> System.IO.Abstractions.IFileSystem }

let private defaultInput paths =
    { YamlPath         = ""
      Verbose          = false
      AutoCreateLabels = false
      SkipCopilot      = true    // skip copilot to avoid AssignIssue calls
      SkipLock         = true
      MaxConcurrency   = 4
      NoParallel       = false
      ContinueOnError  = false }

// ---------------------------------------------------------------------------
// Multi-file execute tests
// ---------------------------------------------------------------------------

[<Fact>]
let ``execute processes all files in a resolved path list`` () =
    let fs     = MockFileSystem()
    let path1  = writeMockYaml fs "job1.yml"
    let path2  = writeMockYaml fs "job2.yml"
    let deps   = makeDeps fs (FakeGhClient(Map.empty))
    let input  = defaultInput [path1; path2]

    let results =
        execute deps [path1; path2] input
        |> Async.RunSynchronously

    Assert.Equal(2, results.Count)
    Assert.True(results |> Map.forall (fun _ r -> match r with Ok _ -> true | Error _ -> false))

[<Fact>]
let ``execute result is always a filename-keyed dictionary`` () =
    let fs    = MockFileSystem()
    let path  = writeMockYaml fs "job.yml"
    let deps  = makeDeps fs (FakeGhClient(Map.empty))
    let input = defaultInput [path]

    let results =
        execute deps [path] input
        |> Async.RunSynchronously

    Assert.True(results.ContainsKey(path))

[<Fact>]
let ``execute stops on first failure without ContinueOnError (sequential)`` () =
    let fs    = MockFileSystem()
    let path1 = writeMockYaml fs "job1.yml"
    let path2 = writeMockYaml fs "job2.yml"
    // Make path1 fail by writing invalid yaml
    (fs :> System.IO.Abstractions.IFileSystem).File.WriteAllText(path1, "not: valid: yaml: at: all\n!!!")
    let deps  = makeDeps fs (FakeGhClient(Map.empty))
    let input = { defaultInput [path1; path2] with NoParallel = true; ContinueOnError = false }

    let results =
        execute deps [path1; path2] input
        |> Async.RunSynchronously

    // path1 should be an error; path2 should not be present (stopped early)
    Assert.True(results.ContainsKey(path1))
    match results.[path1] with
    | Error _ -> ()
    | Ok _    -> Assert.Fail("Expected path1 to fail")
    Assert.False(results.ContainsKey(path2), "path2 should not have been processed")

[<Fact>]
let ``execute continues past failures with ContinueOnError`` () =
    let fs    = MockFileSystem()
    let path1 = writeMockYaml fs "job1.yml"
    let path2 = writeMockYaml fs "job2.yml"
    // Make path1 fail
    (fs :> System.IO.Abstractions.IFileSystem).File.WriteAllText(path1, "not: valid: yaml: at: all\n!!!")
    let deps  = makeDeps fs (FakeGhClient(Map.empty))
    let input = { defaultInput [path1; path2] with NoParallel = true; ContinueOnError = true }

    let results =
        execute deps [path1; path2] input
        |> Async.RunSynchronously

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
    let path1 = writeMockYaml fs "job1.yml"
    let path2 = writeMockYaml fs "job2.yml"
    let deps  = makeDeps fs (FakeGhClient(Map.empty))
    let input = { defaultInput [path1; path2] with NoParallel = true; ContinueOnError = true }

    let results =
        execute deps [path1; path2] input
        |> Async.RunSynchronously

    Assert.Equal(2, results.Count)
    Assert.True(results |> Map.forall (fun _ r -> match r with Ok _ -> true | Error _ -> false))
