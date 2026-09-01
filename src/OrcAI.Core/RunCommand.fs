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
// All GitHub API calls are delegated to the IIssueTracker/IRepoInspector
// abstractions so that this module stays pure and testable.
// ---------------------------------------------------------------------------

open System
open System.Diagnostics
open System.IO
open System.Text
open OrcAI.Core.Domain
open OrcAI.Core.Provider
open OrcAI.Core.Deps
open OrcAI.Core.FileGlob

/// Input parameters derived from parsed CLI arguments.
type RunInput =
    { YamlPath           : string
      Verbose            : bool
      AutoCreateLabels   : bool
      SkipLock           : bool
      MaxConcurrency     : int
      NoParallel         : bool
      ContinueOnError    : bool
      /// Extra labels to union-merge with the YAML labels (from config file).
      DefaultLabels      : string list
      /// Overrides the YAML onClosedIssue value when explicitly set via CLI.
      OnClosedIssue      : ClosedIssueAction option
      /// When true, perform no GitHub mutations and do not write the lock file.
      /// Read-only lookups still run so the preview reflects current state.
      DryRun             : bool
      /// Root directory for repo checkouts. None → temp dir scoped to the run.
      CheckoutRoot       : string option }

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
    { Lock      : LockFile
      Results   : RepoResult list
      Source    : RunSource
      /// Set when an all_repos dependency gate was not met; no repos were processed.
      BlockedBy : string option }

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

let private shellDispatch (cmd: string) : string * string list =
    if OperatingSystem.IsWindows() then "cmd", ["/C"; cmd]
    else "sh", ["-c"; cmd]

let private resolveExec (render: string -> string) (exec: CmdExec) : string * string list =
    match exec with
    | Shell cmd       -> shellDispatch (render cmd)
    | Exec(cmd, args) -> render cmd, args |> List.map render

/// Build the `ProcessStartInfo` for an `execute:` command, shared by the
/// `cmd`/`cmd-checkout`/`cmd-to-github` action handlers.
/// Stdin is redirected and immediately closed (by the caller) so the child sees
/// an instant EOF instead of inheriting an ambiguous, non-EOF-terminated handle
/// — some CLIs (e.g. `opencode run` with no TTY) block reading stdin to EOF
/// before doing anything, and hang or misbehave on a handle that never closes.
/// `PWD` is explicitly set to `workingDir` because `WorkingDirectory` only
/// changes the OS-level cwd of the child (a `chdir`) — it does not update the
/// inherited `PWD` environment variable. Some CLIs (again, `opencode run` — a
/// Bun/Node program) resolve their own project root from `PWD` rather than the
/// real cwd, so a stale inherited `PWD` silently redirects them to wherever
/// *this* process happened to be launched from instead of `workingDir`.
let internal buildExecPsi (executable: string) (args: string list) (workingDir: string) : Diagnostics.ProcessStartInfo =
    let psi = Diagnostics.ProcessStartInfo(executable)
    psi.WorkingDirectory       <- workingDir
    psi.RedirectStandardOutput <- true
    psi.RedirectStandardError  <- true
    psi.RedirectStandardInput  <- true
    psi.UseShellExecute        <- false
    for arg in args do psi.ArgumentList.Add(arg)
    psi.Environment["PWD"] <- workingDir
    psi

let internal runExecCommand (executable: string) (args: string list) (workingDir: string) : Async<int * string> =
    async {
        let psi = buildExecPsi executable args workingDir
        use proc = Diagnostics.Process.Start(psi)
        proc.StandardInput.Close()
        let stdoutTask = proc.StandardOutput.ReadToEndAsync()
        let stderrTask = proc.StandardError.ReadToEndAsync()
        do! proc.WaitForExitAsync() |> Async.AwaitTask
        let! _   = stdoutTask |> Async.AwaitTask
        let! err = stderrTask |> Async.AwaitTask
        return proc.ExitCode, err
    }

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

/// Construct a RunResult signalling that an all_repos dependency gate was not met.
/// No repos are processed and no lock file is written.
let private blockedResult (config: JobConfig) (reason: string) : RunResult =
    let dummyProject =
        { Org   = config.Org
          Id    = ProjectId "0"
          Title = config.ProjectTitle
          Url   = "" }
    let dummyLock =
        { LockedAt     = DateTimeOffset.MinValue
          YamlHash     = ""
          TemplateHash = ""
          Project      = dummyProject
          Repos        = []
          Issues       = []
          PullRequests = []
          SkippedRepos = []
          Failures     = [] }
    { Lock = dummyLock; Results = []; Source = FullRun; BlockedBy = Some reason }

/// True if the error string from `gh issue edit` indicates the issue no longer exists
/// (deleted, transferred, or never existed). Lock entries that hit this are recoverable
/// by recreating the issue in the same repo.
let isStaleIssue (e: string) =
    e.Contains("Could not resolve to an issue", StringComparison.OrdinalIgnoreCase)

/// Placeholder IssueRef for a repo that was skipped because it is archived.
/// Carries no real issue id — id is "0" and URL points to the repo home.
let private archivedPlaceholder (repo: RepoName) : IssueRef =
    let (RepoName r) = repo
    { Repo = repo; Id = IssueId "0"; Url = $"https://github.com/{r}"; Assignees = [] }

/// Returns true when a FetchReposState error definitively means the repo does not exist
/// (as opposed to a transient network/API failure where falling back to individual calls is valid).
let private isRepoNotFoundError (e: string) =
    e = "not found or inaccessible" ||
    e.Contains("Could not resolve to a Repository")

/// Placeholder IssueRef for a repo where a dry run would have created a new issue.
/// Id is "0" and URL points to a non-existent /issues/0 path.
let private dryRunCreatePlaceholder (repo: RepoName) : IssueRef =
    let (RepoName r) = repo
    { Repo = repo; Id = IssueId "0"; Url = $"https://github.com/{r}/issues/0"; Assignees = [] }

/// Default maximum retry attempts per (repo, category) failure before a step is skipped.
let defaultMaxAttempts = 3

/// Append a `Closes #<issue>` line to a PR body so GitHub actually links the PR to
/// the issue (populates `closingPullRequests`, auto-closes on merge). Without this,
/// GitHub has no relationship between the two — orcai's own `ClosesIssue` bookkeeping
/// is in-memory only and isn't communicated to GitHub any other way. A no-op if the
/// body already references the issue.
let appendClosesIssue (issueNum: string) (body: string) : string =
    let closesLine = $"Closes #{issueNum}"
    if body.Contains($"#{issueNum}") then body
    elif String.IsNullOrWhiteSpace(body) then closesLine
    else body.TrimEnd() + "\n\n" + closesLine

/// Build the `gh pr create` ProcessStartInfo. Sets GH_TOKEN on the child process
/// when `token` is non-empty, so the resolved App/PAT token reaches `gh` instead of
/// relying on ambient credentials.
let buildPrCreatePsi (token: string) (repo: string) (head: string) (title: string) (body: string) (workingDir: string) : Diagnostics.ProcessStartInfo =
    let psi = Diagnostics.ProcessStartInfo("gh")
    psi.WorkingDirectory       <- workingDir
    psi.RedirectStandardOutput <- true
    psi.RedirectStandardError  <- true
    psi.UseShellExecute        <- false
    psi.ArgumentList.Add("pr")
    psi.ArgumentList.Add("create")
    psi.ArgumentList.Add("--repo")
    psi.ArgumentList.Add(repo)
    psi.ArgumentList.Add("--head")
    psi.ArgumentList.Add(head)
    psi.ArgumentList.Add("--title")
    psi.ArgumentList.Add(title)
    psi.ArgumentList.Add("--body")
    psi.ArgumentList.Add(body)
    if token <> "" then psi.Environment["GH_TOKEN"] <- token
    psi

/// Resolved per-run parameters shared by `processRepo` (used by both `runFull` and
/// the stale-issue recovery path inside `refreshBodies`).
type private ProcessParams =
    { Config              : JobConfig
      Project             : ProjectInfo
      Verbose             : bool
      AutoCreateLabels    : bool
      Tracker             : IIssueTracker
      Repos               : IRepoInspector option
      Prs                 : IPullRequestLinker option
      ClosedIssueAction   : ClosedIssueAction
      Action              : ActionConfig
      JobOwner            : string option
      DryRun              : bool
      MaxAttempts         : int
      YamlHashChanged     : bool
      TemplateHashChanged : bool
      /// True only when an actual prior lock file was read (as opposed to one being
      /// absent/bypassed). YamlHashChanged/TemplateHashChanged are forced true when
      /// there is no prior lock to compare against — correct for most retry-gating
      /// decisions (shouldAttempt), but wrong for cmd-to-github's idempotency check,
      /// which must not redo a live, unmodified PR just because the lock file was
      /// deleted. See plans/cmd-to-github-idempotency.md.
      HasPriorLock        : bool
      CheckoutRoot        : string
      CheckoutToken       : string }

let private resolveProcessParams
    (deps                : OrcAIDeps)
    (input               : RunInput)
    (config              : JobConfig)
    (project             : ProjectInfo)
    (providerClients     : ProviderClients)
    (yamlHashChanged     : bool)
    (templateHashChanged : bool)
    (hasPriorLock        : bool)
    (checkoutRoot        : string)
    (checkoutToken       : string)
    : ProcessParams =
    let closedIssueAction =
        input.OnClosedIssue |> Option.defaultValue config.OnClosedIssue
    let jobOwner =
        config.JobOwner
        |> Option.orElseWith (fun () ->
            let dir =
                System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(input.YamlPath))
                |> Option.ofObj
                |> Option.defaultValue "."
            Codeowners.tryReadLocal deps.FileSystem dir)
    { Config              = config
      Project             = project
      Verbose             = input.Verbose
      AutoCreateLabels    = input.AutoCreateLabels
      Tracker             = providerClients.Tracker
      Repos               = providerClients.Repos
      Prs                 = providerClients.Prs
      ClosedIssueAction   = closedIssueAction
      Action              = config.Action
      JobOwner            = jobOwner
      DryRun              = input.DryRun
      MaxAttempts         = config.MaxAttempts |> Option.defaultValue defaultMaxAttempts
      YamlHashChanged     = yamlHashChanged
      TemplateHashChanged = templateHashChanged
      HasPriorLock        = hasPriorLock
      CheckoutRoot        = checkoutRoot
      CheckoutToken       = checkoutToken }

/// Decide whether to attempt `cat` for this repo, given prior failures.
/// Skips when attempts hit the cap, or when the cause is UserError and the
/// relevant hash hasn't changed (re-edit the YAML/template to retry).
let private shouldAttempt
    (p             : ProcessParams)
    (priorFailures : RepoFailure list)
    (cat           : RepoFailureCategory)
    : bool =
    match priorFailures |> List.tryFind (fun f -> f.Category = cat) with
    | None                                        -> true
    | Some e when e.Attempts >= p.MaxAttempts     -> false
    | Some e when e.Cause = UserError ->
        match cat with
        | UpdateBody -> p.TemplateHashChanged
        | _          -> p.YamlHashChanged
    | _                                           -> true

/// Ensure every requested label exists in the repo, creating any that are missing.
/// Returns Ok () when all labels are present or successfully created.
/// When dryRun is true, lists missing labels (and logs them when verbose) but does not create them.
let private ensureLabelsExist
    (client  : IIssueTracker)
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

/// Idempotency decision for cmd-to-github, computed from live GitHub/branch state
/// before any clone/execute/push — never from the lock file (see
/// plans/cmd-to-github-idempotency.md). Drives whether the expensive checkout
/// pipeline runs at all.
type internal CmdToGithubDecision =
    /// Nothing left to do — return the (possibly still-current) PR unchanged.
    | SkipIdempotent  of pr: PullRequestRef option
    /// No PR/branch changed since last run and hashes are unchanged — just retry
    /// `gh pr create` against the existing remote branch, no checkout needed.
    | RetryPrCreateOnly of head: string
    /// onClosedPr: reopen — reopen the PR, then push fresh content to its branch
    /// (no new PR opened).
    | ReopenAndRedo of pr: PullRequestRef
    /// onClosedPr: fail — record a failure requiring manual intervention.
    | FailClosedPr of pr: PullRequestRef
    /// Redo the full pipeline: clone, execute, commit, push (and open a PR unless
    /// the write-back mode already found one open).
    | ProceedFullRun

/// Pure decision logic for cmd-to-github's idempotency check, given already-fetched
/// live state — no I/O, so every row of the decision table
/// (plans/cmd-to-github-idempotency.md) is directly unit-testable.
/// `prs` is the live list of PRs closing the issue (from FindPrsForIssue).
/// `branchExists` is only consulted when `prs` has no OPEN/MERGED/CLOSED entry.
let internal decideCmdToGithubAction
    (branch        : string)
    (prs           : PullRequestRef list)
    (onClosedPr    : ClosedPrWriteBackAction)
    (hashesChanged : bool)
    (branchExists  : bool)
    : CmdToGithubDecision =
    let openLive   = prs |> List.tryFind (fun pr -> pr.State = "OPEN")
    let mergedLive = prs |> List.tryFind (fun pr -> pr.State = "MERGED")
    let closedLive = prs |> List.tryFind (fun pr -> pr.State = "CLOSED")
    match openLive, mergedLive, closedLive with
    | Some pr, _, _ when not hashesChanged -> SkipIdempotent (Some pr)
    | Some _, _, _                         -> ProceedFullRun
    | None, Some pr, _                     -> SkipIdempotent (Some pr)
    | None, None, Some pr ->
        match onClosedPr with
        | SkipClosedPr   -> SkipIdempotent (Some pr)
        | FailOnClosedPr -> FailClosedPr pr
        | RecreatePr     -> ProceedFullRun
        | ReopenPr       -> ReopenAndRedo pr
    | None, None, None ->
        if branchExists && not hashesChanged then RetryPrCreateOnly branch
        else ProceedFullRun

/// Run `gh pr create` for a cmd-to-github write-back. Returns the opened PR ref on
/// success, or None if it already exists or the call failed (both cases are
/// recorded via `record`).
let private openCmdToGithubPr
    (record     : RepoFailureCategory -> Result<unit, string> -> unit)
    (verbose    : bool)
    (repoStr    : string)
    (token      : string)
    (issueId    : IssueId)
    (repo       : RepoName)
    (head       : string)
    (title      : string)
    (body       : string)
    (workingDir : string)
    : Async<PullRequestRef option> =
    async {
        let psi = buildPrCreatePsi token repoStr head title body workingDir
        use proc = Diagnostics.Process.Start(psi)
        let stdoutTask = proc.StandardOutput.ReadToEndAsync()
        let stderrTask = proc.StandardError.ReadToEndAsync()
        do! proc.WaitForExitAsync() |> Async.AwaitTask
        let! prUrl = stdoutTask |> Async.AwaitTask
        let! err   = stderrTask |> Async.AwaitTask
        if proc.ExitCode <> 0 then
            if err.Contains("already exists") then
                record CmdToGithubOpenPrFailed (Ok ())
                if verbose then eprintfn "[%s] PR already exists for branch %s" repoStr head
            else
                record CmdToGithubOpenPrFailed (Error (err.Trim()))
                eprintfn "[%s] Warning: pr create failed: %s" repoStr err
            return None
        else
            record CmdToGithubOpenPrFailed (Ok ())
            if verbose then eprintfn "[%s] PR opened: %s" repoStr (prUrl.Trim())
            return Some { Repo = repo; Number = PrNumber 0; Url = prUrl.Trim(); ClosesIssue = issueId; State = "OPEN" }
    }

/// Result of processing a single repo: the run outcome (if any) plus the per-step
/// attempt results, which `runFull` feeds into `mergeFailures` to update the lock.
type private RepoOutcome =
    { Result      : RepoResult option
      Attempted   : (RepoFailureCategory * Result<unit, string>) list
      PullRequest : PullRequestRef option }

/// Process a single repo: find/create issue, add to project, trigger assignee.
/// `priorFailures` drives the per-step skip decisions (max-attempts cap, UserError
/// causes locked behind a hash change).
let private processRepo
    (deps            : OrcAIDeps)
    (p               : ProcessParams)
    (priorFailures   : RepoFailure list)
    (prefetchedState : RepoState option)
    (repo            : RepoName)
    : Async<RepoOutcome> =
    async {
        let client = p.Tracker
        let (RepoName repoStr) = repo
        let config            = p.Config
        let project           = p.Project
        let verbose           = p.Verbose
        let autoCreateLabels  = p.AutoCreateLabels
        let closedIssueAction = p.ClosedIssueAction
        let action            = p.Action
        let jobOwner          = p.JobOwner
        let dryRun            = p.DryRun
        let checkoutRoot      = p.CheckoutRoot

        let attempted = ResizeArray<RepoFailureCategory * Result<unit, string>>()
        let record cat result = attempted.Add(cat, result)
        let outcome r         = { Result = r; Attempted = List.ofSeq attempted; PullRequest = None }
        let outcomeWithPr r pr = { Result = r; Attempted = List.ofSeq attempted; PullRequest = pr }

        // -1. Pre-check: skip repos that are archived (read-only). Uses prefetched state
        //     when available; falls back to an individual API call (e.g. recreateStaleIssues).
        //     Errors from the pre-check itself are non-fatal — fall through and let the write fail.
        let! archivedResult =
            match prefetchedState with
            | Some s -> async { return Ok s.IsArchived }
            | None   ->
                match p.Repos with
                | Some repos -> repos.IsArchived repo
                | None       -> async { return Ok false }
        match archivedResult with
        | Ok true ->
            if verbose then eprintfn "[%s] Repo is archived — skipping." repoStr
            return outcome (Some { Issue = archivedPlaceholder repo; Outcome = SkippedArchived })
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

        // 1. Find or create issue. shouldAttempt-gated skipping when prior failures
        //    have hit the cap or are UserError without a relevant hash change.
        let! issueResult =
            async {
                if not (shouldAttempt p priorFailures FindIssue) then
                    return Error "FindIssue skipped (prior failure not retryable)"
                else
                let findOpen =
                    match prefetchedState with
                    | Some s -> async { return Ok s.OpenIssue }
                    | None   -> client.FindIssue repo config.IssueTitle
                match! findOpen with
                | Error e ->
                    record FindIssue (Error e)
                    return Error $"Failed to check for existing open issue: {e}"
                | Ok (Some issue) ->
                    record FindIssue (Ok ())
                    if verbose then eprintfn "[%s] Issue already exists: %s" repoStr issue.Url
                    return Ok (issue, AlreadyExisted)
                | Ok None ->
                    record FindIssue (Ok ())
                    let findClosed =
                        match prefetchedState with
                        | Some s -> async { return Ok s.ClosedIssue }
                        | None   -> client.FindClosedIssue repo config.IssueTitle
                    match! findClosed with
                    | Error e ->
                        record FindIssue (Error e)
                        return Error $"Failed to check for existing closed issue: {e}"
                    | Ok None ->
                        if dryRun then
                            if verbose then eprintfn "[%s] Would create issue '%s' (dry run)" repoStr config.IssueTitle
                            return Ok (dryRunCreatePlaceholder repo, DryRunWouldCreate)
                        elif not (shouldAttempt p priorFailures CreateIssue) then
                            return Error "CreateIssue skipped (prior failure not retryable)"
                        else
                            if verbose then eprintfn "[%s] Creating issue '%s'" repoStr config.IssueTitle
                            match! client.CreateIssue repo config.IssueTitle config.IssueBodyByRepo.[repo] config.Labels with
                            | Ok issue -> record CreateIssue (Ok ()); return Ok (issue, Created)
                            | Error e  -> record CreateIssue (Error e); return Error e
                    | Ok (Some closed) ->
                        match closedIssueAction with
                        | Create ->
                            if dryRun then
                                if verbose then eprintfn "[%s] Would create issue '%s' (dry run)" repoStr config.IssueTitle
                                return Ok (dryRunCreatePlaceholder repo, DryRunWouldCreate)
                            elif not (shouldAttempt p priorFailures CreateIssue) then
                                return Error "CreateIssue skipped (prior failure not retryable)"
                            else
                                if verbose then eprintfn "[%s] Creating issue '%s'" repoStr config.IssueTitle
                                match! client.CreateIssue repo config.IssueTitle config.IssueBodyByRepo.[repo] config.Labels with
                                | Ok issue -> record CreateIssue (Ok ()); return Ok (issue, Created)
                                | Error e  -> record CreateIssue (Error e); return Error e
                        | Reopen ->
                            if dryRun then
                                if verbose then eprintfn "[%s] Would reopen closed issue: %s (dry run)" repoStr closed.Url
                                return Ok (closed, DryRunWouldReopen)
                            elif not (shouldAttempt p priorFailures ReopenIssue) then
                                return Error "ReopenIssue skipped (prior failure not retryable)"
                            else
                                if verbose then eprintfn "[%s] Reopening closed issue: %s" repoStr closed.Url
                                match! client.ReopenIssue repo closed.Id with
                                | Ok issue -> record ReopenIssue (Ok ()); return Ok (issue, Reopened)
                                | Error e  -> record ReopenIssue (Error e); return Error e
                        | Skip ->
                            if verbose then eprintfn "[%s] Closed issue found, skipping: %s" repoStr closed.Url
                            return Ok (closed, Skipped)
                        | Fail ->
                            eprintfn "[%s] Closed issue found and --on-closed-issue=fail is set: %s" repoStr closed.Url
                            return Error $"Closed issue exists for repo {repoStr}: {closed.Url}"
            }

        match issueResult with
        | Error e ->
            if verbose then eprintfn "[%s] Error finding/creating issue: %s" repoStr e
            return outcome None
        | Ok (issue, issueOutcome) ->

        // 2. Add to project and assign copilot (bypassed for Skipped outcome)
        if issueOutcome = Skipped then
            return outcome (Some { Issue = issue; Outcome = issueOutcome })
        else

        // Dry-run short-circuit: no further mutations beyond this point.
        if dryRun then
            if verbose then
                match issueOutcome with
                | DryRunWouldCreate -> eprintfn "[%s] Would add issue to project and trigger action (dry run)" repoStr
                | DryRunWouldReopen -> eprintfn "[%s] Would refresh project membership and trigger action (dry run)" repoStr
                | _                 -> ()
            return outcome (Some { Issue = issue; Outcome = issueOutcome })
        else

        // 3. Add to project (idempotent — gh CLI succeeds silently when the item is already present).
        if shouldAttempt p priorFailures AddToProject then
            if verbose && issueOutcome <> AlreadyExisted then eprintfn "[%s] Adding issue to project" repoStr
            match! client.AddIssueToProject project issue with
            | Ok ()   -> record AddToProject (Ok ())
            | Error e ->
                record AddToProject (Error e)
                eprintfn "[%s] Warning: failed to add issue to project: %s" repoStr e
        elif verbose then
            eprintfn "[%s] Skipping AddToProject (prior failure not retryable)" repoStr

        // 4. Execute action. Returns (finalIssue, maybePr) — pr is Some only for cmd-to-github.
        let! finalIssue, maybePr =
            match action with
            | Noop ->
                if verbose then eprintfn "[%s] Skipping action (noop)" repoStr
                async { return issue, None }
            | Comment commentTmpl ->
                async {
                    do! Comments.postTemplatedComment client p.Repos repo issue.Id "@" jobOwner commentTmpl verbose "trigger" Map.empty
                    return issue, None
                }
            | AssignCopilot _ | Assign _ | CommentAndAssign _ ->
                let assignTo, wantsComment, commentTmpl =
                    match action with
                    | AssignCopilot c          -> "@copilot", c.IsSome, c |> Option.defaultValue ""
                    | Assign(t, c)             -> t,          c.IsSome, c |> Option.defaultValue ""
                    | CommentAndAssign(t, c)   -> t,          true,     c
                    | _                        -> failwith "unreachable"
                async {
                    if wantsComment then
                        do! Comments.postTemplatedComment client p.Repos repo issue.Id assignTo jobOwner commentTmpl verbose "trigger" Map.empty
                    if not (hasAssignee assignTo issue) then
                        if not (shouldAttempt p priorFailures AssignIssue) then
                            if verbose then eprintfn "[%s] Skipping AssignIssue (prior failure not retryable)" repoStr
                            return issue, None
                        else
                            if verbose then eprintfn "[%s] Assigning %s" repoStr assignTo
                            match! client.AssignIssue repo issue.Id assignTo with
                            | Error e ->
                                record AssignIssue (Error e)
                                eprintfn "[%s] Warning: failed to assign %s: %s" repoStr assignTo e
                                return issue, None
                            | Ok () ->
                                record AssignIssue (Ok ())
                                return { issue with Assignees = issue.Assignees @ [assignTo.TrimStart('@')] }, None
                    else
                        return issue, None
                }
            | Cmd(exec, cwd, copy) ->
                async {
                    let (IssueId issueNum) = issue.Id
                    let (OrgName orgStr) = config.Org
                    let (ProjectId projectNum) = project.Id
                    let vars =
                        Map.ofList [
                            "repo",           repoStr
                            "org",            orgStr
                            "issue_number",   issueNum
                            "issue_url",      issue.Url
                            "job_title",      config.ProjectTitle
                            "issue_text",     config.IssueBodyByRepo.[repo]
                            "project_number", projectNum
                            "run_datetime",   DateTimeOffset.UtcNow.ToString("o")
                            "issue_hash",     YamlConfig.hashBytes (Text.Encoding.UTF8.GetBytes(config.IssueBodyByRepo.[repo]))
                            "yaml_hash",      ""
                        ]
                    let render (s: string) = renderActionTemplate vars s
                    let executable, allArgs = resolveExec render exec
                    let workingDir = cwd |> Option.map render |> Option.defaultValue "."
                    match FileGlob.copyAll Environment.CurrentDirectory (Path.GetFullPath workingDir) copy with
                    | Error e ->
                        eprintfn "[%s] Warning: copy failed: %s" repoStr e
                        return issue, None
                    | Ok written ->
                    if verbose then
                        eprintfn "[%s] Executing: %s %s" repoStr executable (String.concat " " allArgs)
                    let! exitCode, err = runExecCommand executable allArgs workingDir
                    if exitCode <> 0 then
                        eprintfn "[%s] Warning: cmd exited with code %d: %s" repoStr exitCode err
                    FileGlob.cleanupCopies written
                    return issue, None
                }
            | CmdCheckout(exec, cwd, copy) ->
                async {
                    let (IssueId issueNum) = issue.Id
                    let (OrgName orgStr) = config.Org
                    let branchSlug = CheckoutManager.slugify config.ProjectTitle
                    if verbose then eprintfn "[%s] Cloning repo for cmd-checkout" repoStr
                    let! cloneResult = CheckoutManager.ensureClone p.CheckoutToken checkoutRoot repo
                    match cloneResult with
                    | Error e ->
                        record CmdCheckoutFailed (Error e)
                        eprintfn "[%s] Warning: checkout failed: %s" repoStr e
                        return issue, None
                    | Ok _ ->
                        let! wtResult = CheckoutManager.getWorktree checkoutRoot repo branchSlug
                        match wtResult with
                        | Error e ->
                            record CmdCheckoutFailed (Error e)
                            eprintfn "[%s] Warning: worktree failed: %s" repoStr e
                            return issue, None
                        | Ok worktreePath ->
                            let! defaultBranchResult = CheckoutManager.getDefaultBranch checkoutRoot repo
                            let defaultBranch = defaultBranchResult |> Result.defaultValue ""
                            let (ProjectId projectNum) = project.Id
                            let vars =
                                Map.ofList [
                                    "repo",           repoStr
                                    "org",            orgStr
                                    "issue_number",   issueNum
                                    "issue_url",      issue.Url
                                    "job_title",      config.ProjectTitle
                                    "issue_text",     config.IssueBodyByRepo.[repo]
                                    "project_number", projectNum
                                    "run_datetime",   DateTimeOffset.UtcNow.ToString("o")
                                    "issue_hash",     YamlConfig.hashBytes (Text.Encoding.UTF8.GetBytes(config.IssueBodyByRepo.[repo]))
                                    "yaml_hash",      ""
                                    "checkout_path",  worktreePath
                                    "job_title_slug", branchSlug
                                    "default_branch", defaultBranch
                                ]
                            let render (s: string) = renderActionTemplate vars s
                            let executable, allArgs = resolveExec render exec
                            // cwd is relative to the checkout root, not the process CWD.
                            let workingDir = cwd |> Option.map (fun c -> Path.Combine(worktreePath, render c)) |> Option.defaultValue worktreePath
                            match FileGlob.copyAll Environment.CurrentDirectory workingDir copy with
                            | Error e ->
                                record CmdCheckoutFailed (Error e)
                                eprintfn "[%s] Warning: copy failed: %s" repoStr e
                                CheckoutManager.cleanup checkoutRoot repo branchSlug
                                return issue, None
                            | Ok written ->
                            if verbose then
                                eprintfn "[%s] Executing in checkout: %s %s" repoStr executable (String.concat " " allArgs)
                            let! exitCode, err = runExecCommand executable allArgs workingDir
                            if exitCode <> 0 then
                                record CmdCheckoutFailed (Error $"cmd exited {exitCode}: {err.Trim()}")
                                eprintfn "[%s] Warning: cmd-checkout exited with code %d: %s" repoStr exitCode err
                            FileGlob.cleanupCopies written
                            CheckoutManager.cleanup checkoutRoot repo branchSlug
                            return issue, None
                }
            | CmdToGithub(cfg) ->
                async {
                    let (IssueId issueNum) = issue.Id
                    let (OrgName orgStr) = config.Org
                    let branchSlug = CheckoutManager.slugify config.ProjectTitle
                    let branch     = cfg.Branch        |> Option.defaultValue $"orcai/{branchSlug}"
                    let commitMsg  = cfg.CommitMessage |> Option.defaultValue $"[{issueNum}] {config.ProjectTitle}"
                    let prTitle    = cfg.PrTitle       |> Option.defaultWith (fun () -> cfg.CommitMessage |> Option.defaultValue $"[{issueNum}] {config.ProjectTitle}")
                    let prBody     = cfg.PrBody        |> Option.defaultValue ""
                    // Resolve write-back: job-level YAML → OrcAI config → default open-pr.
                    let effectiveWriteBack =
                        match cfg.WriteBack with
                        | Some wb -> wb
                        | None ->
                            match deps.Config.Action |> Option.bind (fun a -> a.WriteBack) with
                            | Some "push-branch" -> PushBranch
                            | Some "fork-and-pr" -> ForkAndPr
                            | _                  -> OpenPr
                    // Resolve onClosedPr: job-level YAML → OrcAI config → default skip.
                    let onClosedPr =
                        match cfg.OnClosedPr with
                        | Some a -> a
                        | None ->
                            match deps.Config.Action |> Option.bind (fun a -> a.OnClosedPr) with
                            | Some "recreate" -> RecreatePr
                            | Some "reopen"   -> ReopenPr
                            | Some "fail"     -> FailOnClosedPr
                            | _               -> SkipClosedPr
                    let (ProjectId projectNum) = project.Id
                    // Vars available before any clone (excludes checkout_path/default_branch,
                    // which don't exist yet). Used only to resolve the branch name for the
                    // live idempotency check below; the full vars map (with checkout-only
                    // tokens) is rebuilt once a worktree exists, for the actual run.
                    let earlyVars =
                        Map.ofList [
                            "repo",           repoStr
                            "org",            orgStr
                            "issue_number",   issueNum
                            "issue_url",      issue.Url
                            "job_title",      config.ProjectTitle
                            "issue_text",     config.IssueBodyByRepo.[repo]
                            "project_number", projectNum
                            "run_datetime",   DateTimeOffset.UtcNow.ToString("o")
                            "issue_hash",     YamlConfig.hashBytes (Text.Encoding.UTF8.GetBytes(config.IssueBodyByRepo.[repo]))
                            "yaml_hash",      ""
                            "job_title_slug", branchSlug
                        ]
                    let earlyRender (s: string) = renderActionTemplate earlyVars s
                    let renderedBranchEarly = earlyRender branch
                    // Without a prior lock file there is no baseline to compare against —
                    // YamlHashChanged/TemplateHashChanged are forced true in that case for
                    // other purposes (shouldAttempt retry gating), but that would wrongly
                    // force a redo of a live, unmodified PR just because the lock file is
                    // missing. Trust the live PR check instead: no baseline ⇒ not changed.
                    let hashesChanged = p.HasPriorLock && (p.YamlHashChanged || p.TemplateHashChanged)

                    // -----------------------------------------------------------------
                    // Idempotency decision — live GitHub/branch state only, computed
                    // before any clone/execute/push (see plans/cmd-to-github-idempotency.md).
                    // Never derived from the lock file, so correctness holds even when it
                    // is missing, deleted, or stale.
                    //
                    // Only applies when this action opens/tracks a PR (open-pr, fork-and-pr) —
                    // push-branch never creates a PR, so there is no PR/branch state to be
                    // idempotent about; it always proceeds with a full run, unchanged from
                    // before this feature.
                    // -----------------------------------------------------------------
                    let! decision =
                        match effectiveWriteBack, p.Prs with
                        | PushBranch, _ | _, None -> async { return ProceedFullRun }
                        | _, Some prsLinker ->
                            async {
                                let! prs = prsLinker.FindPrsForIssue repo issue.Id
                                if List.isEmpty prs then
                                    let! branchExistsResult = CheckoutManager.branchExistsOnRemote p.CheckoutToken repo renderedBranchEarly
                                    let branchExists = branchExistsResult |> Result.defaultValue false
                                    return decideCmdToGithubAction renderedBranchEarly prs onClosedPr hashesChanged branchExists
                                else
                                    return decideCmdToGithubAction renderedBranchEarly prs onClosedPr hashesChanged false
                            }

                    match decision with
                    | SkipIdempotent maybePr ->
                        if verbose then eprintfn "[%s] cmd-to-github: up to date — skipping checkout/execute/push." repoStr
                        return issue, maybePr
                    | FailClosedPr pr ->
                        record CmdToGithubClosedPrFailed (Error $"Closed PR found for branch (onClosedPr: fail): {pr.Url}")
                        eprintfn "[%s] Warning: cmd-to-github: closed PR requires manual intervention (onClosedPr: fail): %s" repoStr pr.Url
                        return issue, None
                    | RetryPrCreateOnly head ->
                        let renderedPrTitle = earlyRender prTitle
                        let renderedPrBody  = earlyRender prBody |> appendClosesIssue issueNum
                        let! prResult = openCmdToGithubPr record verbose repoStr p.CheckoutToken issue.Id repo head renderedPrTitle renderedPrBody (Path.GetTempPath())
                        return issue, prResult
                    | ReopenAndRedo _ | ProceedFullRun ->

                    let reopenTarget = match decision with ReopenAndRedo pr -> Some pr | _ -> None

                    if verbose then eprintfn "[%s] Cloning repo for cmd-to-github" repoStr
                    let! cloneResult = CheckoutManager.ensureClone p.CheckoutToken checkoutRoot repo
                    match cloneResult with
                    | Error e ->
                        record CmdToGithubCheckoutFailed (Error e)
                        eprintfn "[%s] Warning: checkout failed: %s" repoStr e
                        return issue, None
                    | Ok _ ->
                        let! wtResult = CheckoutManager.getWorktree checkoutRoot repo branchSlug
                        match wtResult with
                        | Error e ->
                            record CmdToGithubCheckoutFailed (Error e)
                            eprintfn "[%s] Warning: worktree failed: %s" repoStr e
                            return issue, None
                        | Ok worktreePath ->
                            let! defaultBranchResult = CheckoutManager.getDefaultBranch checkoutRoot repo
                            let defaultBranch = defaultBranchResult |> Result.defaultValue ""
                            let (ProjectId projectNum) = project.Id
                            let vars =
                                Map.ofList [
                                    "repo",           repoStr
                                    "org",            orgStr
                                    "issue_number",   issueNum
                                    "issue_url",      issue.Url
                                    "job_title",      config.ProjectTitle
                                    "issue_text",     config.IssueBodyByRepo.[repo]
                                    "project_number", projectNum
                                    "run_datetime",   DateTimeOffset.UtcNow.ToString("o")
                                    "issue_hash",     YamlConfig.hashBytes (Text.Encoding.UTF8.GetBytes(config.IssueBodyByRepo.[repo]))
                                    "yaml_hash",      ""
                                    "checkout_path",  worktreePath
                                    "job_title_slug", branchSlug
                                    "default_branch", defaultBranch
                                ]
                            let render (s: string) = renderActionTemplate vars s
                            let executable, allArgs = resolveExec render cfg.Execute
                            // cwd is relative to the checkout root, not the process CWD.
                            let workingDir = cfg.Cwd |> Option.map (fun c -> Path.Combine(worktreePath, render c)) |> Option.defaultValue worktreePath
                            match FileGlob.copyAll Environment.CurrentDirectory workingDir cfg.Copy with
                            | Error e ->
                                record CmdToGithubCheckoutFailed (Error e)
                                eprintfn "[%s] Warning: copy failed: %s" repoStr e
                                CheckoutManager.cleanup checkoutRoot repo branchSlug
                                return issue, None
                            | Ok written ->
                            if verbose then
                                eprintfn "[%s] Executing in checkout: %s %s" repoStr executable (String.concat " " allArgs)
                            let! exitCode, err = runExecCommand executable allArgs workingDir
                            if exitCode <> 0 then
                                record CmdToGithubCheckoutFailed (Error $"cmd exited {exitCode}: {err.Trim()}")
                                eprintfn "[%s] Warning: cmd-to-github cmd exited with code %d: %s" repoStr exitCode err
                                FileGlob.cleanupCopies written
                                CheckoutManager.cleanup checkoutRoot repo branchSlug
                                return issue, None
                            else
                                FileGlob.cleanupCopies written
                                let renderedMsg    = render commitMsg
                                let! commitResult  = CheckoutManager.commitAll worktreePath renderedMsg
                                match commitResult with
                                | Error "no-diff" when not cfg.ErrorIfNoDiff ->
                                    if verbose then eprintfn "[%s] cmd-to-github: no changes, skipping PR" repoStr
                                    CheckoutManager.cleanup checkoutRoot repo branchSlug
                                    return issue, None
                                | Error "no-diff" ->
                                    record CmdToGithubNoDiff (Error "cmd produced no diff (error_if_no_diff is set)")
                                    eprintfn "[%s] Warning: cmd-to-github: no diff after cmd succeeded" repoStr
                                    CheckoutManager.cleanup checkoutRoot repo branchSlug
                                    return issue, None
                                | Error e ->
                                    record CmdToGithubCheckoutFailed (Error e)
                                    eprintfn "[%s] Warning: cmd-to-github commit failed: %s" repoStr e
                                    CheckoutManager.cleanup checkoutRoot repo branchSlug
                                    return issue, None
                                | Ok () ->
                                    record CmdToGithubCheckoutFailed (Ok ())
                                    record CmdToGithubNoDiff (Ok ())
                                    let renderedBranch  = render branch
                                    let renderedPrTitle = render prTitle
                                    let renderedPrBody  = render prBody |> appendClosesIssue issueNum
                                    let openPr (head: string) =
                                        openCmdToGithubPr record verbose repoStr p.CheckoutToken issue.Id repo head renderedPrTitle renderedPrBody worktreePath
                                    let! prResult =
                                        match reopenTarget with
                                        | Some pr ->
                                            async {
                                                let! pushResult = CheckoutManager.pushToOrigin p.CheckoutToken "" worktreePath renderedBranch
                                                match pushResult with
                                                | Error e ->
                                                    record CmdToGithubPushFailed (Error e)
                                                    eprintfn "[%s] Warning: push failed: %s" repoStr e
                                                    return None
                                                | Ok () ->
                                                    record CmdToGithubPushFailed (Ok ())
                                                    match p.Prs with
                                                    | None -> return None
                                                    | Some prsLinker ->
                                                        match! prsLinker.ReopenPr repo pr.Number with
                                                        | Error e ->
                                                            eprintfn "[%s] Warning: failed to reopen PR: %s" repoStr e
                                                            return None
                                                        | Ok () ->
                                                            if verbose then eprintfn "[%s] Reopened PR and pushed fresh content: %s" repoStr pr.Url
                                                            return Some { pr with State = "OPEN" }
                                            }
                                        | None ->
                                        match effectiveWriteBack with
                                        | PushBranch ->
                                            async {
                                                let! pushResult = CheckoutManager.pushToOrigin p.CheckoutToken "" worktreePath renderedBranch
                                                match pushResult with
                                                | Error e ->
                                                    record CmdToGithubPushFailed (Error e)
                                                    eprintfn "[%s] Warning: push failed: %s" repoStr e
                                                    return None
                                                | Ok () ->
                                                    record CmdToGithubPushFailed (Ok ())
                                                    if verbose then eprintfn "[%s] Committed and pushed to %s" repoStr renderedBranch
                                                    return None
                                            }
                                        | OpenPr ->
                                            async {
                                                let! pushResult = CheckoutManager.pushToOrigin p.CheckoutToken "" worktreePath renderedBranch
                                                match pushResult with
                                                | Error e ->
                                                    record CmdToGithubPushFailed (Error e)
                                                    eprintfn "[%s] Warning: push failed: %s" repoStr e
                                                    return None
                                                | Ok () ->
                                                    record CmdToGithubPushFailed (Ok ())
                                                    return! openPr renderedBranch
                                            }
                                        | ForkAndPr ->
                                            async {
                                                let! forkResult = CheckoutManager.forkAndPush p.CheckoutToken repo worktreePath renderedBranch
                                                match forkResult with
                                                | Error e ->
                                                    record CmdToGithubPushFailed (Error e)
                                                    eprintfn "[%s] Warning: fork/push failed: %s" repoStr e
                                                    return None
                                                | Ok forkRepo ->
                                                    record CmdToGithubPushFailed (Ok ())
                                                    return! openPr $"{forkRepo}:{renderedBranch}"
                                            }
                                    CheckoutManager.cleanup checkoutRoot repo branchSlug
                                    return issue, prResult
                }

        return outcomeWithPr (Some { Issue = finalIssue; Outcome = issueOutcome }) maybePr
    }

// ---------------------------------------------------------------------------
// Execute
// ---------------------------------------------------------------------------

/// Perform the full run: find/create project, process all repos, write lock.
/// `priorLock` provides per-repo prior issue refs and failure history. The lock
/// is always written outside dry-run mode (partial state included).
let private runFull
    (deps                : OrcAIDeps)
    (input               : RunInput)
    (config              : JobConfig)
    (yamlHash            : string)
    (templateHash        : string)
    (priorLock           : LockFile option)
    (yamlHashChanged     : bool)
    (templateHashChanged : bool)
    : Result<RunResult, string> =

    // Only GitHub jobs need a token; a Local job's ResolveProvider needs no
    // auth at all, so skip this precheck rather than forcing it unconditionally.
    // The resolved token is reused as GH_TOKEN for the checkout git/gh subprocesses
    // (clone, push, PR create), which otherwise only see the runner's ambient auth.
    let tokenResult =
        match config.Provider with
        | Local  -> Ok ""
        | GitHub -> deps.AuthContext.GetToken() |> Async.RunSynchronously

    match tokenResult with
    | Error e -> Error $"Auth error: {e}"
    | Ok checkoutToken ->

    match deps.ResolveProvider config with
    | Error e -> Error $"Provider error: {e}"
    | Ok providerClients ->

    // 1. Find or create the GitHub Project (must complete before per-repo work).
    //    In dry-run mode, never call CreateProject — synthesise a placeholder so the
    //    rest of the pipeline can still report what would happen.
    let projectResult =
        async {
            let (OrgName orgStr) = config.Org
            match! providerClients.Tracker.FindProject config.Org config.ProjectTitle with
            | Some p -> return Ok p
            | None when input.DryRun ->
                eprintfn "Project '%s' not found in '%s' — would create (dry run)." config.ProjectTitle orgStr
                let placeholder : ProjectInfo =
                    { Org   = config.Org
                      Id    = ProjectId "0"
                      Title = config.ProjectTitle
                      Url   = $"https://github.com/orgs/{orgStr}/projects/0" }
                return Ok placeholder
            | None ->
                eprintfn "Project '%s' not found in '%s', creating..." config.ProjectTitle orgStr
                return! providerClients.Tracker.CreateProject config.Org config.ProjectTitle
        }
        |> Async.RunSynchronously

    match projectResult with
    | Error e -> Error $"Project error: {e}"
    | Ok project ->

    // 2. Process all repos in parallel
    let checkoutRoot =
        input.CheckoutRoot
        |> Option.defaultWith (fun () ->
            Path.Combine(Path.GetTempPath(), $"orcai-{Guid.NewGuid():N}"))
    let processParams = resolveProcessParams deps input config project providerClients yamlHashChanged templateHashChanged priorLock.IsSome checkoutRoot checkoutToken

    let priorFailuresByRepo =
        priorLock
        |> Option.map (fun l ->
            l.Failures |> List.groupBy (fun f -> f.Repo) |> Map.ofList)
        |> Option.defaultValue Map.empty

    // Bulk-prefetch isArchived, open issue, and closed issue for all repos in one
    // GraphQL call instead of N×3 individual REST calls inside processRepo.
    // Providers with no repo inspector (e.g. Local) skip prefetch entirely — processRepo
    // falls back to individual Tracker calls per repo.
    let prefetchedStates =
        match providerClients.Repos with
        | Some repos -> repos.FetchReposState config.Repos config.IssueTitle |> Async.RunSynchronously
        | None       -> Map.empty

    let repoOutcomes =
        config.Repos
        |> List.map (fun repo ->
            let priorFailures  = Map.tryFind repo priorFailuresByRepo |> Option.defaultValue []
            match Map.tryFind repo prefetchedStates with
            | Some (Error e) when isRepoNotFoundError e ->
                // Repo definitively not found — skip without further API calls.
                // Transient errors (where isRepoNotFoundError is false) fall through to individual calls.
                let (RepoName r) = repo
                if input.Verbose then eprintfn "[%s] Repository not found or inaccessible — skipping." r
                async { return { Result = None; Attempted = [FindIssue, Error e]; PullRequest = None } }
            | fetchedResult ->
                let prefetchedState = fetchedResult |> Option.bind Result.toOption
                processRepo deps processParams priorFailures prefetchedState repo)
        |> Async.Parallel
        |> Async.RunSynchronously

    let allResults = repoOutcomes |> Array.choose (fun o -> o.Result) |> Array.toList
    let archivedRepos =
        allResults
        |> List.filter (fun r -> r.Outcome = SkippedArchived)
        |> List.map (fun r -> r.Issue.Repo)
    let successes = allResults |> List.filter (fun r -> r.Outcome <> SkippedArchived)

    let now = DateTimeOffset.UtcNow

    let attempted =
        List.zip config.Repos (List.ofArray repoOutcomes)
        |> List.collect (fun (repo, o) ->
            o.Attempted |> List.map (fun (cat, res) -> repo, cat, res))

    let previousFailures =
        priorLock |> Option.map (fun l -> l.Failures) |> Option.defaultValue []

    let newFailures = LockFile.mergeFailures previousFailures attempted now

    let newPullRequests = repoOutcomes |> Array.choose (fun o -> o.PullRequest) |> Array.toList
    let priorPullRequests = priorLock |> Option.map (fun l -> l.PullRequests) |> Option.defaultValue []
    let mergedPullRequests =
        let newByRepo = newPullRequests |> List.map (fun pr -> pr.Repo) |> Set.ofList
        let kept      = priorPullRequests |> List.filter (fun pr -> not (Set.contains pr.Repo newByRepo))
        kept @ newPullRequests

    let lock : LockFile =
        { LockedAt     = now
          YamlHash     = yamlHash
          TemplateHash = templateHash
          Project      = project
          Repos        = config.Repos
          Issues       = successes |> List.map (fun r -> r.Issue)
          PullRequests = mergedPullRequests
          SkippedRepos = archivedRepos
          Failures     = newFailures }

    if not input.DryRun then
        LockFile.write deps.FileSystem input.YamlPath lock

    // Clean up base clones for checkout-based actions (individual worktrees are
    // already removed inside processRepo; this removes the bare clone dirs).
    let isCheckoutAction =
        match config.Action with
        | CmdCheckout _ | CmdToGithub _ -> true
        | _                         -> false
    if isCheckoutAction then
        for repo in config.Repos do
            CheckoutManager.cleanupAll checkoutRoot repo

    Ok { Lock = lock; Results = allResults; Source = FullRun; BlockedBy = None }

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
            |> List.map (fun repo -> processRepo deps params' [] None repo)
            |> Async.Parallel
            |> Async.RunSynchronously
            |> Array.toList
        let recreatedByRepo =
            List.zip staleRepos recreated
            |> List.choose (fun (repo, outcome) -> outcome.Result |> Option.map (fun r -> repo, r))
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
    (deps                : OrcAIDeps)
    (input               : RunInput)
    (config              : JobConfig)
    (fullResult          : RunResult)
    (yamlHashChanged     : bool)
    (templateHashChanged : bool)
    : Result<RunResult, string> =
    let toRefresh =
        fullResult.Results
        |> List.filter (fun r -> r.Outcome = AlreadyExisted || r.Outcome = Reopened)
    if List.isEmpty toRefresh then
        Ok fullResult
    else

    match deps.ResolveProvider config with
    | Error e -> Error $"Provider error: {e}"
    | Ok providerClients ->
        // Prior failures from the lock (which already includes runFull's merged failures)
        // drive the per-repo UpdateBody skip decision.
        let priorFailuresByRepo =
            fullResult.Lock.Failures
            |> List.groupBy (fun f -> f.Repo)
            |> Map.ofList

        let maxAttempts = config.MaxAttempts |> Option.defaultValue defaultMaxAttempts

        let shouldUpdate (repo: RepoName) : bool =
            let priorEntry =
                priorFailuresByRepo
                |> Map.tryFind repo
                |> Option.bind (fun fs -> fs |> List.tryFind (fun f -> f.Category = UpdateBody))
            match priorEntry with
            | Some e when e.Attempts >= maxAttempts -> false
            | Some e when e.Cause = UserError       -> templateHashChanged
            | _                                     -> true

        let refreshOutcomes =
            toRefresh
            |> List.map (fun r ->
                async {
                    let (RepoName repoStr) = r.Issue.Repo
                    let (IssueId issueN)   = r.Issue.Id
                    if input.DryRun then
                        if input.Verbose then eprintfn "[%s] Would refresh issue body (dry run)" repoStr
                        return { Issue = r.Issue; Outcome = DryRunWouldUpdate }, None
                    elif not (shouldUpdate r.Issue.Repo) then
                        if input.Verbose then eprintfn "[%s] Skipping UpdateBody (prior failure not retryable)" repoStr
                        return r, None
                    else
                        match! providerClients.Tracker.UpdateIssue r.Issue.Repo r.Issue.Id config.IssueTitle config.IssueBodyByRepo.[r.Issue.Repo] with
                        | Ok () ->
                            let newOutcome =
                                match r.Outcome with
                                | Reopened -> Reopened   // keep distinction in summary
                                | _        -> Updated
                            return { Issue = r.Issue; Outcome = newOutcome },
                                   Some (r.Issue.Repo, UpdateBody, Ok ())
                        | Error e when isStaleIssue e ->
                            eprintfn "[%s] Stale issue #%s during body refresh — will recreate." repoStr issueN
                            return { Issue = r.Issue; Outcome = StaleIssueRecreated }, None
                        | Error e ->
                            eprintfn "[%s] Error refreshing issue body: %s" repoStr e
                            return { Issue = r.Issue; Outcome = UpdateFailed e },
                                   Some (r.Issue.Repo, UpdateBody, Error e)
                })
            |> Async.Parallel
            |> Async.RunSynchronously
            |> Array.toList

        let refreshed    = refreshOutcomes |> List.map fst
        let attemptedUB  = refreshOutcomes |> List.choose snd

        let hasStale = refreshed |> List.exists (fun r -> r.Outcome = StaleIssueRecreated)
        let recoveredRefreshed, newRefByRepo =
            if not hasStale then
                refreshed, Map.empty
            else
                let checkoutToken =
                    match config.Provider with
                    | Local  -> ""
                    | GitHub -> deps.AuthContext.GetToken() |> Async.RunSynchronously |> Result.defaultValue ""
                let checkoutRootForRefresh =
                    input.CheckoutRoot
                    |> Option.defaultWith (fun () -> Path.Combine(Path.GetTempPath(), $"orcai-{Guid.NewGuid():N}"))
                let processParams =
                    // Stale-issue recovery: the original issue reference is gone, so
                    // there is no meaningful prior state to compare against here.
                    resolveProcessParams deps input config fullResult.Lock.Project providerClients yamlHashChanged templateHashChanged false checkoutRootForRefresh checkoutToken
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

        let now = DateTimeOffset.UtcNow
        let mergedFailures = LockFile.mergeFailures fullResult.Lock.Failures attemptedUB now

        let finalLock =
            { fullResult.Lock with
                Issues   = finalIssues
                Failures = mergedFailures }

        let lockChanged =
            not (Map.isEmpty newRefByRepo)
            || not (List.isEmpty attemptedUB)
        if lockChanged && not input.DryRun then
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

    // Apply depends_on repo filtering: gate all_repos conditions or narrow per_repo repos.
    let depFilter =
        if mergedConfig.DependsOn.IsEmpty then
            None
        else
            match deps.ResolveProvider mergedConfig with
            | Error e -> Some (Error $"Provider error: {e}")
            | Ok providerClients ->
                let yamlDir = deps.FileSystem.Path.GetDirectoryName(deps.FileSystem.Path.GetFullPath(input.YamlPath)) |> Option.ofObj |> Option.defaultValue "."
                DependencyResolution.filterRepos providerClients.Tracker providerClients.Prs deps.FileSystem mergedConfig yamlDir
                |> Async.RunSynchronously
                |> Some

    match depFilter with
    | Some (Error reason) -> Ok (blockedResult mergedConfig reason)
    | _ ->

    let mergedConfig =
        match depFilter with
        | Some (Ok filteredRepos) -> { mergedConfig with Repos = filteredRepos }
        | _                       -> mergedConfig

    let yamlHash     = YamlConfig.computeHash deps.FileSystem input.YamlPath
    let templateHash =
        match YamlConfig.resolveTemplatePath deps.FileSystem input.YamlPath with
        | Some p -> YamlConfig.computeTemplateHash deps.FileSystem p
        | None   -> ""

    if input.SkipLock then
        if input.Verbose then eprintfn "--skip-lock set, bypassing lock file."
        match runFull deps input mergedConfig yamlHash templateHash None true true with
        | Error e       -> Error e
        | Ok fullResult -> refreshBodies deps input mergedConfig fullResult true true
    else

    match LockFile.tryRead deps.FileSystem input.YamlPath with
    | Some lock when
        lock.YamlHash = yamlHash
        && lock.TemplateHash = templateHash
        && List.isEmpty lock.Failures ->
        if input.Verbose then eprintfn "Lock file found and hashes match — nothing to do."
        let results = lock.Issues |> List.map (fun i -> { Issue = i; Outcome = AlreadyExisted })
        Ok { Lock = lock; Results = results; Source = FromLockFile; BlockedBy = None }

    | Some lock ->
        let yamlHashChanged     = lock.YamlHash <> yamlHash
        let templateHashChanged = lock.TemplateHash <> templateHash
        if input.Verbose then
            if yamlHashChanged || templateHashChanged then
                eprintfn "Lock file found but hashes changed — re-running."
            else
                eprintfn "Lock file has prior failures — retrying."
        match runFull deps input mergedConfig yamlHash templateHash (Some lock) yamlHashChanged templateHashChanged with
        | Error e -> Error e
        | Ok fullResult when not templateHashChanged
                             && not (fullResult.Lock.Failures
                                     |> List.exists (fun f -> f.Category = UpdateBody)) ->
            // No template change and no UpdateBody failures pending — skip body refresh.
            Ok fullResult
        | Ok fullResult ->
            refreshBodies deps input mergedConfig fullResult yamlHashChanged templateHashChanged

    | None ->
        runFull deps input mergedConfig yamlHash templateHash None true true

/// Execute the run command over a list of resolved file paths.
/// Returns a Map from file path to Result<RunResult, string>.
///
/// Files are processed in parallel up to MaxConcurrency (or sequentially when
/// NoParallel=true). Without ContinueOnError the first failure stops processing;
/// with ContinueOnError all files are attempted and per-file errors are collected.
let execute (deps: OrcAIDeps) (paths: string list) (input: RunInput) : Async<Map<string, Result<RunResult, string>>> =
    async {
        // Resolve the full dependency chain: expand, dedup, and topologically order all paths.
        match DependencyResolution.resolveChain deps.FileSystem paths with
        | Error e ->
            let firstPath = paths |> List.tryHead |> Option.defaultValue ""
            return Map.ofList [ firstPath, Error e ]
        | Ok resolvedChain ->

        // If the resolved chain differs from the input (deps were added or ordering changed),
        // run the full chain sequentially so each job's lock is written before dependents start.
        let resolvedAbsPaths = resolvedChain |> List.map fst
        let inputAbsPaths    = paths |> List.map deps.FileSystem.Path.GetFullPath
        let hasOrdering      = resolvedAbsPaths <> inputAbsPaths

        if hasOrdering then
            let results = System.Collections.Generic.Dictionary<string, Result<RunResult, string>>()
            let mutable stop = false
            for (absPath, isDep) in resolvedChain do
                if not stop then
                    let yamlPath =
                        paths
                        |> List.tryFind (fun p -> deps.FileSystem.Path.GetFullPath(p) = absPath)
                        |> Option.defaultValue absPath
                    if isDep then
                        printfn "Running dependency: %s" (deps.FileSystem.Path.GetFileName(absPath))
                    let singleInput = { input with YamlPath = yamlPath }
                    let r = executeSingle deps singleInput
                    results.[yamlPath] <- r
                    match r with
                    | Error _ when not input.ContinueOnError -> stop <- true
                    | _ -> ()
            return results |> Seq.map (fun kv -> kv.Key, kv.Value) |> Map.ofSeq
        else

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
