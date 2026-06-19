module OrcAI.Core.Tests.DependencyResolutionTests

open System.IO
open Testably.Abstractions.Testing
open OrcAI.Core.Domain
open OrcAI.Core.DependencyResolution
open Xunit

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

let private dir = "/work"

let private setup (fs: MockFileSystem) =
    fs.Directory.CreateDirectory(dir) |> ignore
    fs.File.WriteAllText($"{dir}/template.md", "# body")

let private baseYaml =
    "job:\n  title: \"Test Job\"\n  org: \"myorg\"\n" +
    "repos:\n  - \"repo-a\"\n  - \"repo-b\"\n" +
    "issue:\n  template: \"./template.md\"\n  labels: []\n"

/// Write a YAML file that has no depends_on entries.
let private writeNoDeps (fs: MockFileSystem) (name: string) =
    let path = $"{dir}/{name}"
    fs.File.WriteAllText(path, baseYaml)
    path

/// Write a YAML file with one or more dependsOn entries (camelCase keys, matching CamelCaseNamingConvention).
let private writeDeps (fs: MockFileSystem) (name: string) (deps: (string * string) list) =
    let depsSection =
        if deps.IsEmpty then ""
        else
            "dependsOn:\n" +
            (deps
             |> List.map (fun (job, cond) -> $"  - job: {job}\n    condition: {cond}\n")
             |> String.concat "")
    let path = $"{dir}/{name}"
    fs.File.WriteAllText(path, baseYaml + depsSection)
    path

/// Write a YAML with full dependsOn entry control (camelCase keys, matching CamelCaseNamingConvention).
let private writeFullDeps (fs: MockFileSystem) (name: string) (deps: (string * string * string * string) list) =
    let depsSection =
        if deps.IsEmpty then ""
        else
            "dependsOn:\n" +
            (deps
             |> List.map (fun (job, cond, scope, untracked) ->
                 $"  - job: {job}\n    condition: {cond}\n    scope: {scope}\n    untrackedRepos: {untracked}\n")
             |> String.concat "")
    let path = $"{dir}/{name}"
    fs.File.WriteAllText(path, baseYaml + depsSection)
    path

/// Write a lock file for an upstream YAML so filterRepos has data to check.
let private writeLock
    (fs: MockFileSystem)
    (upstreamYamlPath: string)
    (repos: RepoName list)
    (issues: (RepoName * int) list)
    (prs: (RepoName * int * int * string) list)    // repo, prNum, issueNum, state
    =
    let project = { Org = OrgName "myorg"; Number = 1; Title = "Test"; Url = "" }
    let lock : LockFile =
        { LockedAt     = System.DateTimeOffset.MinValue
          YamlHash     = "hash"
          TemplateHash = "hash"
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
    OrcAI.Core.LockFile.write (fs :> System.IO.Abstractions.IFileSystem) upstreamYamlPath lock

// ---------------------------------------------------------------------------
// resolveOrder tests
// ---------------------------------------------------------------------------

[<Fact>]
let ``resolveOrder with no deps returns just the file itself`` () =
    let fs = MockFileSystem()
    setup fs
    let path = writeNoDeps fs "job.yml"
    let result = resolveOrder (fs :> System.IO.Abstractions.IFileSystem) path
    let absPath = Path.GetFullPath(path)
    Assert.Equal(Ok [ absPath ], result)

[<Fact>]
let ``resolveOrder with linear chain returns dep before root`` () =
    let fs = MockFileSystem()
    setup fs
    let _depPath = writeNoDeps fs "job-b.yml"
    let rootPath = writeDeps fs "job-a.yml" [ ("./job-b.yml", "pr_merged") ]
    let result = resolveOrder (fs :> System.IO.Abstractions.IFileSystem) rootPath
    let absRoot = Path.GetFullPath(rootPath)
    let absDep  = Path.GetFullPath($"{dir}/job-b.yml")
    Assert.Equal(Ok [ absDep; absRoot ], result)

[<Fact>]
let ``resolveOrder with deep chain returns correct topological order`` () =
    let fs = MockFileSystem()
    setup fs
    let _pathC = writeNoDeps fs "job-c.yml"
    let _pathB = writeDeps fs "job-b.yml" [ ("./job-c.yml", "issue_closed") ]
    let pathA  = writeDeps fs "job-a.yml" [ ("./job-b.yml", "pr_merged") ]
    let result = resolveOrder (fs :> System.IO.Abstractions.IFileSystem) pathA
    let absA = Path.GetFullPath($"{dir}/job-a.yml")
    let absB = Path.GetFullPath($"{dir}/job-b.yml")
    let absC = Path.GetFullPath($"{dir}/job-c.yml")
    Assert.Equal(Ok [ absC; absB; absA ], result)

[<Fact>]
let ``resolveOrder with diamond deduplicates shared dep`` () =
    // A depends_on B and C; both B and C depend_on D → D should appear only once.
    let fs = MockFileSystem()
    setup fs
    let _pathD = writeNoDeps fs "job-d.yml"
    let _pathB = writeDeps fs "job-b.yml" [ ("./job-d.yml", "pr_merged") ]
    let _pathC = writeDeps fs "job-c.yml" [ ("./job-d.yml", "pr_merged") ]
    let pathA  = writeDeps fs "job-a.yml" [ ("./job-b.yml", "pr_merged"); ("./job-c.yml", "pr_merged") ]
    let result = resolveOrder (fs :> System.IO.Abstractions.IFileSystem) pathA
    match result with
    | Error e -> Assert.Fail($"Expected Ok but got Error: {e}")
    | Ok order ->
        let absA = Path.GetFullPath($"{dir}/job-a.yml")
        let absB = Path.GetFullPath($"{dir}/job-b.yml")
        let absC = Path.GetFullPath($"{dir}/job-c.yml")
        let absD = Path.GetFullPath($"{dir}/job-d.yml")
        // D appears exactly once
        Assert.Equal(1, order |> List.filter ((=) absD) |> List.length)
        // D before B and C; B and C before A
        let indexOf x = order |> List.findIndex ((=) x)
        Assert.True(indexOf absD < indexOf absB)
        Assert.True(indexOf absD < indexOf absC)
        Assert.True(indexOf absB < indexOf absA)
        Assert.True(indexOf absC < indexOf absA)

[<Fact>]
let ``resolveOrder returns error on direct cycle`` () =
    let fs = MockFileSystem()
    setup fs
    // A depends_on B; B depends_on A
    let _pathB = writeDeps fs "job-b.yml" [ ("./job-a.yml", "pr_merged") ]
    let pathA  = writeDeps fs "job-a.yml" [ ("./job-b.yml", "pr_merged") ]
    let result = resolveOrder (fs :> System.IO.Abstractions.IFileSystem) pathA
    match result with
    | Ok _ -> Assert.Fail("Expected Error but got Ok")
    | Error msg ->
        Assert.Contains("Circular dependency", msg)

[<Fact>]
let ``resolveOrder returns error when upstream file is missing`` () =
    let fs = MockFileSystem()
    setup fs
    let pathA = writeDeps fs "job-a.yml" [ ("./missing.yml", "pr_merged") ]
    let result = resolveOrder (fs :> System.IO.Abstractions.IFileSystem) pathA
    match result with
    | Ok _ -> Assert.Fail("Expected Error but got Ok")
    | Error msg ->
        Assert.Contains("missing.yml", msg)

// ---------------------------------------------------------------------------
// resolveChain tests
// ---------------------------------------------------------------------------

[<Fact>]
let ``resolveChain with no deps returns paths as-is`` () =
    let fs = MockFileSystem()
    setup fs
    let pathA = writeNoDeps fs "job-a.yml"
    let pathB = writeNoDeps fs "job-b.yml"
    let result = resolveChain (fs :> System.IO.Abstractions.IFileSystem) [ pathA; pathB ]
    let absA = Path.GetFullPath(pathA)
    let absB = Path.GetFullPath(pathB)
    Assert.Equal(Ok [ (absA, false); (absB, false) ], result)

[<Fact>]
let ``resolveChain expands dependency before the dependent`` () =
    let fs = MockFileSystem()
    setup fs
    let _pathB = writeNoDeps fs "job-b.yml"
    let pathA  = writeDeps  fs "job-a.yml" [ ("./job-b.yml", "pr_merged") ]
    let result = resolveChain (fs :> System.IO.Abstractions.IFileSystem) [ pathA ]
    let absA = Path.GetFullPath(pathA)
    let absB = Path.GetFullPath($"{dir}/job-b.yml")
    Assert.Equal(Ok [ (absB, true); (absA, false) ], result)

[<Fact>]
let ``resolveChain deduplicates when user provides both dep and dependent`` () =
    // User supplies [B, A]; A depends_on B. B should appear only once and NOT be marked as a dep.
    let fs = MockFileSystem()
    setup fs
    let pathB = writeNoDeps fs "job-b.yml"
    let pathA = writeDeps  fs "job-a.yml" [ ("./job-b.yml", "pr_merged") ]
    let result = resolveChain (fs :> System.IO.Abstractions.IFileSystem) [ pathB; pathA ]
    let absA = Path.GetFullPath(pathA)
    let absB = Path.GetFullPath(pathB)
    // B is in the user set → isDep = false; appears before A; A's dep-expansion of B is a no-op (already seen).
    Assert.Equal(Ok [ (absB, false); (absA, false) ], result)

[<Fact>]
let ``resolveChain passes through invalid YAML without propagating error`` () =
    // An invalid YAML file should be included as-is so executeSingle can report the proper error.
    let fs = MockFileSystem()
    setup fs
    let pathBad   = $"{dir}/bad.yml"
    let pathGood  = writeNoDeps fs "good.yml"
    fs.File.WriteAllText(pathBad, "not: valid: yaml: !!!\n")
    let result = resolveChain (fs :> System.IO.Abstractions.IFileSystem) [ pathBad; pathGood ]
    let absBad  = Path.GetFullPath(pathBad)
    let absGood = Path.GetFullPath(pathGood)
    match result with
    | Error e -> Assert.Fail($"Expected Ok but got Error: {e}")
    | Ok chain ->
        Assert.True(chain |> List.exists (fun (p, _) -> p = absBad),  "bad.yml should be in chain")
        Assert.True(chain |> List.exists (fun (p, _) -> p = absGood), "good.yml should be in chain")

[<Fact>]
let ``resolveChain returns error on cycle in valid YAML`` () =
    let fs = MockFileSystem()
    setup fs
    let _pathB = writeDeps fs "job-b.yml" [ ("./job-a.yml", "pr_merged") ]
    let pathA  = writeDeps fs "job-a.yml" [ ("./job-b.yml", "pr_merged") ]
    let result = resolveChain (fs :> System.IO.Abstractions.IFileSystem) [ pathA ]
    match result with
    | Ok _    -> Assert.Fail("Expected Error but got Ok")
    | Error _ -> ()

// ---------------------------------------------------------------------------
// filterRepos tests — per_repo scope
// ---------------------------------------------------------------------------

let private repoA = RepoName "myorg/repo-a"
let private repoB = RepoName "myorg/repo-b"

[<Fact>]
let ``filterRepos per_repo pr_merged includes only repos with merged PR`` () =
    let fs    = MockFileSystem()
    setup fs
    let upPath = writeNoDeps fs "upstream.yml"
    let downPath = writeFullDeps fs "downstream.yml"
                       [ ("./upstream.yml", "pr_merged", "per_repo", "include") ]
    writeLock fs upPath
        [ repoA; repoB ]
        [ (repoA, 10); (repoB, 20) ]
        [ (repoA, 1, 10, "MERGED") ]   // repoA has merged PR; repoB does not
    let config =
        match OrcAI.Core.YamlConfig.parseFile (fs :> System.IO.Abstractions.IFileSystem) downPath with
        | Ok c -> c
        | Error e -> failwith e
    let client = FakeGhClient.from { FakeGhClient.defaults with
                                         FindPrsForIssue = fun _ _ -> async { return [] } }
    let result =
        filterRepos client (fs :> System.IO.Abstractions.IFileSystem) config dir
        |> Async.RunSynchronously
    Assert.Equal(Ok [ repoA ], result)

[<Fact>]
let ``filterRepos per_repo issue_closed includes only repos with closed issue`` () =
    let fs    = MockFileSystem()
    setup fs
    let upPath   = writeNoDeps fs "upstream.yml"
    let downPath = writeFullDeps fs "downstream.yml"
                       [ ("./upstream.yml", "issue_closed", "per_repo", "include") ]
    writeLock fs upPath
        [ repoA; repoB ]
        [ (repoA, 10); (repoB, 20) ]
        []
    let config =
        match OrcAI.Core.YamlConfig.parseFile (fs :> System.IO.Abstractions.IFileSystem) downPath with
        | Ok c -> c
        | Error e -> failwith e
    let client = FakeGhClient.from
                     { FakeGhClient.defaults with
                           GetIssueState = fun repo _ ->
                               async {
                                   return if repo = repoA then Some "CLOSED" else Some "OPEN"
                               } }
    let result =
        filterRepos client (fs :> System.IO.Abstractions.IFileSystem) config dir
        |> Async.RunSynchronously
    Assert.Equal(Ok [ repoA ], result)

[<Fact>]
let ``filterRepos per_repo untracked_repos skip excludes repos not in upstream lock`` () =
    let fs    = MockFileSystem()
    setup fs
    let upPath   = writeNoDeps fs "upstream.yml"
    let downPath = writeFullDeps fs "downstream.yml"
                       [ ("./upstream.yml", "pr_merged", "per_repo", "skip") ]
    // Upstream lock only has repoA; repoB is untracked.
    writeLock fs upPath
        [ repoA ]
        [ (repoA, 10) ]
        [ (repoA, 1, 10, "MERGED") ]
    let config =
        match OrcAI.Core.YamlConfig.parseFile (fs :> System.IO.Abstractions.IFileSystem) downPath with
        | Ok c -> c
        | Error e -> failwith e
    let client = FakeGhClient.from { FakeGhClient.defaults with
                                         FindPrsForIssue = fun _ _ -> async { return [] } }
    let result =
        filterRepos client (fs :> System.IO.Abstractions.IFileSystem) config dir
        |> Async.RunSynchronously
    Assert.Equal(Ok [ repoA ], result)

[<Fact>]
let ``filterRepos per_repo untracked_repos include keeps repos not in upstream lock`` () =
    let fs    = MockFileSystem()
    setup fs
    let upPath   = writeNoDeps fs "upstream.yml"
    let downPath = writeFullDeps fs "downstream.yml"
                       [ ("./upstream.yml", "pr_merged", "per_repo", "include") ]
    // Upstream lock only has repoA; repoB is untracked → should be included.
    writeLock fs upPath
        [ repoA ]
        [ (repoA, 10) ]
        [ (repoA, 1, 10, "MERGED") ]
    let config =
        match OrcAI.Core.YamlConfig.parseFile (fs :> System.IO.Abstractions.IFileSystem) downPath with
        | Ok c -> c
        | Error e -> failwith e
    let client = FakeGhClient.from { FakeGhClient.defaults with
                                         FindPrsForIssue = fun _ _ -> async { return [] } }
    let result =
        filterRepos client (fs :> System.IO.Abstractions.IFileSystem) config dir
        |> Async.RunSynchronously
    match result with
    | Error e -> Assert.Fail($"Expected Ok but got Error: {e}")
    | Ok repos ->
        Assert.Contains(repoA, repos)
        Assert.Contains(repoB, repos)

// ---------------------------------------------------------------------------
// filterRepos tests — all_repos scope
// ---------------------------------------------------------------------------

[<Fact>]
let ``filterRepos all_repos returns Ok when all upstream repos have met condition`` () =
    let fs    = MockFileSystem()
    setup fs
    let upPath   = writeNoDeps fs "upstream.yml"
    let downPath = writeFullDeps fs "downstream.yml"
                       [ ("./upstream.yml", "pr_merged", "all_repos", "include") ]
    writeLock fs upPath
        [ repoA; repoB ]
        [ (repoA, 10); (repoB, 20) ]
        [ (repoA, 1, 10, "MERGED"); (repoB, 2, 20, "MERGED") ]
    let config =
        match OrcAI.Core.YamlConfig.parseFile (fs :> System.IO.Abstractions.IFileSystem) downPath with
        | Ok c -> c
        | Error e -> failwith e
    let client = FakeGhClient.from { FakeGhClient.defaults with
                                         FindPrsForIssue = fun _ _ -> async { return [] } }
    let result =
        filterRepos client (fs :> System.IO.Abstractions.IFileSystem) config dir
        |> Async.RunSynchronously
    match result with
    | Error e -> Assert.Fail($"Expected Ok but got Error: {e}")
    | Ok _    -> ()

[<Fact>]
let ``filterRepos all_repos returns Error when some upstream repos have not met condition`` () =
    let fs    = MockFileSystem()
    setup fs
    let upPath   = writeNoDeps fs "upstream.yml"
    let downPath = writeFullDeps fs "downstream.yml"
                       [ ("./upstream.yml", "pr_merged", "all_repos", "include") ]
    writeLock fs upPath
        [ repoA; repoB ]
        [ (repoA, 10); (repoB, 20) ]
        [ (repoA, 1, 10, "MERGED") ]   // repoB has no merged PR → gate should block
    let config =
        match OrcAI.Core.YamlConfig.parseFile (fs :> System.IO.Abstractions.IFileSystem) downPath with
        | Ok c -> c
        | Error e -> failwith e
    let client = FakeGhClient.from { FakeGhClient.defaults with
                                         FindPrsForIssue = fun _ _ -> async { return [] } }
    let result =
        filterRepos client (fs :> System.IO.Abstractions.IFileSystem) config dir
        |> Async.RunSynchronously
    match result with
    | Ok _    -> Assert.Fail("Expected Error but got Ok")
    | Error msg -> Assert.Contains("gate not met", msg)

[<Fact>]
let ``filterRepos all_repos returns Error when no lock file exists`` () =
    let fs    = MockFileSystem()
    setup fs
    let _upPath  = writeNoDeps fs "upstream.yml"   // no lock written
    let downPath = writeFullDeps fs "downstream.yml"
                       [ ("./upstream.yml", "pr_merged", "all_repos", "include") ]
    let config =
        match OrcAI.Core.YamlConfig.parseFile (fs :> System.IO.Abstractions.IFileSystem) downPath with
        | Ok c -> c
        | Error e -> failwith e
    let client = FakeGhClient.from FakeGhClient.defaults
    let result =
        filterRepos client (fs :> System.IO.Abstractions.IFileSystem) config dir
        |> Async.RunSynchronously
    match result with
    | Ok _    -> Assert.Fail("Expected Error but got Ok")
    | Error _ -> ()
