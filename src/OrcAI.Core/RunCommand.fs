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
      OnClosedIssue      : ClosedIssueAction option
      /// When true, perform no GitHub mutations and do not write the lock file.
      /// Read-only lookups still run so the preview reflects current state.
      DryRun             : bool }

/// Whether an issue was freshly created or already existed in GitHub.
type IssueOutcome =
    | Created
    | AlreadyExisted
    | Reopened
    | Skipped
    | Updated
    | UpdateFailed of string
    /// Repo was archived (read-only) and skipped before any write attempt.
    | SkippedArchived
    /// Lock pointed to a deleted/transferred issue; a fresh issue was created in its place.
    | StaleIssueRecreated
    /// Dry run: a new issue would have been created.
    | DryRunWouldCreate
    /// Dry run: an existing closed issue would have been reopened.
    | DryRunWouldReopen
    /// Dry run: an existing open issue's body would have been refreshed.
    | DryRunWouldUpdate

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

/// True if the error string from `gh label create` indicates the label already exists.
/// `gh label list` can miss labels on first-page-only listings, so treat this as success.
let isLabelAlreadyExists (e: string) =
    e.Contains("already exists", StringComparison.OrdinalIgnoreCase)
    || e.Contains("already been taken", StringComparison.OrdinalIgnoreCase)

/// True if the error string from `gh issue edit` indicates the issue no longer exists
/// (deleted, transferred, or never existed). Lock entries that hit this are recoverable
/// by recreating the issue in the same repo.
let isStaleIssue (e: string) =
    e.Contains("Could not resolve to an issue", StringComparison.OrdinalIgnoreCase)

/// Placeholder IssueRef for a repo that was skipped because it is archived.
/// Carries no real issue number — number is 0 and URL points to the repo home.
let private archivedPlaceholder (repo: RepoName) : IssueRef =
    let (RepoName r) = repo
    { Repo = repo; Number = IssueNumber 0; Url = $"https://github.com/{r}"; Assignees = [] }

/// Placeholder IssueRef for a repo where a dry run would have created a new issue.
/// Number is 0 and URL points to a non-existent /issues/0 path.
let private dryRunCreatePlaceholder (repo: RepoName) : IssueRef =
    let (RepoName r) = repo
    { Repo = repo; Number = IssueNumber 0; Url = $"https://github.com/{r}/issues/0"; Assignees = [] }

/// Resolved per-run parameters shared by `processRepo` (used by both `runFull` and
/// the stale-issue recovery path inside `refreshBodies`).
type private ProcessParams =
    { Config            : JobConfig
      Project           : ProjectInfo
      Verbose           : bool
      AutoCreateLabels  : bool
      SkipCopilot       : bool
      IsPrimaryAuthApp  : bool
      ClosedIssueAction : ClosedIssueAction
      AssignTo          : string
      AssignVia         : string
      AssignComment     : string option
      JobOwner          : string option
      DryRun            : bool }

let private resolveProcessParams
    (deps    : OrcAIDeps)
    (input   : RunInput)
    (config  : JobConfig)
    (project : ProjectInfo)
    : ProcessParams =
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
            let dir =
                System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(input.YamlPath))
                |> Option.ofObj
                |> Option.defaultValue "."
            Codeowners.tryReadLocal deps.FileSystem dir)
    { Config            = config
      Project           = project
      Verbose           = input.Verbose
      AutoCreateLabels  = input.AutoCreateLabels
      SkipCopilot       = skipCopilot
      IsPrimaryAuthApp  = input.IsPrimaryAuthApp
      ClosedIssueAction = closedIssueAction
      AssignTo          = assignTo
      AssignVia         = assignVia
      AssignComment     = assignComment
      JobOwner          = jobOwner
      DryRun            = input.DryRun }

/// Ensure every requested label exists in the repo, creating any that are missing.
/// Returns Ok () when all labels are present or successfully created.
/// When dryRun is true, lists missing labels (and logs them when verbose) but does not create them.
let private ensureLabelsExist
    (client  : IGhClient)
    (repo    : RepoName)
    (labels  : string list)
    (verbose : bool)
    (dryRun  : bool)
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
                elif dryRun then
                    if verbose then
                        for label in missing do
                            eprintfn "[%s] Would create label '%s' (dry run)" repoStr label
                    return Ok ()
                else
                    let mutable lastError : string option = None
                    for label in missing do
                        if verbose then eprintfn "[%s] Creating label '%s'" repoStr label
                        match! client.CreateLabel repo label with
                        | Ok ()   -> ()
                        | Error e when isLabelAlreadyExists e ->
                            if verbose then eprintfn "[%s] Label '%s' already exists — treating as success." repoStr label
                        | Error e -> lastError <- Some $"Could not create label '{label}' in {repoStr}: {e}"
                    match lastError with
                    | Some e -> return Error e
                    | None   -> return Ok ()
    }

/// Process a single repo: find/create issue, add to project, trigger assignee.
/// Returns Some RepoResult on success (with outcome Created or AlreadyExisted),
/// None on any error (error is printed to stderr).
let private processRepo
    (deps : OrcAIDeps)
    (p    : ProcessParams)
    (repo : RepoName)
    : Async<RepoResult option> =
    async {
        let client = deps.GhClient
        let (RepoName repoStr) = repo
        let config            = p.Config
        let project           = p.Project
        let verbose           = p.Verbose
        let autoCreateLabels  = p.AutoCreateLabels
        let skipCopilot       = p.SkipCopilot
        let isPrimaryAuthApp  = p.IsPrimaryAuthApp
        let closedIssueAction = p.ClosedIssueAction
        let assignTo          = p.AssignTo
        let assignVia         = p.AssignVia
        let assignComment     = p.AssignComment
        let jobOwner          = p.JobOwner
        let dryRun            = p.DryRun

        // -1. Pre-check: skip repos that are archived (read-only). Errors from the
        //     pre-check itself are non-fatal — fall through and let the write fail.
        let! archivedResult = client.IsArchived repo
        match archivedResult with
        | Ok true ->
            eprintfn "[%s] Repo is archived — skipping." repoStr
            return Some { Issue = archivedPlaceholder repo; Outcome = SkippedArchived }
        | Ok false
        | Error _ ->

        match archivedResult with
        | Error e when verbose ->
            eprintfn "[%s] Could not check archived state: %s — proceeding." repoStr e
        | _ -> ()

        // 0. Auto-create missing labels if requested
        if autoCreateLabels && not (List.isEmpty config.Labels) then
            match! ensureLabelsExist client repo config.Labels verbose dryRun with
            | Error e -> eprintfn "[%s] Warning: could not ensure labels exist: %s" repoStr e
            | Ok ()   -> ()

        // 1. Find or create issue — track whether it was newly created.
        //    Lookup errors (transient gh failures, exhausted retries) abort the repo
        //    instead of falling through to CreateIssue, which would create a duplicate.
        let! issueResult =
            async {
                match! client.FindIssue repo config.IssueTitle with
                | Error e -> return Error $"Failed to check for existing open issue: {e}"
                | Ok (Some issue) ->
                    if verbose then eprintfn "[%s] Issue already exists: %s" repoStr issue.Url
                    return Ok (issue, AlreadyExisted)
                | Ok None ->
                    match! client.FindClosedIssue repo config.IssueTitle with
                    | Error e -> return Error $"Failed to check for existing closed issue: {e}"
                    | Ok None ->
                        if dryRun then
                            if verbose then eprintfn "[%s] Would create issue '%s' (dry run)" repoStr config.IssueTitle
                            return Ok (dryRunCreatePlaceholder repo, DryRunWouldCreate)
                        else
                            if verbose then eprintfn "[%s] Creating issue '%s'" repoStr config.IssueTitle
                            let! result = client.CreateIssue repo config.IssueTitle config.IssueBody config.Labels
                            return result |> Result.map (fun issue -> (issue, Created))
                    | Ok (Some closed) ->
                        match closedIssueAction with
                        | Create ->
                            if dryRun then
                                if verbose then eprintfn "[%s] Would create issue '%s' (dry run)" repoStr config.IssueTitle
                                return Ok (dryRunCreatePlaceholder repo, DryRunWouldCreate)
                            else
                                if verbose then eprintfn "[%s] Creating issue '%s'" repoStr config.IssueTitle
                                let! result = client.CreateIssue repo config.IssueTitle config.IssueBody config.Labels
                                return result |> Result.map (fun issue -> (issue, Created))
                        | Reopen ->
                            if dryRun then
                                if verbose then eprintfn "[%s] Would reopen closed issue: %s (dry run)" repoStr closed.Url
                                return Ok (closed, DryRunWouldReopen)
                            else
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

        // Dry-run short-circuit: no further mutations beyond this point.
        if dryRun then
            if verbose then
                match outcome with
                | DryRunWouldCreate -> eprintfn "[%s] Would add issue to project and assign %s (dry run)" repoStr assignTo
                | DryRunWouldReopen -> eprintfn "[%s] Would refresh project membership and assignment (dry run)" repoStr
                | _                 -> ()
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
                            do! Comments.postTemplatedComment client repo issue.Number assignTo jobOwner tmpl verbose "trigger" Map.empty
                        | None -> ()

                    // Assign when via includes "assign" and not already assigned.
                    // @copilot specifically requires a PAT (user-level token) to assign;
                    // all other assignees work with GitHub App auth directly.
                    let shouldAssign =
                        (assignVia = "assign" || assignVia = "comment-and-assign")
                        && not (hasAssignee assignTo issue)
                    if shouldAssign then
                        let isCopilot = assignTo.TrimStart('@').Equals("copilot", StringComparison.OrdinalIgnoreCase)
                        let assignClient =
                            if isCopilot then deps.CopilotClient |> Option.defaultValue client
                            else client
                        if isCopilot && deps.CopilotClient.IsNone && isPrimaryAuthApp then
                            eprintfn "[%s] Warning: assigning @copilot requires a PAT. Set ORCAI_PAT or add a 'pat' profile to auth.json." repoStr
                            return issue
                        else
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

    // 1. Find or create the GitHub Project (must complete before per-repo work).
    //    In dry-run mode, never call CreateProject — synthesise a placeholder so the
    //    rest of the pipeline can still report what would happen.
    let projectResult =
        async {
            let (OrgName orgStr) = config.Org
            match! deps.GhClient.FindProject config.Org config.ProjectTitle with
            | Some p -> return Ok p
            | None when input.DryRun ->
                eprintfn "Project '%s' not found in '%s' — would create (dry run)." config.ProjectTitle orgStr
                let placeholder : ProjectInfo =
                    { Org    = config.Org
                      Number = 0
                      Title  = config.ProjectTitle
                      Url    = $"https://github.com/orgs/{orgStr}/projects/0" }
                return Ok placeholder
            | None ->
                eprintfn "Project '%s' not found in '%s', creating..." config.ProjectTitle orgStr
                return! deps.GhClient.CreateProject config.Org config.ProjectTitle
        }
        |> Async.RunSynchronously

    match projectResult with
    | Error e -> Error $"Project error: {e}"
    | Ok project ->

    // 2. Process all repos in parallel
    let processParams = resolveProcessParams deps input config project
    let repoResults =
        config.Repos
        |> List.map (processRepo deps processParams)
        |> Async.Parallel
        |> Async.RunSynchronously

    let allResults = repoResults |> Array.choose id |> Array.toList
    let archivedRepos =
        allResults
        |> List.filter (fun r -> r.Outcome = SkippedArchived)
        |> List.map (fun r -> r.Issue.Repo)
    let successes = allResults |> List.filter (fun r -> r.Outcome <> SkippedArchived)
    let failures  = repoResults |> Array.filter Option.isNone |> Array.length

    let lock : LockFile =
        { LockedAt     = DateTimeOffset.UtcNow
          YamlHash     = yamlHash
          TemplateHash = templateHash
          Project      = project
          Repos        = config.Repos
          Issues       = successes |> List.map (fun r -> r.Issue)
          PullRequests = []
          SkippedRepos = archivedRepos }

    // Only write the lock file if every repo succeeded (and not a dry run)
    if failures = 0 && not input.DryRun then
        LockFile.write deps.FileSystem input.YamlPath lock

    Ok { Lock = lock; Results = allResults; Source = FullRun }

/// For each `StaleIssueRecreated` entry in `results`, run `processRepo` against the
/// same repo so a fresh issue is created in place of the stale one. Returns a new
/// results list with the stale entries replaced by the recreate outcome (Created,
/// AlreadyExisted, etc.) and a Map from old IssueRef → new IssueRef so the caller
/// can rewrite the lock.
let private recreateStaleIssues
    (deps    : OrcAIDeps)
    (params' : ProcessParams)
    (results : RepoResult list)
    : RepoResult list * Map<RepoName, IssueRef> =
    let staleRepos =
        results
        |> List.filter (fun r -> r.Outcome = StaleIssueRecreated)
        |> List.map (fun r -> r.Issue.Repo)
    if List.isEmpty staleRepos then
        results, Map.empty
    else
        let recreated =
            staleRepos
            |> List.map (processRepo deps params')
            |> Async.Parallel
            |> Async.RunSynchronously
            |> Array.toList
        let recreatedByRepo =
            List.zip staleRepos recreated
            |> List.choose (fun (repo, opt) -> opt |> Option.map (fun r -> repo, r))
            |> Map.ofList
        let newRefByRepo =
            recreatedByRepo |> Map.map (fun _ r -> r.Issue)
        let finalResults =
            results |> List.map (fun r ->
                if r.Outcome <> StaleIssueRecreated then r
                else
                    match Map.tryFind r.Issue.Repo recreatedByRepo with
                    | Some recreated ->
                        // Surface the recreate outcome as StaleIssueRecreated so callers/UI
                        // can show it distinctly, but carry the *new* IssueRef.
                        { Issue = recreated.Issue; Outcome = StaleIssueRecreated }
                    | None ->
                        // processRepo returned None → recreate failed (already logged).
                        { r with Outcome = UpdateFailed "stale issue recreate failed" })
        finalResults, newRefByRepo

/// Refresh issue bodies for repos that `runFull` confirmed are open or just reopened.
/// Called after `runFull` whenever the MD template may have changed (the lock's template
/// hash differs from the current one, or `--skip-lock` was set so no prior hash exists).
/// Skips `Created`, `Skipped`, `SkippedArchived`, and any failure outcomes; `runFull`
/// already handles `onClosedIssue` so the closed-issue case never reaches this step.
/// Stale errors during the refresh (issue deleted/transferred between `runFull` and the
/// body write) are recovered via `recreateStaleIssues`.
let private refreshBodies
    (deps       : OrcAIDeps)
    (input      : RunInput)
    (config     : JobConfig)
    (fullResult : RunResult)
    : Result<RunResult, string> =
    let toRefresh =
        fullResult.Results
        |> List.filter (fun r -> r.Outcome = AlreadyExisted || r.Outcome = Reopened)
    if List.isEmpty toRefresh then
        Ok fullResult
    else
        let refreshed =
            toRefresh
            |> List.map (fun r ->
                async {
                    if input.DryRun then
                        let (RepoName repoStr) = r.Issue.Repo
                        if input.Verbose then eprintfn "[%s] Would refresh issue body (dry run)" repoStr
                        return { Issue = r.Issue; Outcome = DryRunWouldUpdate }
                    else
                        let (RepoName repoStr) = r.Issue.Repo
                        let (IssueNumber issueN) = r.Issue.Number
                        match! deps.GhClient.UpdateIssue r.Issue.Repo r.Issue.Number config.IssueTitle config.IssueBody with
                        | Ok () ->
                            let newOutcome =
                                match r.Outcome with
                                | Reopened -> Reopened   // keep distinction in summary
                                | _        -> Updated
                            return { Issue = r.Issue; Outcome = newOutcome }
                        | Error e when isStaleIssue e ->
                            eprintfn "[%s] Stale issue #%d during body refresh — will recreate." repoStr issueN
                            return { Issue = r.Issue; Outcome = StaleIssueRecreated }
                        | Error e ->
                            eprintfn "[%s] Error refreshing issue body: %s" repoStr e
                            return { Issue = r.Issue; Outcome = UpdateFailed e }
                })
            |> Async.Parallel
            |> Async.RunSynchronously
            |> Array.toList
        let hasStale = refreshed |> List.exists (fun r -> r.Outcome = StaleIssueRecreated)
        let recoveredRefreshed, newRefByRepo =
            if not hasStale then
                refreshed, Map.empty
            else
                let processParams = resolveProcessParams deps input config fullResult.Lock.Project
                recreateStaleIssues deps processParams refreshed
        let refreshedByRepo =
            recoveredRefreshed |> List.map (fun r -> r.Issue.Repo, r) |> Map.ofList
        let finalResults =
            fullResult.Results
            |> List.map (fun r ->
                refreshedByRepo
                |> Map.tryFind r.Issue.Repo
                |> Option.defaultValue r)
        let finalIssues =
            fullResult.Lock.Issues
            |> List.map (fun i ->
                match Map.tryFind i.Repo newRefByRepo with
                | Some newRef -> newRef
                | None        -> i)
        let finalLock = { fullResult.Lock with Issues = finalIssues }
        if not (Map.isEmpty newRefByRepo) && not input.DryRun then
            LockFile.write deps.FileSystem input.YamlPath finalLock
        Ok { fullResult with Lock = finalLock; Results = finalResults }

/// Execute the run command for a single YAML path.
/// Returns a RunResult on success, or an error string.
///
/// Dispatch logic:
///   --skip-lock          → runFull, then refreshBodies (unconditional — no prior hash)
///   Both hashes match    → fast path, zero network calls (AlreadyExisted for all)
///   Lock + anything diff → runFull, then refreshBodies iff template hash changed
///   No lock file         → runFull (creates everything fresh)
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
        match runFull deps input mergedConfig yamlHash templateHash with
        | Error e       -> Error e
        | Ok fullResult -> refreshBodies deps input mergedConfig fullResult
    else

    match LockFile.tryRead deps.FileSystem input.YamlPath with
    | Some lock when lock.YamlHash = yamlHash && lock.TemplateHash = templateHash ->
        if input.Verbose then eprintfn "Lock file found and hashes match — nothing to do."
        let results = lock.Issues |> List.map (fun i -> { Issue = i; Outcome = AlreadyExisted })
        Ok { Lock = lock; Results = results; Source = FromLockFile }

    | Some lock ->
        if input.Verbose then eprintfn "Lock file found but hashes changed — re-running."
        match runFull deps input mergedConfig yamlHash templateHash with
        | Error e -> Error e
        | Ok fullResult when lock.TemplateHash = templateHash ->
            // YAML changed but template didn't — no body refresh needed.
            Ok fullResult
        | Ok fullResult ->
            refreshBodies deps input mergedConfig fullResult

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
