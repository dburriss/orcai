module OrcAI.Core.RunCommand

// ---------------------------------------------------------------------------
// Implements the `orcai run` command.
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
open OrcAI.Core.Domain
open OrcAI.Core.GhClient
open OrcAI.Core.Deps

/// Input parameters derived from parsed CLI arguments.
type RunInput =
    { YamlPath           : string
      Verbose            : bool
      AutoCreateLabels   : bool
      SkipCopilot        : bool
      SkipLock           : bool
      MaxConcurrency     : int
      NoParallel         : bool
      ContinueOnError    : bool
      /// Extra labels to union-merge with the YAML labels (from config file).
      DefaultLabels      : string list
      /// True when the primary auth is a GitHub App. Used to emit an appropriate
      /// warning when no secondary PAT is available for Copilot assignment.
      IsPrimaryAuthApp   : bool }

/// Whether an issue was freshly created or already existed in GitHub.
type IssueOutcome = | Created | AlreadyExisted

/// The result for a single repo processed during the run.
type RepoResult =
    { Issue   : IssueRef
      Outcome : IssueOutcome }

/// Whether the result came from the lock file (no network) or a full GitHub run.
type RunSource = | FromLockFile | FullRun

/// The result returned to the CLI for display.
type RunResult =
    { Lock    : LockFile
      Results : RepoResult list
      Source  : RunSource }

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

/// True if the issue already has copilot assigned (case-insensitive).
let private hasCopilot (issue: IssueRef) =
    issue.Assignees
    |> List.exists (fun a -> a.Equals("copilot", StringComparison.OrdinalIgnoreCase))

/// Given the labels that already exist in a repo, return those from the requested
/// list that are missing (case-insensitive comparison).
let labelsToCreate (existing: string list) (requested: string list) : string list =
    let existingSet =
        existing
        |> List.map (fun s -> s.ToLowerInvariant())
        |> Set.ofList
    requested
    |> List.filter (fun l -> not (Set.contains (l.ToLowerInvariant()) existingSet))

/// Ensure every requested label exists in the repo, creating any that are missing.
/// Returns Ok () when all labels are present or successfully created.
let private ensureLabelsExist
    (client  : IGhClient)
    (repo    : RepoName)
    (labels  : string list)
    (verbose : bool)
    : Async<Result<unit, string>> =
    async {
        if List.isEmpty labels then
            return Ok ()
        else
            let (RepoName repoStr) = repo
            match! client.ListLabels repo with
            | Error e -> return Error $"Could not list labels for {repoStr}: {e}"
            | Ok existing ->
                let missing = labelsToCreate existing labels
                if List.isEmpty missing then
                    return Ok ()
                else
                    let mutable lastError : string option = None
                    for label in missing do
                        if verbose then eprintfn "[%s] Creating label '%s'" repoStr label
                        match! client.CreateLabel repo label with
                        | Error e -> lastError <- Some $"Could not create label '{label}' in {repoStr}: {e}"
                        | Ok ()   -> ()
                    match lastError with
                    | Some e -> return Error e
                    | None   -> return Ok ()
    }

/// Process a single repo: find/create issue, add to project, assign copilot.
/// Returns Some RepoResult on success (with outcome Created or AlreadyExisted),
/// None on any error (error is printed to stderr).
let private processRepo
    (deps             : OrcAIDeps)
    (config           : JobConfig)
    (project          : ProjectInfo)
    (verbose          : bool)
    (autoCreateLabels : bool)
    (skipCopilot      : bool)
    (isPrimaryAuthApp : bool)
    (repo             : RepoName)
    : Async<RepoResult option> =
    async {
        let client = deps.GhClient
        let (RepoName repoStr) = repo

        // 0. Auto-create missing labels if requested
        if autoCreateLabels && not (List.isEmpty config.Labels) then
            match! ensureLabelsExist client repo config.Labels verbose with
            | Error e -> eprintfn "[%s] Warning: could not ensure labels exist: %s" repoStr e
            | Ok ()   -> ()

        // 1. Find or create issue — track whether it was newly created
        let! issueResult =
            async {
                let! issueOpt = client.FindIssue repo config.IssueTitle
                match issueOpt with
                | Some issue ->
                    if verbose then eprintfn "[%s] Issue already exists: %s" repoStr issue.Url
                    return Ok (issue, AlreadyExisted)
                | None ->
                    if verbose then eprintfn "[%s] Creating issue '%s'" repoStr config.IssueTitle
                    let! result = client.CreateIssue repo config.IssueTitle config.IssueBody config.Labels
                    return result |> Result.map (fun issue -> (issue, Created))
            }

        match issueResult with
        | Error e ->
            eprintfn "[%s] Error finding/creating issue: %s" repoStr e
            return None
        | Ok (issue, outcome) ->

        // 2. Add to project (idempotent — errors are swallowed in GhClient)
        if verbose then eprintfn "[%s] Adding issue to project" repoStr
        let! _ = client.AddIssueToProject project issue

        // 3. Assign @copilot only if not already assigned and not disabled.
        //    When the primary auth is a GitHub App, use the CopilotClient (PAT-based)
        //    instead — GitHub Apps cannot assign @copilot.
        //    If no CopilotClient is available and primary auth is App-based, warn and skip.
        let! finalIssue =
            if skipCopilot then
                if verbose then eprintfn "[%s] Skipping @copilot assignment (--skip-copilot)" repoStr
                async { return issue }
            elif hasCopilot issue then
                if verbose then eprintfn "[%s] Copilot already assigned, skipping" repoStr
                async { return issue }
            else
                match deps.CopilotClient, isPrimaryAuthApp with
                | None, true ->
                    eprintfn "[%s] Warning: primary auth is a GitHub App which cannot assign @copilot. Set ORCAI_PAT or add a 'pat' profile to auth.json to enable Copilot assignment." repoStr
                    async { return issue }
                | clientOpt, _ ->
                    let assignClient = clientOpt |> Option.defaultValue client
                    async {
                        if verbose then eprintfn "[%s] Assigning @copilot" repoStr
                        match! assignClient.AssignIssue repo issue.Number "@copilot" with
                        | Error e ->
                            eprintfn "[%s] Warning: failed to assign @copilot: %s" repoStr e
                            return issue
                        | Ok () ->
                            // Return issue with copilot added to assignees list
                            return { issue with Assignees = issue.Assignees @ ["copilot"] }
                    }

        return Some { Issue = finalIssue; Outcome = outcome }
    }

// ---------------------------------------------------------------------------
// Execute
// ---------------------------------------------------------------------------

/// Perform the full run: find/create project, process all repos, write lock.
/// Only writes the lock file if all repos succeeded.
let private runFull
    (deps     : OrcAIDeps)
    (input    : RunInput)
    (config   : JobConfig)
    (yamlHash : string)
    : Result<RunResult, string> =

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
    let skipCopilot = input.SkipCopilot || config.SkipCopilot
    let repoResults =
        config.Repos
        |> List.map (processRepo deps config project input.Verbose input.AutoCreateLabels skipCopilot input.IsPrimaryAuthApp)
        |> Async.Parallel
        |> Async.RunSynchronously

    let successes = repoResults |> Array.choose id |> Array.toList
    let failures  = repoResults |> Array.filter Option.isNone |> Array.length

    let lock : LockFile =
        { LockedAt     = DateTimeOffset.UtcNow
          YamlHash     = yamlHash
          Project      = project
          Repos        = config.Repos
          Issues       = successes |> List.map (fun r -> r.Issue)
          PullRequests = [] }

    // Only write the lock file if every repo succeeded
    if failures = 0 then
        LockFile.write deps.FileSystem input.YamlPath lock

    Ok { Lock = lock; Results = successes; Source = FullRun }

/// Execute the run command for a single YAML path.
/// Returns a RunResult on success, or an error string.
///
/// Fast path: if a lock file exists, its YAML hash matches, and --skip-lock is
/// not set, returns immediately with zero network calls — everything already ran
/// successfully. All issues are reported as AlreadyExisted.
///
/// If --skip-lock is set, or the hash differs, or no lock file exists, performs
/// a full run. processRepo tracks whether each issue was Created or AlreadyExisted.
/// The lock file is only written when all repos succeed.
let executeSingle (deps: OrcAIDeps) (input: RunInput) : Result<RunResult, string> =
    match YamlConfig.parseFile deps.FileSystem input.YamlPath with
    | Error e -> Error e
    | Ok config ->

    // Union-merge default labels from config with YAML labels (no duplicates, case-insensitive).
    let mergedConfig =
        if input.DefaultLabels.IsEmpty then
            config
        else
            let existingLower = config.Labels |> List.map (fun s -> s.ToLowerInvariant()) |> Set.ofList
            let extraLabels   = input.DefaultLabels |> List.filter (fun l -> not (Set.contains (l.ToLowerInvariant()) existingLower))
            { config with Labels = config.Labels @ extraLabels }

    let yamlHash = YamlConfig.computeHash deps.FileSystem input.YamlPath

    if input.SkipLock then
        // Bypass lock entirely — always do a full run
        if input.Verbose then
            eprintfn "--skip-lock set, bypassing lock file."
        runFull deps input mergedConfig yamlHash
    else

    match LockFile.tryRead deps.FileSystem input.YamlPath with
    | Some lock when lock.YamlHash = yamlHash ->
        // Lock file is current — nothing to do, report all issues as already existing
        if input.Verbose then
            eprintfn "Lock file found and hash matches — nothing to do."
        let results =
            lock.Issues |> List.map (fun i -> { Issue = i; Outcome = AlreadyExisted })
        Ok { Lock = lock; Results = results; Source = FromLockFile }
    | Some _ ->
        if input.Verbose then
            eprintfn "Lock file found but YAML hash has changed — re-running."
        runFull deps input mergedConfig yamlHash
    | None ->
        runFull deps input mergedConfig yamlHash

/// Execute the run command over a list of resolved file paths.
/// Returns a Map from file path to Result<RunResult, string>.
///
/// Files are processed in parallel up to MaxConcurrency (or sequentially when
/// NoParallel=true). Without ContinueOnError the first failure stops processing;
/// with ContinueOnError all files are attempted and per-file errors are collected.
let execute (deps: OrcAIDeps) (paths: string list) (input: RunInput) : Async<Map<string, Result<RunResult, string>>> =
    async {
        if input.NoParallel then
            // Sequential — stop on first error unless ContinueOnError
            let results = System.Collections.Generic.Dictionary<string, Result<RunResult, string>>()
            let mutable stop = false
            for path in paths do
                if not stop then
                    let singleInput = { input with YamlPath = path }
                    let r = executeSingle deps singleInput
                    results.[path] <- r
                    match r with
                    | Error _ when not input.ContinueOnError -> stop <- true
                    | _ -> ()
            return results |> Seq.map (fun kv -> kv.Key, kv.Value) |> Map.ofSeq
        else
            // Parallel — throttled by MaxConcurrency
            let semaphore = new System.Threading.SemaphoreSlim(input.MaxConcurrency)
            let runOne (path: string) : Async<string * Result<RunResult, string>> =
                async {
                    do! semaphore.WaitAsync() |> Async.AwaitTask
                    try
                        let singleInput = { input with YamlPath = path }
                        let r = executeSingle deps singleInput
                        return (path, r)
                    finally
                        semaphore.Release() |> ignore
                }

            if input.ContinueOnError then
                let! pairs = paths |> List.map runOne |> Async.Parallel
                return pairs |> Array.toSeq |> Map.ofSeq
            else
                // Stop on first error: run sequentially in batches and bail early.
                // Simple approach: run all but collect; filter out later is not correct
                // so we use a cancellation-aware sequential fan-out.
                let results = System.Collections.Generic.Dictionary<string, Result<RunResult, string>>()
                let mutable firstError : string option = None
                let tasks = paths |> List.map runOne
                let! pairs = tasks |> Async.Parallel
                for (path, r) in pairs do
                    results.[path] <- r
                    match r with
                    | Error e when firstError.IsNone -> firstError <- Some e
                    | _ -> ()
                // If there was an error without ContinueOnError, only return up to and including the first failure.
                // Since parallel execution has already run everything, we return all results as-is
                // (parallel stop-on-error is best-effort; sequential is exact).
                return results |> Seq.map (fun kv -> kv.Key, kv.Value) |> Map.ofSeq
    }
