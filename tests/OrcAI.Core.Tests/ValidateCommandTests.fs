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
    FakeGhClient.make FakeGhClient.neverCalledHandlers

/// Client where only RepoExists is wired; all other methods throw.
/// Pass an empty map to have all repos return Ok.
let private validateClient (repoResults: Map<string, Result<unit, string>>) =
    FakeGhClient.make
        { FakeGhClient.neverCalledHandlers with
            RepoExists = fun repo ->
                let (RepoName r) = repo
                match Map.tryFind r repoResults with
                | Some result -> async { return result }
                | None        -> async { return Ok () } }

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

[<Fact>]
let ``validate returns IsValid=true when file exists, config parses, and all repos exist`` () =
    let fs    = MockFileSystem()
    let path  = A.Yaml.writeTo fs A.Yaml.valid "# body"
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
    let path     = A.Yaml.writeTo fs A.Yaml.valid "# body"
    let repoMap  = Map.ofList [ "myorg/repo-a", Error "not found"; "myorg/repo-b", Ok () ]
    let deps     = Given.deps fs (validateClient repoMap)
    let input    = A.ValidateInput.defaults path

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
    let path    = A.Yaml.writeTo fs A.Yaml.valid "# body"
    let repoMap = Map.ofList [ "myorg/repo-a", Error "404"; "myorg/repo-b", Error "403" ]
    let deps    = Given.deps fs (validateClient repoMap)
    let input   = A.ValidateInput.defaults path

    let results = execute deps [path] input |> Async.RunSynchronously

    let (_, result) = List.exactlyOne results
    Assert.False(result.IsValid)
    Assert.Empty(result.ConfigErrors)
    Assert.Equal(2, result.RepoErrors.Length)

[<Fact>]
let ``validate with NoParallel=true returns same results as parallel`` () =
    let fs      = MockFileSystem()
    let path    = A.Yaml.writeTo fs A.Yaml.valid "# body"
    let repoMap = Map.ofList [ "myorg/repo-a", Error "not found" ]
    let deps    = Given.deps fs (validateClient repoMap)

    let parallelResult =
        execute deps [path] (A.ValidateInput.defaults path)
        |> Async.RunSynchronously |> List.exactlyOne |> snd

    let seqResult =
        execute deps [path] (A.ValidateInput.defaults path |> A.ValidateInput.withNoParallel true)
        |> Async.RunSynchronously |> List.exactlyOne |> snd

    Assert.Equal(parallelResult.IsValid,            seqResult.IsValid)
    Assert.Equal<string list>(parallelResult.ConfigErrors, seqResult.ConfigErrors)
    Assert.Equal(parallelResult.RepoErrors.Length,  seqResult.RepoErrors.Length)
