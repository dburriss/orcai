module OrcAI.Core.InfoCommand

// ---------------------------------------------------------------------------
// Implements the `orcai info` command.
//
// Displays the current state of a job:
//   - By default reads from the lock file if one exists.
//   - With --skip-lock, fetches live state from GitHub.
//   - With --save-lock, writes a new lock file after fetching live state.
// ---------------------------------------------------------------------------

open System
open OrcAI.Core.Domain
open OrcAI.Core.GhClient
open OrcAI.Core.Deps

/// Input parameters derived from parsed CLI arguments.
type InfoInput =
    { YamlPath  : string
      SkipLock  : bool
      SaveLock  : bool }

/// The result returned to the CLI for display.
type InfoResult =
    { Lock   : LockFile
      Source : InfoSource }

and InfoSource = | FromLockFile | FromGitHub

// ---------------------------------------------------------------------------
// Live fetch helpers
// ---------------------------------------------------------------------------

/// Fetch the current state of a job from GitHub, assembling a LockFile snapshot.
let private fetchFromGitHub
    (client       : IGhClient)
    (config       : JobConfig)
    (yamlHash     : string)
    (templateHash : string)
    : Async<Result<LockFile, string>> =
    async {
        let (OrgName orgStr) = config.Org

        // 1. Find the GitHub Project
        match! client.FindProject config.Org config.ProjectTitle with
        | None ->
            return Error $"GitHub Project '{config.ProjectTitle}' not found in org '{orgStr}'."
        | Some project ->

        // 2. For each repo, find the issue and its PRs
        let! issueResults =
            config.Repos
            |> List.map (fun repo ->
                async {
                    let! issueOpt = client.FindIssue repo config.IssueTitle
                    match issueOpt with
                    | None -> return ([], [])
                    | Some issue ->
                        let! prs = client.FindPrsForIssue repo issue.Number
                        return ([issue], prs)
                })
            |> Async.Parallel

        let issues       = issueResults |> Array.toList |> List.collect fst
        let pullRequests = issueResults |> Array.toList |> List.collect snd

        let lock : LockFile =
            { LockedAt     = DateTimeOffset.UtcNow
              YamlHash     = yamlHash
              TemplateHash = templateHash
              Project      = project
              Repos        = config.Repos
              Issues       = issues
              PullRequests = pullRequests
              SkippedRepos = [] }

        return Ok lock
    }

// ---------------------------------------------------------------------------
// Execute
// ---------------------------------------------------------------------------

/// Execute the info command.
/// Returns an InfoResult on success, or an error string.
let execute (deps: OrcAIDeps) (input: InfoInput) : Result<InfoResult, string> =
    // Parse the YAML config first (needed both for lock-file path and for live fetch)
    match YamlConfig.parseFile deps.FileSystem input.YamlPath with
    | Error e -> Error e
    | Ok config ->

    // Try the lock file unless --no-lock was specified
    let lockOpt =
        if input.SkipLock then None
        else LockFile.tryRead deps.FileSystem input.YamlPath

    match lockOpt with
    | Some lock ->
        Ok { Lock = lock; Source = FromLockFile }

    | None ->
        // Fetch live state from GitHub
        let yamlHash     = YamlConfig.computeHash deps.FileSystem input.YamlPath
        let templateHash =
            match YamlConfig.resolveTemplatePath deps.FileSystem input.YamlPath with
            | Some p -> YamlConfig.computeTemplateHash deps.FileSystem p
            | None   -> ""
        let result   =
            fetchFromGitHub deps.GhClient config yamlHash templateHash
            |> Async.RunSynchronously

        match result with
        | Error e -> Error e
        | Ok lock ->
            if input.SaveLock then
                LockFile.write deps.FileSystem input.YamlPath lock
            Ok { Lock = lock; Source = FromGitHub }
