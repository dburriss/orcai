module OrcAI.Core.Tests.ValidateCommandTests

open Xunit
open Testably.Abstractions.Testing
open OrcAI.Core.Domain
open OrcAI.Core.GhClient
open OrcAI.Core.Deps
open OrcAI.Core.ValidateCommand

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

/// Build a MockFileSystem pre-populated with a valid YAML config and its template.
let private writeMockYaml (fs: MockFileSystem) (yaml: string) (templateContent: string) : string =
    let dir          = "/work"
    fs.Directory.CreateDirectory(dir) |> ignore
    let templatePath = dir + "/template.md"
    fs.File.WriteAllText(templatePath, templateContent)
    let resolvedYaml = yaml.Replace("TEMPLATE_PLACEHOLDER", "./template.md")
    let yamlPath     = dir + "/job.yml"
    fs.File.WriteAllText(yamlPath, resolvedYaml)
    yamlPath

let private validYaml =
    "job:\n" +
    "  title: \"Add AGENTS.md\"\n" +
    "  org: \"myorg\"\n" +
    "repos:\n" +
    "  - \"repo-a\"\n" +
    "  - \"repo-b\"\n" +
    "issue:\n" +
    "  template: \"TEMPLATE_PLACEHOLDER\"\n" +
    "  labels: [\"documentation\"]\n"

/// A fake IGhClient whose RepoExists result is controlled per-call.
/// `repoResults` maps repo string → Result<unit, string>; missing entries default to Ok.
type FakeGhClient(repoResults: Map<string, Result<unit, string>>) =

    let notImpl name = failwith $"FakeGhClient.{name} not expected in validate tests"

    interface IGhClient with
        member _.RepoExists repo =
            let (RepoName r) = repo
            match Map.tryFind r repoResults with
            | Some result -> async { return result }
            | None        -> async { return Ok () }

        member _.FindProject _ _           = notImpl "FindProject"
        member _.CreateProject _ _         = notImpl "CreateProject"
        member _.DeleteProject _           = notImpl "DeleteProject"
        member _.ListLabels _              = notImpl "ListLabels"
        member _.CreateLabel _ _           = notImpl "CreateLabel"
        member _.FindIssue _ _             = notImpl "FindIssue"
        member _.CreateIssue _ _ _ _       = notImpl "CreateIssue"
        member _.CloseIssue _ _            = notImpl "CloseIssue"
        member _.AddIssueToProject _ _     = notImpl "AddIssueToProject"
        member _.AssignIssue _ _ _         = notImpl "AssignIssue"
        member _.FindPrsForIssue _ _       = notImpl "FindPrsForIssue"
        member _.ClosePr _ _               = notImpl "ClosePr"
        member _.ListRepos _               = notImpl "ListRepos"

/// A FakeGhClient that asserts it is never called (for file/schema error tests).
type NeverCalledGhClient() =
    let boom name = failwith $"GhClient.{name} must not be called when file/config is invalid"
    interface IGhClient with
        member _.RepoExists _              = boom "RepoExists"
        member _.FindProject _ _           = boom "FindProject"
        member _.CreateProject _ _         = boom "CreateProject"
        member _.DeleteProject _           = boom "DeleteProject"
        member _.ListLabels _              = boom "ListLabels"
        member _.CreateLabel _ _           = boom "CreateLabel"
        member _.FindIssue _ _             = boom "FindIssue"
        member _.CreateIssue _ _ _ _       = boom "CreateIssue"
        member _.CloseIssue _ _            = boom "CloseIssue"
        member _.AddIssueToProject _ _     = boom "AddIssueToProject"
        member _.AssignIssue _ _ _         = boom "AssignIssue"
        member _.FindPrsForIssue _ _       = boom "FindPrsForIssue"
        member _.ClosePr _ _               = boom "ClosePr"
        member _.ListRepos _               = boom "ListRepos"

let private makeDeps (fs: MockFileSystem) (client: IGhClient) : OrcAIDeps =
    { GhClient    = client
      AuthContext = { new OrcAI.Core.AuthContext.IAuthContext with
                         member _.GetToken() = async { return Ok "fake-token" } }
      FileSystem  = fs :> System.IO.Abstractions.IFileSystem
      Config      = OrcAI.Core.OrcAIConfig.empty }

let private makeInput path noParallel =
    { YamlPath = path; NoParallel = noParallel; MaxConcurrency = 4; ContinueOnError = false }

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

[<Fact>]
let ``validate returns IsValid=true when file exists, config parses, and all repos exist`` () =
    let fs     = MockFileSystem()
    let path   = writeMockYaml fs validYaml "# body"
    let client = FakeGhClient(Map.empty) // all repos → Ok
    let deps   = makeDeps fs client
    let input  = makeInput path false

    let results = execute deps [path] input |> Async.RunSynchronously

    let (_, result) = List.exactlyOne results
    Assert.True(result.IsValid)
    Assert.Empty(result.ConfigErrors)
    Assert.Empty(result.RepoErrors)

[<Fact>]
let ``validate returns error immediately when file does not exist`` () =
    let fs     = MockFileSystem()
    let client = NeverCalledGhClient()
    let deps   = makeDeps fs client
    let path   = "/nonexistent/job.yml"
    let input  = makeInput path false

    let results = execute deps [path] input |> Async.RunSynchronously

    let (_, result) = List.exactlyOne results
    Assert.False(result.IsValid)
    Assert.NotEmpty(result.ConfigErrors)
    Assert.Empty(result.RepoErrors)

[<Fact>]
let ``validate returns config error when YAML is malformed`` () =
    let fs  = MockFileSystem()
    fs.Directory.CreateDirectory("/work") |> ignore
    let yamlPath = "/work/job.yml"
    fs.File.WriteAllText(yamlPath, "repos:\n  - r\n")  // missing 'job' section
    let client = NeverCalledGhClient()
    let deps   = makeDeps fs client
    let input  = makeInput yamlPath false

    let results = execute deps [yamlPath] input |> Async.RunSynchronously

    let (_, result) = List.exactlyOne results
    Assert.False(result.IsValid)
    Assert.NotEmpty(result.ConfigErrors)
    Assert.Empty(result.RepoErrors)

[<Fact>]
let ``validate returns config error when template file is missing`` () =
    let fs  = MockFileSystem()
    fs.Directory.CreateDirectory("/work") |> ignore
    let yamlPath = "/work/job.yml"
    let yaml =
        "job:\n" +
        "  title: \"T\"\n" +
        "  org: \"o\"\n" +
        "repos:\n" +
        "  - \"r\"\n" +
        "issue:\n" +
        "  template: \"./missing.md\"\n"
    fs.File.WriteAllText(yamlPath, yaml)
    let client = NeverCalledGhClient()
    let deps   = makeDeps fs client
    let input  = makeInput yamlPath false

    let results = execute deps [yamlPath] input |> Async.RunSynchronously

    let (_, result) = List.exactlyOne results
    Assert.False(result.IsValid)
    Assert.NotEmpty(result.ConfigErrors)
    Assert.Empty(result.RepoErrors)

[<Fact>]
let ``validate returns repo error for one inaccessible repo and reports others as ok`` () =
    let fs   = MockFileSystem()
    let path = writeMockYaml fs validYaml "# body"
    // repo-a fails, repo-b succeeds
    let repoMap = Map.ofList [ "myorg/repo-a", Error "not found"; "myorg/repo-b", Ok () ]
    let client  = FakeGhClient(repoMap)
    let deps    = makeDeps fs client
    let input   = makeInput path false

    let results = execute deps [path] input |> Async.RunSynchronously

    let (_, result) = List.exactlyOne results
    Assert.False(result.IsValid)
    Assert.Empty(result.ConfigErrors)
    Assert.Equal(1, result.RepoErrors.Length)
    let (RepoName errRepo, _) = result.RepoErrors[0]
    Assert.Equal("myorg/repo-a", errRepo)

[<Fact>]
let ``validate collects errors for all inaccessible repos, not just the first`` () =
    let fs   = MockFileSystem()
    let path = writeMockYaml fs validYaml "# body"
    // both repos fail
    let repoMap = Map.ofList [ "myorg/repo-a", Error "404"; "myorg/repo-b", Error "403" ]
    let client  = FakeGhClient(repoMap)
    let deps    = makeDeps fs client
    let input   = makeInput path false

    let results = execute deps [path] input |> Async.RunSynchronously

    let (_, result) = List.exactlyOne results
    Assert.False(result.IsValid)
    Assert.Empty(result.ConfigErrors)
    Assert.Equal(2, result.RepoErrors.Length)

[<Fact>]
let ``validate with NoParallel=true returns same results as parallel`` () =
    let fs   = MockFileSystem()
    let path = writeMockYaml fs validYaml "# body"
    let repoMap = Map.ofList [ "myorg/repo-a", Error "not found" ]
    let client  = FakeGhClient(repoMap)
    let deps    = makeDeps fs client

    let parallelResult =
        execute deps [path] (makeInput path false)
        |> Async.RunSynchronously
        |> List.exactlyOne |> snd

    let seqResult =
        execute deps [path] (makeInput path true)
        |> Async.RunSynchronously
        |> List.exactlyOne |> snd

    Assert.Equal(parallelResult.IsValid,       seqResult.IsValid)
    Assert.Equal<string list>(parallelResult.ConfigErrors,  seqResult.ConfigErrors)
    Assert.Equal(parallelResult.RepoErrors.Length, seqResult.RepoErrors.Length)
