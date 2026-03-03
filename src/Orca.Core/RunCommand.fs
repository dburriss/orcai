module Orca.Core.RunCommand

// ---------------------------------------------------------------------------
// Implements the `orca run` command.
//
// For each repository in the job config:
//   1. Creates (or finds) the GitHub Project for the org (idempotent).
//   2. Creates (or finds) an issue in the repository (idempotent).
//   3. Adds the issue to the GitHub Project (idempotent).
//   4. Assigns the issue to @copilot only if copilot is not already assigned.
//
// All GitHub API calls are delegated to the IGhClient abstraction so that
// this module stays pure and testable.
// ---------------------------------------------------------------------------

open System
open Orca.Core.Domain
open Orca.Core.GhClient
open Orca.Core.Deps

/// Input parameters derived from parsed CLI arguments.
type RunInput =
    { YamlPath : string
      Verbose  : bool }

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

/// True if the issue already has copilot assigned (case-insensitive).
let private hasCopilot (issue: IssueRef) =
    issue.Assignees
    |> List.exists (fun a -> a.Equals("copilot", StringComparison.OrdinalIgnoreCase))

/// Process a single repo: find/create issue, add to project, assign copilot.
/// Returns Some IssueRef on success, None on any error (error is printed to stderr).
let private processRepo
    (client  : IGhClient)
    (config  : JobConfig)
    (project : ProjectInfo)
    (verbose : bool)
    (repo    : RepoName)
    : Async<IssueRef option> =
    async {
        let (RepoName repoStr) = repo

        // 1. Find or create issue
        let! issueResult =
            async {
                let! issueOpt = client.FindIssue repo config.IssueTitle
                match issueOpt with
                | Some issue ->
                    if verbose then eprintfn "[%s] Issue already exists: %s" repoStr issue.Url
                    return Ok issue
                | None ->
                    if verbose then eprintfn "[%s] Creating issue '%s'" repoStr config.IssueTitle
                    return! client.CreateIssue repo config.IssueTitle config.IssueBody config.Labels
            }

        match issueResult with
        | Error e ->
            eprintfn "[%s] Error finding/creating issue: %s" repoStr e
            return None
        | Ok issue ->

        // 2. Add to project (idempotent — errors are swallowed in GhClient)
        if verbose then eprintfn "[%s] Adding issue to project" repoStr
        let! _ = client.AddIssueToProject project issue

        // 3. Assign @copilot only if not already assigned
        let! finalIssue =
            if hasCopilot issue then
                if verbose then eprintfn "[%s] Copilot already assigned, skipping" repoStr
                async { return issue }
            else
                async {
                    if verbose then eprintfn "[%s] Assigning @copilot" repoStr
                    match! client.AssignIssue repo issue.Number "@copilot" with
                    | Error e ->
                        eprintfn "[%s] Warning: failed to assign @copilot: %s" repoStr e
                        return issue
                    | Ok () ->
                        // Return issue with copilot added to assignees list
                        return { issue with Assignees = issue.Assignees @ ["copilot"] }
                }

        return Some finalIssue
    }

// ---------------------------------------------------------------------------
// Execute
// ---------------------------------------------------------------------------

/// Perform the full run: find/create project, process all repos, write lock.
/// Only writes the lock file if all repos succeeded.
let private runFull
    (deps     : OrcaDeps)
    (input    : RunInput)
    (config   : JobConfig)
    (yamlHash : string)
    : Result<LockFile, string> =

    let tokenResult =
        deps.AuthContext.GetToken()
        |> Async.RunSynchronously

    match tokenResult with
    | Error e -> Error $"Auth error: {e}"
    | Ok _ ->

    // 1. Find or create the GitHub Project (must complete before per-repo work)
    let projectResult =
        async {
            let (OrgName orgStr) = config.Org
            match! deps.GhClient.FindProject config.Org config.ProjectTitle with
            | Some p -> return Ok p
            | None   ->
                eprintfn "Project '%s' not found in '%s', creating..." config.ProjectTitle orgStr
                return! deps.GhClient.CreateProject config.Org config.ProjectTitle
        }
        |> Async.RunSynchronously

    match projectResult with
    | Error e -> Error $"Project error: {e}"
    | Ok project ->

    // 2. Process all repos in parallel
    let issueResults =
        config.Repos
        |> List.map (processRepo deps.GhClient config project input.Verbose)
        |> Async.Parallel
        |> Async.RunSynchronously

    let successes = issueResults |> Array.choose id |> Array.toList
    let failures  = issueResults |> Array.filter Option.isNone |> Array.length

    let lock : LockFile =
        { LockedAt     = DateTimeOffset.UtcNow
          YamlHash     = yamlHash
          Project      = project
          Repos        = config.Repos
          Issues       = successes
          PullRequests = [] }

    // Only write the lock file if every repo succeeded
    if failures = 0 then
        LockFile.write input.YamlPath lock

    Ok lock

/// Execute the run command.
/// Returns a LockFile snapshot on success, or an error string.
///
/// Fast path: if a lock file exists and its YAML hash matches, returns
/// immediately with zero network calls — everything already ran successfully.
/// If the hash differs (YAML changed), re-runs in full.
/// The lock file is only written when all repos succeed.
let execute (deps: OrcaDeps) (input: RunInput) : Result<LockFile, string> =
    match YamlConfig.parseFile input.YamlPath with
    | Error e -> Error e
    | Ok config ->

    let yamlHash = YamlConfig.computeHash input.YamlPath

    // If a lock file exists and the YAML hash matches, every repo was already
    // processed successfully — return immediately with zero network calls.
    match LockFile.tryRead input.YamlPath with
    | Some lock when lock.YamlHash = yamlHash ->
        if input.Verbose then
            eprintfn "Lock file found and hash matches — nothing to do."
        Ok lock
    | Some _ ->
        if input.Verbose then
            eprintfn "Lock file found but YAML hash has changed — re-running."
        runFull deps input config yamlHash
    | None ->
        runFull deps input config yamlHash
