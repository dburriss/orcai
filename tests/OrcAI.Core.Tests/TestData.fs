module OrcAI.Core.Tests.TestData

open System
open Testably.Abstractions.Testing
open OrcAI.Core.Domain
open OrcAI.Core.Provider
open OrcAI.Core.Deps
open OrcAI.Core.OrcAIConfig

/// Builders for test domain types. Use `defaults ()` for a sensible base value,
/// then pipe through the `with*` helpers to express only what the test cares about.
module A =

    module ProjectInfo =
        let defaults () : ProjectInfo =
            { Org   = OrgName "myorg"
              Id    = ProjectId "1"
              Title = "My Project"
              Url   = "https://github.com/orgs/myorg/projects/1" }

        let withNumber n (p: ProjectInfo) = { p with Id = ProjectId (string n) }
        let withTitle  t (p: ProjectInfo) = { p with Title = t }

    module IssueRef =
        let defaults (repo: RepoName) num : IssueRef =
            let (RepoName r) = repo
            { Repo      = repo
              Id        = IssueId (string num)
              Url       = $"https://github.com/{r}/issues/{num}"
              Assignees = [] }

        let withAssignees xs (i: IssueRef) = { i with Assignees = xs }

    module PullRequestRef =
        let defaults (repo: RepoName) prNum issueNum : PullRequestRef =
            let (RepoName r) = repo
            { Repo        = repo
              Number      = PrNumber prNum
              Url         = $"https://github.com/{r}/pull/{prNum}"
              ClosesIssue = IssueId (string issueNum)
              State       = "OPEN" }

        let withState s (pr: PullRequestRef) = { pr with State = s }

    module LockFile =
        let private repoA = RepoName "myorg/repo-a"

        let defaults () : LockFile =
            { LockedAt     = DateTimeOffset(2026, 3, 2, 10, 0, 0, TimeSpan.Zero)
              YamlHash     = "abc123"
              TemplateHash = "def456"
              Project      = ProjectInfo.defaults ()
              Repos        = [ repoA; RepoName "myorg/repo-b" ]
              Issues       = [ IssueRef.defaults repoA 7 |> IssueRef.withAssignees [ "copilot" ] ]
              PullRequests = [ PullRequestRef.defaults repoA 3 7 ]
              SkippedRepos = []
              Failures     = [] }

        let withHash h  (lf: LockFile) = { lf with YamlHash = h }
        let withRepos rs (lf: LockFile) = { lf with Repos = rs }
        let withIssues is (lf: LockFile) = { lf with Issues = is }

    module Yaml =
        let valid =
            "job:\n" +
            "  title: \"Add AGENTS.md\"\n" +
            "  org: \"myorg\"\n" +
            "repos:\n" +
            "  - \"repo-a\"\n" +
            "  - \"repo-b\"\n" +
            "issue:\n" +
            "  template: \"TEMPLATE_PLACEHOLDER\"\n" +
            "  labels: [\"documentation\"]\n"

    module RunInput =
        let defaults () : OrcAI.Core.RunCommand.RunInput =
            { YamlPath         = ""
              Verbose          = false
              AutoCreateLabels = false
              SkipLock         = true
              MaxConcurrency   = 4
              NoParallel       = false
              ContinueOnError  = false
              DefaultLabels    = []
              OnClosedIssue    = None
              DryRun           = false
              CheckoutRoot     = None }

        let withOnClosedIssue a (i: OrcAI.Core.RunCommand.RunInput)       = { i with OnClosedIssue = a }
        let withNoParallel v (i: OrcAI.Core.RunCommand.RunInput)          = { i with NoParallel = v }
        let withContinueOnError v (i: OrcAI.Core.RunCommand.RunInput)     = { i with ContinueOnError = v }
        let withAutoCreateLabels v (i: OrcAI.Core.RunCommand.RunInput)    = { i with AutoCreateLabels = v }
        let withDryRun v (i: OrcAI.Core.RunCommand.RunInput)              = { i with DryRun = v }

    module NudgeInput =
        let withOnClosedPr a (i: OrcAI.Core.NudgeCommand.NudgeInput) = { i with OnClosedPr = a }

    module ValidateInput =
        let defaults path : OrcAI.Core.ValidateCommand.ValidateInput =
            { YamlPath = path; NoParallel = false; MaxConcurrency = 4; ContinueOnError = false; SkipLock = false }

        let withNoParallel v (i: OrcAI.Core.ValidateCommand.ValidateInput) = { i with NoParallel = v }
        let withSkipLock   v (i: OrcAI.Core.ValidateCommand.ValidateInput) = { i with SkipLock = v }

/// Pre-populated state builders for test setup.
module Given =

    /// Write a YAML file and its template to a MockFileSystem; returns the YAML path.
    let yamlFile (fs: MockFileSystem) (yaml: string) (templateContent: string) : string =
        let dir          = "/work"
        fs.Directory.CreateDirectory(dir) |> ignore
        let templatePath = dir + "/template.md"
        fs.File.WriteAllText(templatePath, templateContent)
        let resolvedYaml = yaml.Replace("TEMPLATE_PLACEHOLDER", "./template.md")
        let yamlPath     = dir + "/job.yml"
        fs.File.WriteAllText(yamlPath, resolvedYaml)
        yamlPath

    /// Write a named valid YAML file (with a shared template stub) to a MockFileSystem;
    /// returns the YAML path. Suitable for multi-file execute tests.
    let namedYamlFile (fs: MockFileSystem) (name: string) : string =
        let dir          = "/work"
        fs.Directory.CreateDirectory(dir) |> ignore
        let templatePath = $"{dir}/template.md"
        if not (fs.File.Exists(templatePath)) then
            fs.File.WriteAllText(templatePath, "# body")
        let yaml =
            "job:\n  title: \"Add AGENTS.md\"\n  org: \"myorg\"\n" +
            "repos:\n  - \"repo-a\"\n" +
            "issue:\n  template: \"./template.md\"\n  labels: []\n"
        let yamlPath = $"{dir}/{name}"
        fs.File.WriteAllText(yamlPath, yaml)
        yamlPath

    /// Write a named YAML file with action: noop to a MockFileSystem; returns the YAML path.
    let namedNoopYamlFile (fs: MockFileSystem) (name: string) : string =
        let dir          = "/work"
        fs.Directory.CreateDirectory(dir) |> ignore
        let templatePath = $"{dir}/template.md"
        if not (fs.File.Exists(templatePath)) then
            fs.File.WriteAllText(templatePath, "# body")
        let yaml =
            "job:\n  title: \"Add AGENTS.md\"\n  org: \"myorg\"\n" +
            "repos:\n  - \"repo-a\"\n" +
            "issue:\n  template: \"./template.md\"\n  labels: []\n" +
            "action:\n  type: noop\n"
        let yamlPath = $"{dir}/{name}"
        fs.File.WriteAllText(yamlPath, yaml)
        yamlPath

    let deps (fs: MockFileSystem) (client: IIssueTracker) : OrcAIDeps =
        let providerClients : ProviderClients =
            { Tracker = client
              Prs     = Some (client :?> IPullRequestLinker)
              Repos   = Some (client :?> IRepoInspector) }
        { ResolveProvider = fun _ -> Ok providerClients
          AuthContext     = { new OrcAI.Core.AuthContext.IAuthContext with
                                 member _.GetToken() = async { return Ok "fake-token" } }
          FileSystem      = fs :> System.IO.Abstractions.IFileSystem
          Config          = empty }

    /// Rewrites the resolved ProviderClients (e.g. to set Prs/Repos = None), simulating
    /// a provider that lacks PR-linking or repo-inspection capability.
    let mapProviderClients (f: ProviderClients -> ProviderClients) (deps: OrcAIDeps) : OrcAIDeps =
        { deps with ResolveProvider = fun cfg -> deps.ResolveProvider cfg |> Result.map f }

    /// Fails the test if AuthContext.GetToken() is ever called — used to assert that a
    /// Local-provider job's auth precheck is skipped (see RunCommand.fs / CleanupCommand.fs).
    let withNeverCalledAuth (deps: OrcAIDeps) : OrcAIDeps =
        { deps with
            AuthContext =
                { new OrcAI.Core.AuthContext.IAuthContext with
                      member _.GetToken() = async { return failwith "AuthContext.GetToken should not be called for a Local-provider job" } } }
