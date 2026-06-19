module OrcAI.Core.Tests.ValidateCommandTests

open Xunit
open Testably.Abstractions.Testing
open OrcAI.Core.Domain
open OrcAI.Core.ValidateCommand
open OrcAI.Core.Tests.TestData

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

/// Client where all methods throw — validates that GitHub is never contacted
/// when config or file validation fails before reaching repo checks.
let private neverCalledClient () =
    FakeGhClient.from FakeGhClient.neverCalledHandlers

/// Client where only ReposExist is wired; all other methods throw.
/// Pass an empty map to have all repos return Ok.
let private validateClient (repoResults: Map<string, Result<unit, string>>) =
    FakeGhClient.from
        { FakeGhClient.neverCalledHandlers with
            ReposExist = fun repos -> async {
                return repos |> List.map (fun repo ->
                    let (RepoName r) = repo
                    repo, (Map.tryFind r repoResults |> Option.defaultValue (Ok ())))
                |> Map.ofList } }

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

[<Fact>]
let ``validate returns IsValid=true when file exists, config parses, and all repos exist`` () =
    let fs    = MockFileSystem()
    let path  = Given.yamlFile fs A.Yaml.valid "# body"
    let deps  = Given.deps fs (validateClient Map.empty)
    let input = A.ValidateInput.defaults path

    let results = execute deps [path] input |> Async.RunSynchronously

    let (_, result) = List.exactlyOne results
    Assert.True(result.IsValid)
    Assert.Empty(result.ConfigErrors)
    Assert.Empty(result.RepoErrors)

[<Fact>]
let ``validate returns error immediately when file does not exist`` () =
    let fs    = MockFileSystem()
    let deps  = Given.deps fs (neverCalledClient ())
    let input = A.ValidateInput.defaults "/nonexistent/job.yml"

    let results = execute deps ["/nonexistent/job.yml"] input |> Async.RunSynchronously

    let (_, result) = List.exactlyOne results
    Assert.False(result.IsValid)
    Assert.NotEmpty(result.ConfigErrors)
    Assert.Empty(result.RepoErrors)

[<Fact>]
let ``validate returns config error when YAML is malformed`` () =
    let fs = MockFileSystem()
    fs.Directory.CreateDirectory("/work") |> ignore
    fs.File.WriteAllText("/work/job.yml", "repos:\n  - r\n")
    let deps  = Given.deps fs (neverCalledClient ())
    let input = A.ValidateInput.defaults "/work/job.yml"

    let results = execute deps ["/work/job.yml"] input |> Async.RunSynchronously

    let (_, result) = List.exactlyOne results
    Assert.False(result.IsValid)
    Assert.NotEmpty(result.ConfigErrors)
    Assert.Empty(result.RepoErrors)

[<Fact>]
let ``validate returns config error when template file is missing`` () =
    let fs = MockFileSystem()
    fs.Directory.CreateDirectory("/work") |> ignore
    let yaml =
        "job:\n  title: \"T\"\n  org: \"o\"\n" +
        "repos:\n  - \"r\"\n" +
        "issue:\n  template: \"./missing.md\"\n"
    fs.File.WriteAllText("/work/job.yml", yaml)
    let deps  = Given.deps fs (neverCalledClient ())
    let input = A.ValidateInput.defaults "/work/job.yml"

    let results = execute deps ["/work/job.yml"] input |> Async.RunSynchronously

    let (_, result) = List.exactlyOne results
    Assert.False(result.IsValid)
    Assert.NotEmpty(result.ConfigErrors)
    Assert.Empty(result.RepoErrors)

[<Fact>]
let ``validate returns repo error for one inaccessible repo and reports others as ok`` () =
    let fs       = MockFileSystem()
    let path     = Given.yamlFile fs A.Yaml.valid "# body"
    let repoMap  = Map.ofList [ "myorg/repo-a", Error "not found"; "myorg/repo-b", Ok () ]
    let deps     = Given.deps fs (validateClient repoMap)
    let input    = A.ValidateInput.defaults path |> A.ValidateInput.withSkipLock true

    let results = execute deps [path] input |> Async.RunSynchronously

    let (_, result) = List.exactlyOne results
    Assert.False(result.IsValid)
    Assert.Empty(result.ConfigErrors)
    Assert.Equal(1, result.RepoErrors.Length)
    let (RepoName errRepo, _) = result.RepoErrors[0]
    Assert.Equal("myorg/repo-a", errRepo)

[<Fact>]
let ``validate collects errors for all inaccessible repos, not just the first`` () =
    let fs      = MockFileSystem()
    let path    = Given.yamlFile fs A.Yaml.valid "# body"
    let repoMap = Map.ofList [ "myorg/repo-a", Error "404"; "myorg/repo-b", Error "403" ]
    let deps    = Given.deps fs (validateClient repoMap)
    let input   = A.ValidateInput.defaults path |> A.ValidateInput.withSkipLock true

    let results = execute deps [path] input |> Async.RunSynchronously

    let (_, result) = List.exactlyOne results
    Assert.False(result.IsValid)
    Assert.Empty(result.ConfigErrors)
    Assert.Equal(2, result.RepoErrors.Length)

[<Fact>]
let ``validate with NoParallel=true returns same results as parallel`` () =
    let fs      = MockFileSystem()
    let path    = Given.yamlFile fs A.Yaml.valid "# body"
    let repoMap = Map.ofList [ "myorg/repo-a", Error "not found" ]
    let deps    = Given.deps fs (validateClient repoMap)

    let parallelResult =
        execute deps [path] (A.ValidateInput.defaults path |> A.ValidateInput.withSkipLock true)
        |> Async.RunSynchronously |> List.exactlyOne |> snd

    let seqResult =
        execute deps [path] (A.ValidateInput.defaults path |> A.ValidateInput.withSkipLock true |> A.ValidateInput.withNoParallel true)
        |> Async.RunSynchronously |> List.exactlyOne |> snd

    Assert.Equal(parallelResult.IsValid,            seqResult.IsValid)
    Assert.Equal<string list>(parallelResult.ConfigErrors, seqResult.ConfigErrors)
    Assert.Equal(parallelResult.RepoErrors.Length,  seqResult.RepoErrors.Length)

// ---------------------------------------------------------------------------
// Lock-file-aware tests
// ---------------------------------------------------------------------------

[<Fact>]
let ``validate with no lock file checks all repos`` () =
    let fs      = MockFileSystem()
    let path    = Given.yamlFile fs A.Yaml.valid "# body"
    let repoMap = Map.ofList [ "myorg/repo-a", Error "not found" ]
    let deps    = Given.deps fs (validateClient repoMap)
    let input   = A.ValidateInput.defaults path  // SkipLock = false, no lock file on disk

    let results = execute deps [path] input |> Async.RunSynchronously

    let (_, result) = List.exactlyOne results
    Assert.False(result.IsValid)
    Assert.Empty(result.ConfigErrors)
    Assert.Equal(1, result.RepoErrors.Length)

[<Fact>]
let ``validate with lock file covering all repos skips repo checks`` () =
    let fs   = MockFileSystem()
    let path = Given.yamlFile fs A.Yaml.valid "# body"
    // Write a lock file that already has both repos from A.Yaml.valid.
    let lock = A.LockFile.defaults ()  // has myorg/repo-a and myorg/repo-b
    OrcAI.Core.LockFile.write (fs :> System.IO.Abstractions.IFileSystem) path lock
    let deps  = Given.deps fs (neverCalledClient ())
    let input = A.ValidateInput.defaults path  // SkipLock = false

    let results = execute deps [path] input |> Async.RunSynchronously

    let (_, result) = List.exactlyOne results
    Assert.True(result.IsValid)
    Assert.Empty(result.ConfigErrors)
    Assert.Empty(result.RepoErrors)

[<Fact>]
let ``validate with lock file only checks repos not already in lock`` () =
    let fs      = MockFileSystem()
    let path    = Given.yamlFile fs A.Yaml.valid "# body"
    // Lock only has repo-a; repo-b is new and must be live-checked.
    let lock    = A.LockFile.defaults () |> A.LockFile.withRepos [ RepoName "myorg/repo-a" ]
    OrcAI.Core.LockFile.write (fs :> System.IO.Abstractions.IFileSystem) path lock
    let repoMap = Map.ofList [ "myorg/repo-b", Error "not found" ]
    let deps    = Given.deps fs (validateClient repoMap)
    let input   = A.ValidateInput.defaults path  // SkipLock = false

    let results = execute deps [path] input |> Async.RunSynchronously

    let (_, result) = List.exactlyOne results
    Assert.False(result.IsValid)
    Assert.Empty(result.ConfigErrors)
    Assert.Equal(1, result.RepoErrors.Length)
    let (RepoName errRepo, _) = result.RepoErrors[0]
    Assert.Equal("myorg/repo-b", errRepo)

[<Fact>]
let ``validate with skip-lock checks all repos regardless of lock file`` () =
    let fs   = MockFileSystem()
    let path = Given.yamlFile fs A.Yaml.valid "# body"
    // Lock has both repos, but --skip-lock should still check them live.
    let lock = A.LockFile.defaults ()
    OrcAI.Core.LockFile.write (fs :> System.IO.Abstractions.IFileSystem) path lock
    let repoMap = Map.ofList [ "myorg/repo-a", Error "permission denied" ]
    let deps    = Given.deps fs (validateClient repoMap)
    let input   = A.ValidateInput.defaults path |> A.ValidateInput.withSkipLock true

    let results = execute deps [path] input |> Async.RunSynchronously

    let (_, result) = List.exactlyOne results
    Assert.False(result.IsValid)
    Assert.Empty(result.ConfigErrors)
    Assert.Equal(1, result.RepoErrors.Length)

// ---------------------------------------------------------------------------
// dependsOn validation tests
// ---------------------------------------------------------------------------

let private depYaml (upstreamRelPath: string) =
    "job:\n  title: \"T\"\n  org: \"myorg\"\n" +
    "repos:\n  - \"repo-a\"\n" +
    "issue:\n  template: \"./template.md\"\n  labels: []\n" +
    "dependsOn:\n" +
    $"  - job: {upstreamRelPath}\n" +
    "    condition: pr_merged\n"

[<Fact>]
let ``validate passes when dependsOn references an existing upstream file`` () =
    let fs = MockFileSystem()
    fs.Directory.CreateDirectory("/work") |> ignore
    fs.File.WriteAllText("/work/template.md", "# body")
    fs.File.WriteAllText("/work/upstream.yml",
        "job:\n  title: \"U\"\n  org: \"myorg\"\n" +
        "repos:\n  - \"repo-a\"\n" +
        "issue:\n  template: \"./template.md\"\n  labels: []\n")
    let downPath = "/work/downstream.yml"
    fs.File.WriteAllText(downPath, depYaml "./upstream.yml")
    let deps  = Given.deps fs (validateClient Map.empty)
    let input = A.ValidateInput.defaults downPath |> A.ValidateInput.withSkipLock true

    let results = execute deps [downPath] input |> Async.RunSynchronously

    let (_, result) = List.exactlyOne results
    Assert.True(result.IsValid)
    Assert.Empty(result.ConfigErrors)

[<Fact>]
let ``validate fails when dependsOn references a missing upstream file`` () =
    let fs = MockFileSystem()
    fs.Directory.CreateDirectory("/work") |> ignore
    fs.File.WriteAllText("/work/template.md", "# body")
    let downPath = "/work/downstream.yml"
    fs.File.WriteAllText(downPath, depYaml "./missing.yml")
    let deps  = Given.deps fs (neverCalledClient ())
    let input = A.ValidateInput.defaults downPath

    let results = execute deps [downPath] input |> Async.RunSynchronously

    let (_, result) = List.exactlyOne results
    Assert.False(result.IsValid)
    Assert.NotEmpty(result.ConfigErrors)
    Assert.Contains("missing.yml", result.ConfigErrors.[0])

[<Fact>]
let ``validate fails when dependsOn contains a circular reference`` () =
    let fs = MockFileSystem()
    fs.Directory.CreateDirectory("/work") |> ignore
    fs.File.WriteAllText("/work/template.md", "# body")
    let cycleYaml (otherPath: string) =
        "job:\n  title: \"T\"\n  org: \"myorg\"\n" +
        "repos:\n  - \"repo-a\"\n" +
        "issue:\n  template: \"./template.md\"\n  labels: []\n" +
        "dependsOn:\n" +
        $"  - job: {otherPath}\n" +
        "    condition: pr_merged\n"
    fs.File.WriteAllText("/work/b.yml", cycleYaml "./a.yml")
    let pathA = "/work/a.yml"
    fs.File.WriteAllText(pathA, cycleYaml "./b.yml")
    let deps  = Given.deps fs (neverCalledClient ())
    let input = A.ValidateInput.defaults pathA

    let results = execute deps [pathA] input |> Async.RunSynchronously

    let (_, result) = List.exactlyOne results
    Assert.False(result.IsValid)
    Assert.NotEmpty(result.ConfigErrors)
    Assert.Contains("Circular", result.ConfigErrors.[0])
