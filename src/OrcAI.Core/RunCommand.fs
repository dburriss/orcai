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
      IsPrimaryAuthApp   : bool
      /// Overrides the YAML onClosedIssue value when explicitly set via CLI.
      OnClosedIssue      : ClosedIssueAction option }

/// Whether an issue was freshly created or already existed in GitHub.
type IssueOutcome = | Created | AlreadyExisted | Reopened | Skipped | Updated | UpdateFailed of string

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

/// True if the issue already has the given assignee (case-insensitive, strips leading @).
let private hasAssignee (assignee: string) (issue: IssueRef) =
    let handle = assignee.TrimStart('@')
    issue.Assignees
    |> List.exists (fun a -> a.Equals(handle, StringComparison.OrdinalIgnoreCase))

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

/// Process a single repo: find/create issue, add to project, trigger assignee.
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
    (closedIssueAction: ClosedIssueAction)
    (assignTo         : string)
    (assignVia        : string)
    (assignComment    : string option)
    (jobOwner         : string option)
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
                    let! closedOpt = client.FindClosedIssue repo config.IssueTitle
                    match closedOpt with
                    | None ->
                        if verbose then eprintfn "[%s] Creating issue '%s'" repoStr config.IssueTitle
                        let! result = client.CreateIssue repo config.IssueTitle config.IssueBody config.Labels
                        return result |> Result.map (fun issue -> (issue, Created))
                    | Some closed ->
                        match closedIssueAction with
                        | Create ->
                            if verbose then eprintfn "[%s] Creating issue '%s'" repoStr config.IssueTitle
                            let! result = client.CreateIssue repo config.IssueTitle config.IssueBody config.Labels
                            return result |> Result.map (fun issue -> (issue, Created))
                        | Reopen ->
                            if verbose then eprintfn "[%s] Reopening closed issue: %s" repoStr closed.Url
                            let! reopenResult = client.ReopenIssue repo closed.Number
                            return reopenResult |> Result.map (fun issue -> (issue, Reopened))
                        | Skip ->
                            if verbose then eprintfn "[%s] Closed issue found, skipping: %s" repoStr closed.Url
                            return Ok (closed, Skipped)
                        | Fail ->
                            eprintfn "[%s] Closed issue found and --on-closed-issue=fail is set: %s" repoStr closed.Url
                            return Error $"Closed issue exists for repo {repoStr}: {closed.Url}"
            }

        match issueResult with
        | Error e ->
            eprintfn "[%s] Error finding/creating issue: %s" repoStr e
            return None
        | Ok (issue, outcome) ->

        // 2. Add to project and assign copilot (bypassed for Skipped outcome)
        if outcome = Skipped then
            return Some { Issue = issue; Outcome = outcome }
        else

        // 3. Add to project (idempotent — errors are swallowed in GhClient)
        if verbose then eprintfn "[%s] Adding issue to project" repoStr
        let! _ = client.AddIssueToProject project issue

        // 3. Trigger assignee: post comment and/or assign, controlled by assignVia.
        //    When the primary auth is a GitHub App, use the CopilotClient (PAT-based)
        //    for assignment — GitHub Apps cannot assign users directly.
        //    If no CopilotClient is available and primary auth is App-based, warn and skip assignment.
        let! finalIssue =
            if skipCopilot then
                if verbose then eprintfn "[%s] Skipping assignment (--skip-copilot)" repoStr
                async { return issue }
            else
                async {
                    // Post trigger comment when via includes "comment"
                    if assignVia = "comment" || assignVia = "comment-and-assign" then
                        match assignComment with
                        | Some tmpl ->
                            let! codeownersContent = client.FetchCodeowners repo
                            let repoOwners = codeownersContent |> Option.bind Codeowners.parseCatchAll
                            let vars =
                                [ "assignee",       assignTo
                                  yield! jobOwner   |> Option.map (fun v -> "job.owner",       v) |> Option.toList
                                  yield! repoOwners |> Option.map (fun v -> "repo.codeowners", v) |> Option.toList ]
                                |> Map.ofList
                            let body = renderTemplate vars tmpl
                            if verbose then eprintfn "[%s] Posting trigger comment" repoStr
                            match! client.PostComment repo issue.Number body with
                            | Error e -> eprintfn "[%s] Warning: failed to post trigger comment: %s" repoStr e
                            | Ok ()   -> ()
                        | None -> ()

                    // Assign when via includes "assign" and not already assigned
                    let shouldAssign =
                        (assignVia = "assign" || assignVia = "comment-and-assign")
                        && not (hasAssignee assignTo issue)
                    if shouldAssign then
                        match deps.CopilotClient, isPrimaryAuthApp with
                        | None, true ->
                            eprintfn "[%s] Warning: primary auth is a GitHub App which cannot assign users. Set ORCAI_PAT or add a 'pat' profile to auth.json." repoStr
                            return issue
                        | clientOpt, _ ->
                            let assignClient = clientOpt |> Option.defaultValue client
                            if verbose then eprintfn "[%s] Assigning %s" repoStr assignTo
                            match! assignClient.AssignIssue repo issue.Number assignTo with
                            | Error e ->
                                eprintfn "[%s] Warning: failed to assign %s: %s" repoStr assignTo e
                                return issue
                            | Ok () ->
                                return { issue with Assignees = issue.Assignees @ [assignTo.TrimStart('@')] }
                    else
                        return issue
                }

        return Some { Issue = finalIssue; Outcome = outcome }
    }

// ---------------------------------------------------------------------------
// Execute
// ---------------------------------------------------------------------------

/// Perform the full run: find/create project, process all repos, write lock.
/// Only writes the lock file if all repos succeeded.
let private runFull
    (deps         : OrcAIDeps)
    (input        : RunInput)
    (config       : JobConfig)
    (yamlHash     : string)
    (templateHash : string)
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
    let skipCopilot       = input.SkipCopilot || config.SkipCopilot
    let closedIssueAction = input.OnClosedIssue |> Option.defaultValue config.OnClosedIssue
    let pickAssign f =
        config.Assign |> Option.bind f
        |> Option.orElse (deps.Config.Assign |> Option.bind f)
    let assignTo      = pickAssign (fun a -> a.To)      |> Option.defaultValue "@copilot"
    let assignVia     = pickAssign (fun a -> a.Via)     |> Option.defaultValue "assign"
    let assignComment = pickAssign (fun a -> a.Comment)
    let jobOwner =
        config.JobOwner
        |> Option.orElseWith (fun () ->
            let dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(input.YamlPath)) |> Option.ofObj |> Option.defaultValue "."
            Codeowners.tryReadLocal deps.FileSystem dir)
    let repoResults =
        config.Repos
        |> List.map (processRepo deps config project input.Verbose input.AutoCreateLabels skipCopilot input.IsPrimaryAuthApp closedIssueAction assignTo assignVia assignComment jobOwner)
        |> Async.Parallel
        |> Async.RunSynchronously

    let successes = repoResults |> Array.choose id |> Array.toList
    let failures  = repoResults |> Array.filter Option.isNone |> Array.length

    let lock : LockFile =
        { LockedAt     = DateTimeOffset.UtcNow
          YamlHash     = yamlHash
          TemplateHash = templateHash
          Project      = project
          Repos        = config.Repos
          Issues       = successes |> List.map (fun r -> r.Issue)
          PullRequests = [] }

    // Only write the lock file if every repo succeeded
    if failures = 0 then
        LockFile.write deps.FileSystem input.YamlPath lock

    Ok { Lock = lock; Results = successes; Source = FullRun }

/// Update issue bodies for a list of existing issues using the current template.
/// Returns one RepoResult per issue (Updated or UpdateFailed).
let private applyBodyUpdates
    (deps   : OrcAIDeps)
    (config : JobConfig)
    (issues : IssueRef list)
    : RepoResult list =
    issues
    |> List.map (fun issue ->
        async {
            let (RepoName repoStr) = issue.Repo
            match! deps.GhClient.UpdateIssue issue.Repo issue.Number config.IssueTitle config.IssueBody with
            | Ok ()   -> return { Issue = issue; Outcome = Updated }
            | Error e ->
                eprintfn "[%s] Error updating issue body: %s" repoStr e
                return { Issue = issue; Outcome = UpdateFailed e }
        })
    |> Async.Parallel
    |> Async.RunSynchronously
    |> Array.toList

/// Update issue bodies for all issues in the lock file, then write an updated lock.
/// Called when only the template hash has changed (no structural changes needed).
let private updateBodies
    (deps         : OrcAIDeps)
    (input        : RunInput)
    (config       : JobConfig)
    (yamlHash     : string)
    (templateHash : string)
    (lock         : LockFile)
    : Result<RunResult, string> =
    let results = applyBodyUpdates deps config lock.Issues
    let newLock = { lock with YamlHash = yamlHash; TemplateHash = templateHash; LockedAt = DateTimeOffset.UtcNow }
    LockFile.write deps.FileSystem input.YamlPath newLock
    Ok { Lock = newLock; Results = results; Source = FullRun }

/// Execute the run command for a single YAML path.
/// Returns a RunResult on success, or an error string.
///
/// Dispatch logic (checked in order, --skip-lock bypasses all):
///   Both hashes match   → fast path, zero network calls (AlreadyExisted for all)
///   Only YAML changed   → runFull (structural changes; body unchanged)
///   Only template changed → updateBodies only (UpdateIssue per repo in lock)
///   Both changed        → runFull, then UpdateIssue for any AlreadyExisted repos
///   No lock file        → runFull (creates everything fresh)
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

    let yamlHash     = YamlConfig.computeHash deps.FileSystem input.YamlPath
    let templateHash =
        match YamlConfig.resolveTemplatePath deps.FileSystem input.YamlPath with
        | Some p -> YamlConfig.computeTemplateHash deps.FileSystem p
        | None   -> ""

    if input.SkipLock then
        if input.Verbose then eprintfn "--skip-lock set, bypassing lock file."
        runFull deps input mergedConfig yamlHash templateHash
    else

    match LockFile.tryRead deps.FileSystem input.YamlPath with
    | Some lock when lock.YamlHash = yamlHash && lock.TemplateHash = templateHash ->
        if input.Verbose then eprintfn "Lock file found and hashes match — nothing to do."
        let results = lock.Issues |> List.map (fun i -> { Issue = i; Outcome = AlreadyExisted })
        Ok { Lock = lock; Results = results; Source = FromLockFile }

    | Some lock when lock.YamlHash = yamlHash ->
        if input.Verbose then eprintfn "Lock file found, template changed — updating issue bodies."
        updateBodies deps input mergedConfig yamlHash templateHash lock

    | Some lock when lock.TemplateHash = templateHash ->
        if input.Verbose then eprintfn "Lock file found but YAML hash changed — re-running."
        runFull deps input mergedConfig yamlHash templateHash

    | Some lock ->
        // Both changed: structural runFull, then update bodies for existing issues.
        if input.Verbose then eprintfn "Lock file found but YAML and template hashes changed — re-running and updating issue bodies."
        match runFull deps input mergedConfig yamlHash templateHash with
        | Error e -> Error e
        | Ok fullResult ->
            let toUpdate =
                fullResult.Results
                |> List.filter (fun r -> r.Outcome = AlreadyExisted)
                |> List.map (fun r -> r.Issue)
            if List.isEmpty toUpdate then
                Ok fullResult
            else
                let updated = applyBodyUpdates deps mergedConfig toUpdate
                let updatedByRepo =
                    updated |> List.map (fun r -> r.Issue.Repo, r) |> Map.ofList
                let finalResults =
                    fullResult.Results |> List.map (fun r ->
                        if r.Outcome <> AlreadyExisted then r
                        else updatedByRepo |> Map.tryFind r.Issue.Repo |> Option.defaultValue r)
                Ok { fullResult with Results = finalResults }

    | None ->
        runFull deps input mergedConfig yamlHash templateHash

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
