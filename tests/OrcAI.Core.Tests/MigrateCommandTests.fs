module OrcAI.Core.Tests.MigrateCommandTests

open Xunit
open Testably.Abstractions.Testing
open OrcAI.Core.Domain
open OrcAI.Core.MigrateCommand
open OrcAI.Core.Tests.TestData

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

let private run (fs: MockFileSystem) (yamlPath: string) (dryRun: bool) =
    execute fs { YamlPath = yamlPath; DryRun = dryRun }

/// Re-parse the (migrated) YAML with the real production parser and return
/// its Action — the strongest possible assertion that migrate's output is
/// actually valid, current-schema YAML, not just "some text changed".
let private actionOf (fs: MockFileSystem) (yamlPath: string) : ActionConfig =
    match OrcAI.Core.YamlConfig.parseFile fs yamlPath with
    | Error e      -> failwith $"Expected migrated YAML to parse, got: {e}"
    | Ok jobConfig -> jobConfig.Action

let private onClosedIssueOf (fs: MockFileSystem) (yamlPath: string) : ClosedIssueAction =
    match OrcAI.Core.YamlConfig.parseFile fs yamlPath with
    | Error e      -> failwith $"Expected migrated YAML to parse, got: {e}"
    | Ok jobConfig -> jobConfig.OnClosedIssue

let private baseJob = "job:\n  title: \"Add AGENTS.md\"\n  org: \"myorg\"\n"
let private baseRepos = "repos:\n  - \"repo-a\"\n"
let private baseIssue = "issue:\n  template: \"TEMPLATE_PLACEHOLDER\"\n  labels: []\n"

// ---------------------------------------------------------------------------
// YAML: assign.via -> action
// ---------------------------------------------------------------------------

[<Fact>]
let ``migrate maps an absent assign block to action: assign-copilot`` () =
    let fs   = MockFileSystem()
    let path = Given.yamlFile fs (baseJob + baseRepos + baseIssue) "# body"
    let result = run fs path false |> Result.map (fun r -> r.Yaml) |> function Ok r -> r | Error e -> failwith e
    Assert.True(result.Changed)
    Assert.Equal(Some 1, result.FromVersion)
    Assert.Equal(Some 2, result.ToVersion)
    Assert.Equal(AssignCopilot None, actionOf fs path)

[<Fact>]
let ``migrate maps assign.via=assign with default to (copilot handle) to action: assign-copilot`` () =
    let fs = MockFileSystem()
    let yaml = baseJob + baseRepos + baseIssue + "assign:\n  via: \"assign\"\n"
    let path = Given.yamlFile fs yaml "# body"
    run fs path false |> ignore
    Assert.Equal(AssignCopilot None, actionOf fs path)

[<Fact>]
let ``migrate treats a bare 'copilot' handle (no leading at-sign) the same as the copilot handle with one`` () =
    let fs = MockFileSystem()
    let yaml = baseJob + baseRepos + baseIssue + "assign:\n  to: \"copilot\"\n"
    let path = Given.yamlFile fs yaml "# body"
    run fs path false |> ignore
    Assert.Equal(AssignCopilot None, actionOf fs path)

[<Fact>]
let ``migrate maps assign.via=assign with a custom to`` () =
    let fs = MockFileSystem()
    let yaml = baseJob + baseRepos + baseIssue + "assign:\n  to: \"@someone\"\n  via: \"assign\"\n"
    let path = Given.yamlFile fs yaml "# body"
    run fs path false |> ignore
    Assert.Equal(Assign("@someone", None), actionOf fs path)

[<Fact>]
let ``migrate drops assign.comment when via=assign — it was already inert under the old runtime`` () =
    let fs = MockFileSystem()
    let yaml = baseJob + baseRepos + baseIssue + "assign:\n  to: \"@someone\"\n  via: \"assign\"\n  comment: \"hello {assignee}\"\n"
    let path = Given.yamlFile fs yaml "# body"
    run fs path false |> ignore
    Assert.Equal(Assign("@someone", None), actionOf fs path)

// ---------------------------------------------------------------------------
// YAML: job.skipCopilot
// ---------------------------------------------------------------------------

[<Fact>]
let ``migrate maps skipCopilot=true (no assign block) to action: noop with no warnings`` () =
    let fs = MockFileSystem()
    let yaml = "job:\n  title: \"Add AGENTS.md\"\n  org: \"myorg\"\n  skipCopilot: true\n" + baseRepos + baseIssue
    let path = Given.yamlFile fs yaml "# body"
    let yamlReport = run fs path false |> function Ok r -> r.Yaml | Error e -> failwith e
    Assert.Equal(Noop, actionOf fs path)
    Assert.Empty(yamlReport.Warnings)

[<Fact>]
let ``migrate maps skipCopilot=true with an assign block to action: noop and warns the block was dropped`` () =
    let fs = MockFileSystem()
    let yaml =
        "job:\n  title: \"Add AGENTS.md\"\n  org: \"myorg\"\n  skipCopilot: true\n" + baseRepos + baseIssue +
        "assign:\n  to: \"@bob\"\n"
    let path = Given.yamlFile fs yaml "# body"
    let yamlReport = run fs path false |> function Ok r -> r.Yaml | Error e -> failwith e
    Assert.Equal(Noop, actionOf fs path)
    Assert.Contains(yamlReport.Warnings, fun (w: string) -> w.Contains("skipCopilot"))

// ---------------------------------------------------------------------------
// YAML: assign.via = comment / comment-and-assign
// ---------------------------------------------------------------------------

[<Fact>]
let ``migrate inlines {assignee} into the comment for via=comment and warns`` () =
    let fs = MockFileSystem()
    let yaml = baseJob + baseRepos + baseIssue + "assign:\n  to: \"@bot\"\n  via: \"comment\"\n  comment: \"cc {assignee} please\"\n"
    let path = Given.yamlFile fs yaml "# body"
    let yamlReport = run fs path false |> function Ok r -> r.Yaml | Error e -> failwith e
    Assert.Equal(Comment "cc @bot please", actionOf fs path)
    Assert.Contains(yamlReport.Warnings, fun (w: string) -> w.Contains("{assignee}"))

[<Fact>]
let ``migrate treats via=comment with no comment template as noop and warns`` () =
    let fs = MockFileSystem()
    let yaml = baseJob + baseRepos + baseIssue + "assign:\n  via: \"comment\"\n"
    let path = Given.yamlFile fs yaml "# body"
    let yamlReport = run fs path false |> function Ok r -> r.Yaml | Error e -> failwith e
    Assert.Equal(Noop, actionOf fs path)
    Assert.NotEmpty(yamlReport.Warnings)

[<Fact>]
let ``migrate keeps comment-and-assign with a comment as-is (no substitution needed)`` () =
    let fs = MockFileSystem()
    let yaml = baseJob + baseRepos + baseIssue + "assign:\n  to: \"@bot\"\n  via: \"comment-and-assign\"\n  comment: \"hi {assignee}\"\n"
    let path = Given.yamlFile fs yaml "# body"
    let yamlReport = run fs path false |> function Ok r -> r.Yaml | Error e -> failwith e
    Assert.Equal(CommentAndAssign("@bot", "hi {assignee}"), actionOf fs path)
    Assert.Empty(yamlReport.Warnings)

[<Fact>]
let ``migrate downgrades comment-and-assign with no comment to a plain assign and warns`` () =
    let fs = MockFileSystem()
    let yaml = baseJob + baseRepos + baseIssue + "assign:\n  to: \"@bot\"\n  via: \"comment-and-assign\"\n"
    let path = Given.yamlFile fs yaml "# body"
    let yamlReport = run fs path false |> function Ok r -> r.Yaml | Error e -> failwith e
    Assert.Equal(Assign("@bot", None), actionOf fs path)
    Assert.NotEmpty(yamlReport.Warnings)

[<Fact>]
let ``migrate errors on an unrecognised assign.via value`` () =
    let fs = MockFileSystem()
    let yaml = baseJob + baseRepos + baseIssue + "assign:\n  via: \"bogus\"\n"
    let path = Given.yamlFile fs yaml "# body"
    match run fs path false with
    | Ok _    -> Assert.Fail("Expected an error for an unrecognised assign.via value")
    | Error e -> Assert.Contains("Unknown assign.via value", e)

// ---------------------------------------------------------------------------
// YAML: onClosedIssue default preservation
// ---------------------------------------------------------------------------

[<Fact>]
let ``migrate adds onClosedIssue: create when absent, preserving the old default`` () =
    let fs   = MockFileSystem()
    let path = Given.yamlFile fs (baseJob + baseRepos + baseIssue) "# body"
    run fs path false |> ignore
    Assert.Equal(Create, onClosedIssueOf fs path)

[<Fact>]
let ``migrate preserves an explicit onClosedIssue value untouched`` () =
    let fs = MockFileSystem()
    let yaml = "job:\n  title: \"Add AGENTS.md\"\n  org: \"myorg\"\n  onClosedIssue: \"reopen\"\n" + baseRepos + baseIssue
    let path = Given.yamlFile fs yaml "# body"
    run fs path false |> ignore
    Assert.Equal(Reopen, onClosedIssueOf fs path)

// ---------------------------------------------------------------------------
// YAML: already-v2 / conflicting files
// ---------------------------------------------------------------------------

[<Fact>]
let ``migrate stamps version onto an already-v2-shaped file without changing its action, then no-ops on a second run`` () =
    let fs = MockFileSystem()
    let yaml = baseJob + baseRepos + baseIssue + "action:\n  type: noop\n"
    let path = Given.yamlFile fs yaml "# body"

    let first = run fs path false |> function Ok r -> r.Yaml | Error e -> failwith e
    Assert.True(first.Changed)
    Assert.Equal(Noop, actionOf fs path)

    let second = run fs path false |> function Ok r -> r.Yaml | Error e -> failwith e
    Assert.False(second.Changed)

[<Fact>]
let ``migrate errors when both a legacy assign: block and a new action: block are present`` () =
    let fs = MockFileSystem()
    let yaml = baseJob + baseRepos + baseIssue + "assign:\n  to: \"@bob\"\naction:\n  type: noop\n"
    let path = Given.yamlFile fs yaml "# body"
    match run fs path false with
    | Ok _    -> Assert.Fail("Expected an error for a file with both 'assign:' and 'action:'")
    | Error e -> Assert.Contains("both a legacy", e)

// ---------------------------------------------------------------------------
// Lock file
// ---------------------------------------------------------------------------

let private legacyLockJson =
    """{
      "lockedAt":     "2026-03-02T10:00:00+00:00",
      "yamlHash":     "abc123",
      "templateHash": "def456",
      "project":      { "org": "myorg", "number": 1, "title": "P", "url": "https://github.com/orgs/myorg/projects/1" },
      "repos":        ["myorg/repo-a"],
      "issues":       [ { "repo": "myorg/repo-a", "number": 7, "url": "https://github.com/myorg/repo-a/issues/7", "assignees": ["copilot"] } ],
      "pullRequests": [ { "repo": "myorg/repo-a", "number": 3, "url": "https://github.com/myorg/repo-a/pull/3", "closesIssue": 7 } ]
    }"""

[<Fact>]
let ``migrate upgrades a v1 lock file to v2 (string ids, PR state defaulted) and warns`` () =
    let fs   = MockFileSystem()
    let path = Given.yamlFile fs (baseJob + baseRepos + baseIssue) "# body"
    let lockPath = OrcAI.Core.LockFile.lockFilePath path
    fs.File.WriteAllText(lockPath, legacyLockJson)

    let lockReport = run fs path false |> function Ok r -> r.Lock | Error e -> failwith e
    match lockReport with
    | None -> Assert.Fail("Expected a lock report")
    | Some report ->
        Assert.True(report.Changed)
        Assert.Equal(Some 1, report.FromVersion)
        Assert.Equal(Some 2, report.ToVersion)
        Assert.NotEmpty(report.Warnings)

    match OrcAI.Core.LockFile.tryRead fs path with
    | None      -> Assert.Fail("Expected the migrated lock file to be readable")
    | Some lock ->
        Assert.Equal(ProjectId "1", lock.Project.Id)
        let issue = List.head lock.Issues
        Assert.Equal(IssueId "7", issue.Id)
        let pr = List.head lock.PullRequests
        Assert.Equal(IssueId "7", pr.ClosesIssue)
        Assert.Equal("OPEN", pr.State)

[<Fact>]
let ``migrate no-ops on an already-v2 lock file`` () =
    let fs   = MockFileSystem()
    let path = Given.yamlFile fs (baseJob + baseRepos + baseIssue) "# body"
    OrcAI.Core.LockFile.write fs path (A.LockFile.defaults ())
    let lockReport = run fs path false |> function Ok r -> r.Lock | Error e -> failwith e
    match lockReport with
    | None         -> Assert.Fail("Expected a lock report")
    | Some report  -> Assert.False(report.Changed)

[<Fact>]
let ``migrate reports Lock=None when no lock file exists`` () =
    let fs   = MockFileSystem()
    let path = Given.yamlFile fs (baseJob + baseRepos + baseIssue) "# body"
    let result = run fs path false |> function Ok r -> r | Error e -> failwith e
    Assert.True(result.Lock.IsNone)

// ---------------------------------------------------------------------------
// --dryrun / backups / missing file
// ---------------------------------------------------------------------------

[<Fact>]
let ``--dryrun reports the would-be diff but writes nothing`` () =
    let fs   = MockFileSystem()
    let originalYaml = baseJob + baseRepos + baseIssue + "assign:\n  to: \"@someone\"\n"
    let path = Given.yamlFile fs originalYaml "# body"
    let lockPath = OrcAI.Core.LockFile.lockFilePath path
    fs.File.WriteAllText(lockPath, legacyLockJson)
    let originalYamlOnDisk = fs.File.ReadAllText(path)
    let originalLockOnDisk = fs.File.ReadAllText(lockPath)

    let result = run fs path true |> function Ok r -> r | Error e -> failwith e

    Assert.True(result.Yaml.Changed)
    Assert.True(result.Yaml.BackupPath.IsNone)
    Assert.True(result.Lock.Value.Changed)
    Assert.True(result.Lock.Value.BackupPath.IsNone)

    Assert.Equal(originalYamlOnDisk, fs.File.ReadAllText(path))
    Assert.Equal(originalLockOnDisk, fs.File.ReadAllText(lockPath))
    Assert.False(fs.File.Exists(path + ".bak"))
    Assert.False(fs.File.Exists(lockPath + ".bak"))

[<Fact>]
let ``a real migration writes .bak files containing the pre-migration bytes`` () =
    let fs   = MockFileSystem()
    let originalYaml = baseJob + baseRepos + baseIssue + "assign:\n  to: \"@someone\"\n"
    let path = Given.yamlFile fs originalYaml "# body"
    let lockPath = OrcAI.Core.LockFile.lockFilePath path
    fs.File.WriteAllText(lockPath, legacyLockJson)
    let originalYamlOnDisk = fs.File.ReadAllText(path)
    let originalLockOnDisk = fs.File.ReadAllText(lockPath)

    run fs path false |> ignore

    Assert.True(fs.File.Exists(path + ".bak"))
    Assert.Equal(originalYamlOnDisk, fs.File.ReadAllText(path + ".bak"))
    Assert.True(fs.File.Exists(lockPath + ".bak"))
    Assert.Equal(originalLockOnDisk, fs.File.ReadAllText(lockPath + ".bak"))

[<Fact>]
let ``migrate errors when the YAML file does not exist`` () =
    let fs = MockFileSystem()
    match run fs "/work/missing.yml" false with
    | Ok _    -> Assert.Fail("Expected an error for a missing YAML file")
    | Error e -> Assert.Contains("not found", e)
