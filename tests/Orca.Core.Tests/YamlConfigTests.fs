module Orca.Core.Tests.YamlConfigTests

open System.IO
open System.Text
open Xunit
open Orca.Core.YamlConfig
open Orca.Core.Domain

// ---------------------------------------------------------------------------
// Unit tests for YAML config parsing and hash computation.
// ---------------------------------------------------------------------------

[<Fact>]
let ``parseFile returns error for missing file`` () =
    let result = parseFile "/nonexistent/path/job.yml"
    Assert.True(Result.isError result)

/// Write a temp YAML file plus an issue template and return the YAML path.
let private writeTempYaml (yaml: string) (templateContent: string) : string =
    let dir  = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName())
    Directory.CreateDirectory(dir) |> ignore
    let templatePath = Path.Combine(dir, "template.md")
    File.WriteAllText(templatePath, templateContent)
    let resolvedYaml = yaml.Replace("TEMPLATE_PLACEHOLDER", "./template.md")
    let yamlPath = Path.Combine(dir, "job.yml")
    File.WriteAllText(yamlPath, resolvedYaml)
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

[<Fact>]
let ``parseFile parses valid YAML into JobConfig`` () =
    let yamlPath = writeTempYaml validYaml "# Issue body"
    let result   = parseFile yamlPath
    match result with
    | Error e -> Assert.True(false, $"Expected Ok but got Error: {e}")
    | Ok cfg  ->
        Assert.Equal(OrgName "myorg", cfg.Org)
        Assert.Equal("Add AGENTS.md", cfg.ProjectTitle)
        Assert.Equal("Add AGENTS.md", cfg.IssueTitle)
        Assert.Equal(2, cfg.Repos.Length)
        Assert.Contains(RepoName "myorg/repo-a", cfg.Repos)
        Assert.Contains(RepoName "myorg/repo-b", cfg.Repos)
        Assert.Equal("# Issue body", cfg.IssueBody)

[<Fact>]
let ``parseFile prefixes repos with org`` () =
    let yamlPath = writeTempYaml validYaml "body"
    match parseFile yamlPath with
    | Error e -> Assert.True(false, $"Expected Ok but got Error: {e}")
    | Ok cfg  ->
        for repo in cfg.Repos do
            let (RepoName r) = repo
            Assert.StartsWith("myorg/", r)

[<Fact>]
let ``parseFile returns error when template file is missing`` () =
    let dir      = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName())
    Directory.CreateDirectory(dir) |> ignore
    let yamlPath = Path.Combine(dir, "job.yml")
    let content =
        "job:\n" +
        "  title: \"T\"\n" +
        "  org: \"o\"\n" +
        "repos:\n" +
        "  - \"r\"\n" +
        "issue:\n" +
        "  template: \"./missing.md\"\n"
    File.WriteAllText(yamlPath, content)
    let result = parseFile yamlPath
    Assert.True(Result.isError result)

[<Fact>]
let ``parseFile returns error when job section is missing`` () =
    let dir      = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName())
    Directory.CreateDirectory(dir) |> ignore
    let yamlPath = Path.Combine(dir, "job.yml")
    File.WriteAllText(yamlPath, "repos:\n  - r\n")
    let result = parseFile yamlPath
    Assert.True(Result.isError result)

[<Fact>]
let ``parseFile parses labels from YAML into JobConfig`` () =
    let yamlPath = writeTempYaml validYaml "# Issue body"
    match parseFile yamlPath with
    | Error e -> Assert.True(false, $"Expected Ok but got Error: {e}")
    | Ok cfg  ->
        Assert.Equal(1, cfg.Labels.Length)
        Assert.Contains("documentation", cfg.Labels)

[<Fact>]
let ``parseFile sets Labels to empty list when not present in YAML`` () =
    let yaml =
        "job:\n" +
        "  title: \"No Labels\"\n" +
        "  org: \"myorg\"\n" +
        "repos:\n" +
        "  - \"repo-a\"\n" +
        "issue:\n" +
        "  template: \"TEMPLATE_PLACEHOLDER\"\n"
    let yamlPath = writeTempYaml yaml "body"
    match parseFile yamlPath with
    | Error e -> Assert.True(false, $"Expected Ok but got Error: {e}")
    | Ok cfg  -> Assert.Empty(cfg.Labels)

[<Fact>]
let ``computeHash returns consistent hex string for same content`` () =
    let dir      = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName())
    Directory.CreateDirectory(dir) |> ignore
    let yamlPath = Path.Combine(dir, "job.yml")
    File.WriteAllText(yamlPath, "content: hello")
    let hash1 = computeHash yamlPath
    let hash2 = computeHash yamlPath
    Assert.Equal(hash1, hash2)
    Assert.Equal(64, hash1.Length) // SHA-256 hex = 32 bytes = 64 chars

[<Fact>]
let ``computeHash returns different hashes for different content`` () =
    let dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName())
    Directory.CreateDirectory(dir) |> ignore
    let p1 = Path.Combine(dir, "a.yml")
    let p2 = Path.Combine(dir, "b.yml")
    File.WriteAllText(p1, "content: hello")
    File.WriteAllText(p2, "content: world")
    let h1 = computeHash p1
    let h2 = computeHash p2
    Assert.NotEqual<string>(h1, h2)

// ---------------------------------------------------------------------------
// Pure function tests — no file I/O required
// ---------------------------------------------------------------------------

let private validYamlText =
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
    match parse validYamlText "/any/path/issue.md" "Issue body text" with
    | Error e -> Assert.True(false, $"Expected Ok but got Error: {e}")
    | Ok cfg  ->
        Assert.Equal(OrgName "acme", cfg.Org)
        Assert.Equal("My Job", cfg.ProjectTitle)
        Assert.Equal("My Job", cfg.IssueTitle)
        Assert.Equal("Issue body text", cfg.IssueBody)
        Assert.Equal(2, cfg.Repos.Length)
        Assert.Contains(RepoName "acme/svc-a", cfg.Repos)
        Assert.Contains(RepoName "acme/svc-b", cfg.Repos)

[<Fact>]
let ``parse prefixes all repos with org name`` () =
    match parse validYamlText "" "body" with
    | Error e -> Assert.True(false, $"Expected Ok: {e}")
    | Ok cfg  ->
        for (RepoName r) in cfg.Repos do
            Assert.StartsWith("acme/", r)

[<Fact>]
let ``parse returns labels from YAML`` () =
    match parse validYamlText "" "body" with
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
    let yaml = "repos:\n  - r\n"
    Assert.True(Result.isError (parse yaml "" ""))

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
let ``parse sets SkipCopilot false when not present in YAML`` () =
    match parse validYamlText "/any/path/issue.md" "body" with
    | Error e -> Assert.True(false, $"Expected Ok but got Error: {e}")
    | Ok cfg  -> Assert.False(cfg.SkipCopilot)

[<Fact>]
let ``parse sets SkipCopilot true when job.skipCopilot is true`` () =
    let yaml =
        "job:\n" +
        "  title: \"My Job\"\n" +
        "  org: \"acme\"\n" +
        "  skipCopilot: true\n" +
        "repos:\n" +
        "  - \"svc-a\"\n" +
        "issue:\n" +
        "  template: \"./issue.md\"\n"
    match parse yaml "/any/path/issue.md" "body" with
    | Error e -> Assert.True(false, $"Expected Ok but got Error: {e}")
    | Ok cfg  -> Assert.True(cfg.SkipCopilot)
    let bytes = Encoding.UTF8.GetBytes("hello world")
    let result = hashBytes bytes
    Assert.Equal(64, result.Length)
    Assert.True(result |> Seq.forall (fun c -> (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')),
                "Expected lowercase hex characters only")

[<Fact>]
let ``hashBytes returns same hash for identical bytes`` () =
    let bytes = Encoding.UTF8.GetBytes("deterministic")
    Assert.Equal(hashBytes bytes, hashBytes bytes)

[<Fact>]
let ``hashBytes returns different hash for different bytes`` () =
    let h1 = hashBytes (Encoding.UTF8.GetBytes("foo"))
    let h2 = hashBytes (Encoding.UTF8.GetBytes("bar"))
    Assert.NotEqual<string>(h1, h2)

[<Fact>]
let ``hashBytes known SHA-256 value`` () =
    // echo -n "" | sha256sum  => e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855
    let result = hashBytes [||]
    Assert.Equal("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", result)
