module OrcAI.Core.Tests.YamlConfigTests

open System.Text
open Xunit
open Testably.Abstractions.Testing
open OrcAI.Core.YamlConfig
open OrcAI.Core.Domain
open OrcAI.Core.Tests.TestData

// ---------------------------------------------------------------------------
// File-based parsing tests (MockFileSystem)
// ---------------------------------------------------------------------------

[<Fact>]
let ``parseFile returns error for missing file`` () =
    let fs = MockFileSystem()
    Assert.True(Result.isError (parseFile fs "/nonexistent/path/job.yml"))

[<Fact>]
let ``parseFile parses valid YAML into JobConfig`` () =
    let fs      = MockFileSystem()
    let path    = Given.yamlFile fs A.Yaml.valid "# Issue body"
    match parseFile fs path with
    | Error e  -> Assert.True(false, $"Expected Ok but got Error: {e}")
    | Ok cfg   ->
        Assert.Equal(OrgName "myorg", cfg.Org)
        Assert.Equal("Add AGENTS.md", cfg.ProjectTitle)
        Assert.Equal("Add AGENTS.md", cfg.IssueTitle)
        Assert.Equal(2, cfg.Repos.Length)
        Assert.Contains(RepoName "myorg/repo-a", cfg.Repos)
        Assert.Contains(RepoName "myorg/repo-b", cfg.Repos)
        Assert.Equal("# Issue body", cfg.IssueBody)

[<Fact>]
let ``parseFile prefixes repos with org`` () =
    let fs   = MockFileSystem()
    let path = Given.yamlFile fs A.Yaml.valid "body"
    match parseFile fs path with
    | Error e -> Assert.True(false, $"Expected Ok but got Error: {e}")
    | Ok cfg  ->
        for (RepoName r) in cfg.Repos do
            Assert.StartsWith("myorg/", r)

[<Fact>]
let ``parseFile returns error when template file is missing`` () =
    let fs = MockFileSystem()
    fs.Directory.CreateDirectory("/work") |> ignore
    let yaml =
        "job:\n  title: \"T\"\n  org: \"o\"\n" +
        "repos:\n  - \"r\"\n" +
        "issue:\n  template: \"./missing.md\"\n"
    fs.File.WriteAllText("/work/job.yml", yaml)
    Assert.True(Result.isError (parseFile fs "/work/job.yml"))

[<Fact>]
let ``parseFile returns error when job section is missing`` () =
    let fs = MockFileSystem()
    fs.Directory.CreateDirectory("/work") |> ignore
    fs.File.WriteAllText("/work/job.yml", "repos:\n  - r\n")
    Assert.True(Result.isError (parseFile fs "/work/job.yml"))

[<Fact>]
let ``parseFile parses labels from YAML into JobConfig`` () =
    let fs   = MockFileSystem()
    let path = Given.yamlFile fs A.Yaml.valid "# Issue body"
    match parseFile fs path with
    | Error e -> Assert.True(false, $"Expected Ok but got Error: {e}")
    | Ok cfg  ->
        Assert.Equal(1, cfg.Labels.Length)
        Assert.Contains("documentation", cfg.Labels)

[<Fact>]
let ``parseFile sets Labels to empty list when not present in YAML`` () =
    let yaml =
        "job:\n  title: \"No Labels\"\n  org: \"myorg\"\n" +
        "repos:\n  - \"repo-a\"\n" +
        "issue:\n  template: \"TEMPLATE_PLACEHOLDER\"\n"
    let fs   = MockFileSystem()
    let path = Given.yamlFile fs yaml "body"
    match parseFile fs path with
    | Error e -> Assert.True(false, $"Expected Ok but got Error: {e}")
    | Ok cfg  -> Assert.Empty(cfg.Labels)

[<Fact>]
let ``computeHash returns consistent hex string for same content`` () =
    let fs = MockFileSystem()
    fs.Directory.CreateDirectory("/work") |> ignore
    fs.File.WriteAllText("/work/job.yml", "content: hello")
    let hash1 = computeHash fs "/work/job.yml"
    let hash2 = computeHash fs "/work/job.yml"
    Assert.Equal(hash1, hash2)
    Assert.Equal(64, hash1.Length)

[<Fact>]
let ``computeHash returns different hashes for different content`` () =
    let fs = MockFileSystem()
    fs.Directory.CreateDirectory("/work") |> ignore
    fs.File.WriteAllText("/work/a.yml", "content: hello")
    fs.File.WriteAllText("/work/b.yml", "content: world")
    Assert.NotEqual<string>(computeHash fs "/work/a.yml", computeHash fs "/work/b.yml")

// ---------------------------------------------------------------------------
// Pure parse tests (no file I/O)
// ---------------------------------------------------------------------------

let private pureParsYaml =
    "job:\n" +
    "  title: \"My Job\"\n" +
    "  org: \"acme\"\n" +
    "repos:\n" +
    "  - \"svc-a\"\n" +
    "  - \"svc-b\"\n" +
    "issue:\n" +
    "  template: \"./issue.md\"\n" +
    "  labels: [\"bug\", \"help wanted\"]\n"

[<Fact>]
let ``parse builds correct JobConfig from raw strings`` () =
    match parse pureParsYaml "/any/path/issue.md" "Issue body text" with
    | Error e -> Assert.True(false, $"Expected Ok but got Error: {e}")
    | Ok cfg  ->
        Assert.Equal(OrgName "acme", cfg.Org)
        Assert.Equal("My Job",          cfg.ProjectTitle)
        Assert.Equal("My Job",          cfg.IssueTitle)
        Assert.Equal("Issue body text", cfg.IssueBody)
        Assert.Equal(2, cfg.Repos.Length)
        Assert.Contains(RepoName "acme/svc-a", cfg.Repos)
        Assert.Contains(RepoName "acme/svc-b", cfg.Repos)

[<Fact>]
let ``parse prefixes all repos with org name`` () =
    match parse pureParsYaml "" "body" with
    | Error e -> Assert.True(false, $"Expected Ok: {e}")
    | Ok cfg  ->
        for (RepoName r) in cfg.Repos do
            Assert.StartsWith("acme/", r)

[<Fact>]
let ``parse returns labels from YAML`` () =
    match parse pureParsYaml "" "body" with
    | Error e -> Assert.True(false, $"Expected Ok: {e}")
    | Ok cfg  ->
        Assert.Equal(2, cfg.Labels.Length)
        Assert.Contains("bug", cfg.Labels)
        Assert.Contains("help wanted", cfg.Labels)

[<Fact>]
let ``parse returns empty labels when section absent`` () =
    let yaml =
        "job:\n  title: \"T\"\n  org: \"o\"\n" +
        "repos:\n  - \"r\"\n" +
        "issue:\n  template: \"./t.md\"\n"
    match parse yaml "" "body" with
    | Error e -> Assert.True(false, $"Expected Ok: {e}")
    | Ok cfg  -> Assert.Empty(cfg.Labels)

[<Fact>]
let ``parse returns error when job section is missing`` () =
    Assert.True(Result.isError (parse "repos:\n  - r\n" "" ""))

[<Fact>]
let ``parse returns error when repos list is empty`` () =
    let yaml = "job:\n  title: \"T\"\n  org: \"o\"\nrepos: []\nissue:\n  template: \"./t.md\"\n"
    Assert.True(Result.isError (parse yaml "" ""))

[<Fact>]
let ``parse returns error when issue section is missing`` () =
    let yaml = "job:\n  title: \"T\"\n  org: \"o\"\nrepos:\n  - r\n"
    Assert.True(Result.isError (parse yaml "" ""))

[<Fact>]
let ``parse returns error when title is blank`` () =
    let yaml = "job:\n  title: \"\"\n  org: \"o\"\nrepos:\n  - r\nissue:\n  template: \"./t.md\"\n"
    Assert.True(Result.isError (parse yaml "" ""))

[<Fact>]
let ``parse returns error when org is blank`` () =
    let yaml = "job:\n  title: \"T\"\n  org: \"\"\nrepos:\n  - r\nissue:\n  template: \"./t.md\"\n"
    Assert.True(Result.isError (parse yaml "" ""))

[<Fact>]
let ``parse defaults to AssignCopilot None when action is absent`` () =
    match parse pureParsYaml "/any/path/issue.md" "body" with
    | Error e -> Assert.True(false, $"Expected Ok but got Error: {e}")
    | Ok cfg  -> Assert.Equal(AssignCopilot None, cfg.Action)

[<Fact>]
let ``parse returns None for MaxAttempts when failures section absent`` () =
    match parse pureParsYaml "" "body" with
    | Error e -> Assert.True(false, $"Expected Ok: {e}")
    | Ok cfg  -> Assert.Equal(None, cfg.MaxAttempts)

[<Fact>]
let ``parse reads failures.maxAttempts when set`` () =
    let yaml =
        "job:\n  title: \"T\"\n  org: \"o\"\n" +
        "repos:\n  - \"r\"\n" +
        "issue:\n  template: \"./t.md\"\n" +
        "failures:\n  maxAttempts: 5\n"
    match parse yaml "" "body" with
    | Error e -> Assert.True(false, $"Expected Ok: {e}")
    | Ok cfg  -> Assert.Equal(Some 5, cfg.MaxAttempts)

[<Fact>]
let ``parse returns error when skipCopilot is present`` () =
    let yaml =
        "job:\n  title: \"My Job\"\n  org: \"acme\"\n  skipCopilot: true\n" +
        "repos:\n  - \"svc-a\"\n" +
        "issue:\n  template: \"./issue.md\"\n"
    let result = parse yaml "/any/path/issue.md" "body"
    Assert.True(Result.isError result)
    match result with
    | Error msg -> Assert.Contains("skipCopilot", msg)
    | Ok _ -> ()

// ---------------------------------------------------------------------------
// dependsOn — pure parse
// ---------------------------------------------------------------------------

let private baseYamlForDeps =
    "job:\n  title: \"T\"\n  org: \"o\"\n" +
    "repos:\n  - \"r\"\n" +
    "issue:\n  template: \"./t.md\"\n"

[<Fact>]
let ``parse parses a valid dependsOn entry with defaults`` () =
    let yaml =
        baseYamlForDeps +
        "dependsOn:\n" +
        "  - job: ./upstream.yml\n" +
        "    condition: pr_merged\n"
    match parse yaml "" "body" with
    | Error e -> Assert.True(false, $"Expected Ok but got Error: {e}")
    | Ok cfg  ->
        Assert.Equal(1, cfg.DependsOn.Length)
        let dep = cfg.DependsOn.[0]
        Assert.Equal("./upstream.yml", dep.Job)
        Assert.Equal(PrMerged, dep.Condition)
        Assert.Equal(PerRepo, dep.Scope)
        Assert.Equal(UntrackedReposBehavior.Include, dep.UntrackedRepos)

[<Fact>]
let ``parse parses dependsOn with issue_closed condition`` () =
    let yaml =
        baseYamlForDeps +
        "dependsOn:\n" +
        "  - job: ./up.yml\n" +
        "    condition: issue_closed\n"
    match parse yaml "" "body" with
    | Error e -> Assert.True(false, $"Expected Ok: {e}")
    | Ok cfg  -> Assert.Equal(IssueClosed, cfg.DependsOn.[0].Condition)

[<Fact>]
let ``parse parses dependsOn with all_repos scope`` () =
    let yaml =
        baseYamlForDeps +
        "dependsOn:\n" +
        "  - job: ./up.yml\n" +
        "    condition: pr_merged\n" +
        "    scope: all_repos\n"
    match parse yaml "" "body" with
    | Error e -> Assert.True(false, $"Expected Ok: {e}")
    | Ok cfg  -> Assert.Equal(AllRepos, cfg.DependsOn.[0].Scope)

[<Fact>]
let ``parse parses dependsOn with untrackedRepos skip`` () =
    let yaml =
        baseYamlForDeps +
        "dependsOn:\n" +
        "  - job: ./up.yml\n" +
        "    condition: pr_merged\n" +
        "    untrackedRepos: skip\n"
    match parse yaml "" "body" with
    | Error e -> Assert.True(false, $"Expected Ok: {e}")
    | Ok cfg  -> Assert.Equal(UntrackedReposBehavior.Skip, cfg.DependsOn.[0].UntrackedRepos)

[<Fact>]
let ``parse parses multiple dependsOn entries`` () =
    let yaml =
        baseYamlForDeps +
        "dependsOn:\n" +
        "  - job: ./a.yml\n" +
        "    condition: pr_merged\n" +
        "  - job: ./b.yml\n" +
        "    condition: issue_closed\n"
    match parse yaml "" "body" with
    | Error e -> Assert.True(false, $"Expected Ok: {e}")
    | Ok cfg  ->
        Assert.Equal(2, cfg.DependsOn.Length)
        Assert.Equal("./a.yml", cfg.DependsOn.[0].Job)
        Assert.Equal("./b.yml", cfg.DependsOn.[1].Job)

[<Fact>]
let ``parse returns error for unknown dependsOn condition`` () =
    let yaml =
        baseYamlForDeps +
        "dependsOn:\n" +
        "  - job: ./up.yml\n" +
        "    condition: unknown_condition\n"
    Assert.True(Result.isError (parse yaml "" "body"))

[<Fact>]
let ``parse returns error for unknown dependsOn scope`` () =
    let yaml =
        baseYamlForDeps +
        "dependsOn:\n" +
        "  - job: ./up.yml\n" +
        "    condition: pr_merged\n" +
        "    scope: unknown_scope\n"
    Assert.True(Result.isError (parse yaml "" "body"))

[<Fact>]
let ``parse returns error for dependsOn entry missing job field`` () =
    let yaml =
        baseYamlForDeps +
        "dependsOn:\n" +
        "  - condition: pr_merged\n"
    Assert.True(Result.isError (parse yaml "" "body"))

// ---------------------------------------------------------------------------
// action: field parsing
// ---------------------------------------------------------------------------

let private actionBaseYaml =
    "job:\n  title: \"T\"\n  org: \"o\"\n" +
    "repos:\n  - \"r\"\n" +
    "issue:\n  template: \"./t.md\"\n"

[<Fact>]
let ``parse returns error when assign block is present`` () =
    let yaml = actionBaseYaml + "assign:\n  to: \"@copilot\"\n"
    let result = parse yaml "" "body"
    Assert.True(Result.isError result)
    match result with
    | Error msg -> Assert.Contains("assign", msg)
    | Ok _ -> ()

[<Fact>]
let ``parse parses action type assign-copilot with comment`` () =
    let yaml = actionBaseYaml + "action:\n  type: assign-copilot\n  comment: \"hello\"\n"
    match parse yaml "" "body" with
    | Error e -> Assert.True(false, $"Expected Ok: {e}")
    | Ok cfg  -> Assert.Equal(AssignCopilot (Some "hello"), cfg.Action)

[<Fact>]
let ``parse parses action type assign`` () =
    let yaml = actionBaseYaml + "action:\n  type: assign\n  to: \"@alice\"\n"
    match parse yaml "" "body" with
    | Error e -> Assert.True(false, $"Expected Ok: {e}")
    | Ok cfg  -> Assert.Equal(Assign("@alice", None), cfg.Action)

[<Fact>]
let ``parse parses action type comment`` () =
    let yaml = actionBaseYaml + "action:\n  type: comment\n  comment: \"done\"\n"
    match parse yaml "" "body" with
    | Error e -> Assert.True(false, $"Expected Ok: {e}")
    | Ok cfg  -> Assert.Equal(Comment "done", cfg.Action)

[<Fact>]
let ``parse parses action type noop`` () =
    let yaml = actionBaseYaml + "action:\n  type: noop\n"
    match parse yaml "" "body" with
    | Error e -> Assert.True(false, $"Expected Ok: {e}")
    | Ok cfg  -> Assert.Equal(Noop, cfg.Action)

[<Fact>]
let ``parse parses action type cmd with execute`` () =
    let yaml = actionBaseYaml + "action:\n  type: cmd\n  execute: \"./run.sh\"\n"
    match parse yaml "" "body" with
    | Error e -> Assert.True(false, $"Expected Ok: {e}")
    | Ok cfg  -> Assert.Equal(Cmd(Script "./run.sh", [], None), cfg.Action)

[<Fact>]
let ``parse parses action type cmd with run`` () =
    let yaml = actionBaseYaml + "action:\n  type: cmd\n  run: \"echo hello\"\n"
    match parse yaml "" "body" with
    | Error e -> Assert.True(false, $"Expected Ok: {e}")
    | Ok cfg  -> Assert.Equal(Cmd(Inline "echo hello", [], None), cfg.Action)

[<Fact>]
let ``parse returns error for cmd with both execute and run`` () =
    let yaml = actionBaseYaml + "action:\n  type: cmd\n  execute: \"./s.sh\"\n  run: \"echo hi\"\n"
    Assert.True(Result.isError (parse yaml "" "body"))

[<Fact>]
let ``parse returns error for cmd with neither execute nor run`` () =
    let yaml = actionBaseYaml + "action:\n  type: cmd\n"
    Assert.True(Result.isError (parse yaml "" "body"))

[<Fact>]
let ``parse returns error for assign without to`` () =
    let yaml = actionBaseYaml + "action:\n  type: assign\n"
    Assert.True(Result.isError (parse yaml "" "body"))

[<Fact>]
let ``parse returns error for unknown action type`` () =
    let yaml = actionBaseYaml + "action:\n  type: foobar\n"
    Assert.True(Result.isError (parse yaml "" "body"))

// ---------------------------------------------------------------------------
// hashBytes — pure
// ---------------------------------------------------------------------------

[<Fact>]
let ``hashBytes returns a 64-char lowercase hex string`` () =
    let result = hashBytes (Encoding.UTF8.GetBytes("hello world"))
    Assert.Equal(64, result.Length)
    Assert.True(result |> Seq.forall (fun c -> (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')),
                "Expected lowercase hex characters only")

[<Fact>]
let ``hashBytes returns same hash for identical bytes`` () =
    let bytes = Encoding.UTF8.GetBytes("deterministic")
    Assert.Equal(hashBytes bytes, hashBytes bytes)

[<Fact>]
let ``hashBytes returns different hash for different bytes`` () =
    Assert.NotEqual<string>(
        hashBytes (Encoding.UTF8.GetBytes("foo")),
        hashBytes (Encoding.UTF8.GetBytes("bar")))

[<Fact>]
let ``hashBytes known SHA-256 value`` () =
    Assert.Equal("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", hashBytes [||])
