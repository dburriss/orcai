module OrcAI.Core.NudgeCommand

// ---------------------------------------------------------------------------
// Implements the `orcai nudge` command.
//
// For each issue in the lock file that has no corresponding PR:
//   1. Checks GitHub live via FindPrsForIssue.
//   2. If a PR exists, no action (optionally saves it to the lock file).
//   3. If no PR exists, re-triggers Copilot by unassigning then reassigning @copilot.
//
// Issues that already have a PR in the lock file are skipped without any
// network calls.
// ---------------------------------------------------------------------------

open System
open OrcAI.Core.Domain
open OrcAI.Core.GhClient
open OrcAI.Core.Deps

type NudgeInput =
    { YamlPath         : string
      DryRun           : bool
      Verbose          : bool
      SaveLock         : bool
      MaxConcurrency   : int
      IsPrimaryAuthApp : bool
      OnClosedPr       : ClosedPrAction }

type NudgeOutcome =
    | Skipped
    | PrFoundLive
    | SkippedClosedPr
    | NudgeSent
    | DryRunWouldNudge
    | NudgeFailed of reason: string

type NudgeResult =
    { Repo    : RepoName
      Issue   : IssueNumber
      Outcome : NudgeOutcome
      LivePrs : PullRequestRef list }

/// True when a `gh` error message indicates the GitHub App token cannot assign
/// agents (e.g. @copilot) and a PAT is required instead.
let isAppTokenAssignError (msg: string) =
    msg.Contains(
        "Assigning agents is not supported with GitHub App installation tokens",
        StringComparison.OrdinalIgnoreCase)

/// Classify a raw gh error and return a user-friendly nudge failure message.
let private nudgeFailureMessage (assignTo: string) (rawError: string) : string =
    match LockFile.classifyCause rawError with
    | NotFound        -> "issue not found on GitHub — the lock file may be stale; run 'orcai run' to refresh"
    | UserError       -> $"could not assign '{assignTo}' — check the assignee name is valid"
    | Permission      -> "permission denied — ensure the token has the required scopes"
    | RateLimit       -> rawError
    | NetworkTransient -> $"network error contacting GitHub — try again ({rawError})"
    | Unknown         -> rawError

let execute (deps: OrcAIDeps) (input: NudgeInput) : Result<NudgeResult list, string> =
    match YamlConfig.parseFile deps.FileSystem input.YamlPath with
    | Error e -> Error e
    | Ok jobConfig ->

    match LockFile.tryRead deps.FileSystem input.YamlPath with
    | None -> Error "No lock file found — run 'orcai run' first."
    | Some lock ->

    let client = deps.GhClient
    let assignClient =
        match deps.CopilotClient, input.IsPrimaryAuthApp with
        | Some c, true -> c
        | _            -> client

    // Resolve effective nudge config: YAML wins, then global/local config, then defaults.
    let pickNudge f =
        jobConfig.Nudge |> Option.bind f
        |> Option.orElse (deps.Config.Nudge |> Option.bind f)
    let assignTo     = extractAssignee jobConfig.Action |> Option.defaultValue "@copilot"
    let nudgeMode    = pickNudge  (fun n -> n.Mode)    |> Option.defaultValue "reassign"
    let nudgeComment = pickNudge  (fun n -> n.Comment)
    let jobOwner =
        jobConfig.JobOwner
        |> Option.orElseWith (fun () ->
            let dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(input.YamlPath)) |> Option.ofObj |> Option.defaultValue "."
            Codeowners.tryReadLocal deps.FileSystem dir)

    let modeReassigns = nudgeMode = "reassign" || nudgeMode = "comment-and-reassign"

    // Pre-flight: if we'd be using the App-token client to reassign @copilot
    // we know upfront the reassign will fail. Refuse before the unassign loop
    // runs and leaves every issue detached from @copilot.
    let appCannotReassignCopilot =
        modeReassigns
        && not input.DryRun
        && assignTo.Equals("@copilot", StringComparison.OrdinalIgnoreCase)
        && input.IsPrimaryAuthApp
        && deps.CopilotClient.IsNone

    if appCannotReassignCopilot then
        Error "Cannot reassign @copilot: primary auth is a GitHub App and no PAT is configured. \
               Set ORCAI_PAT (CI) or run 'orcai auth pat --token <PAT>' (local). \
               Refusing to unassign @copilot when reassignment would fail."
    else

    let semaphore = new System.Threading.SemaphoreSlim(max 1 input.MaxConcurrency)
    let results =
        lock.Issues
        |> List.map (fun issue ->
            async {
                do! semaphore.WaitAsync() |> Async.AwaitTask
                try
                    let (RepoName repoStr) = issue.Repo

                    let hasPrInLock =
                        lock.PullRequests
                        |> List.exists (fun pr -> pr.Repo = issue.Repo && pr.ClosesIssue = issue.Number && pr.State = "OPEN")

                    if hasPrInLock then
                        if input.Verbose then eprintfn "[%s] PR already in lock file, skipping" repoStr
                        return { Repo = issue.Repo; Issue = issue.Number; Outcome = Skipped; LivePrs = [] }
                    else
                        let! prs = client.FindPrsForIssue issue.Repo issue.Number
                        let openOrMergedPrs = prs |> List.filter (fun pr -> pr.State = "OPEN" || pr.State = "MERGED")
                        let closedPrs       = prs |> List.filter (fun pr -> pr.State = "CLOSED")

                        if not (List.isEmpty openOrMergedPrs) then
                            if input.Verbose then eprintfn "[%s] PR found on GitHub, no nudge needed" repoStr
                            return { Repo = issue.Repo; Issue = issue.Number; Outcome = PrFoundLive; LivePrs = openOrMergedPrs }
                        else

                        // Compute an early-exit result when only closed PRs exist.
                        // None means "proceed to nudge" (Nudge action, or no PRs at all).
                        let closedPrExit =
                            if not (List.isEmpty closedPrs) then
                                match input.OnClosedPr with
                                | ClosedPrAction.Skip ->
                                    if input.Verbose then eprintfn "[%s] Closed PR found, skipping (--on-closed-pr skip)" repoStr
                                    Some { Repo = issue.Repo; Issue = issue.Number; Outcome = SkippedClosedPr; LivePrs = closedPrs }
                                | ClosedPrAction.Fail ->
                                    Some { Repo = issue.Repo; Issue = issue.Number; Outcome = NudgeFailed "closed PR exists (use --on-closed-pr nudge to re-trigger)"; LivePrs = [] }
                                | ClosedPrAction.Nudge ->
                                    None
                            else None

                        match closedPrExit with
                        | Some result -> return result
                        | None ->

                        if input.DryRun then
                            if input.Verbose then eprintfn "[%s] DRY RUN: would nudge %s" repoStr assignTo
                            return { Repo = issue.Repo; Issue = issue.Number; Outcome = DryRunWouldNudge; LivePrs = [] }
                        else
                            // Post nudge comment when mode includes comment
                            if nudgeMode = "comment-only" || nudgeMode = "comment-and-reassign" then
                                match nudgeComment with
                                | Some tmpl ->
                                    do! Comments.postTemplatedComment client issue.Repo issue.Number assignTo jobOwner tmpl input.Verbose "nudge" Map.empty
                                | None -> ()

                            // Unassign + reassign when mode includes reassign.
                            // Capture failures so the result reflects what actually happened.
                            let mutable failure : string option = None
                            if modeReassigns then
                                if input.Verbose then eprintfn "[%s] Nudging %s (unassign + reassign)" repoStr assignTo
                                match! assignClient.UnassignIssue issue.Repo issue.Number assignTo with
                                | Error e -> failure <- Some (nudgeFailureMessage assignTo e)
                                | Ok ()   -> ()
                                if failure.IsNone then
                                    match! assignClient.AssignIssue issue.Repo issue.Number assignTo with
                                    | Error e -> failure <- Some (nudgeFailureMessage assignTo e)
                                    | Ok ()   -> ()

                            let outcome =
                                match failure with
                                | Some reason -> NudgeFailed reason
                                | None        -> NudgeSent
                            return { Repo = issue.Repo; Issue = issue.Number; Outcome = outcome; LivePrs = [] }
                finally
                    semaphore.Release() |> ignore
            })
        |> Async.Parallel
        |> Async.RunSynchronously
        |> Array.toList

    if input.SaveLock then
        let newPrs = results |> List.collect (fun r -> r.LivePrs)
        if not (List.isEmpty newPrs) then
            let updatedLock = { lock with PullRequests = lock.PullRequests @ newPrs }
            LockFile.write deps.FileSystem input.YamlPath updatedLock

    Ok results
