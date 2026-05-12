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

open OrcAI.Core.Domain
open OrcAI.Core.GhClient
open OrcAI.Core.Deps

type NudgeInput =
    { YamlPath         : string
      DryRun           : bool
      Verbose          : bool
      SaveLock         : bool
      IsPrimaryAuthApp : bool }

type NudgeOutcome = | Skipped | PrFoundLive | NudgeSent | DryRunWouldNudge

type NudgeResult =
    { Repo    : RepoName
      Issue   : IssueNumber
      Outcome : NudgeOutcome
      LivePrs : PullRequestRef list }

let execute (deps: OrcAIDeps) (input: NudgeInput) : Result<NudgeResult list, string> =
    match YamlConfig.parseFile deps.FileSystem input.YamlPath with
    | Error e -> Error e
    | Ok _ ->

    match LockFile.tryRead deps.FileSystem input.YamlPath with
    | None -> Error "No lock file found — run 'orcai run' first."
    | Some lock ->

    let client = deps.GhClient
    let assignClient =
        match deps.CopilotClient, input.IsPrimaryAuthApp with
        | Some c, true -> c
        | _            -> client

    let results =
        lock.Issues
        |> List.map (fun issue ->
            async {
                let (RepoName repoStr) = issue.Repo

                let hasPrInLock =
                    lock.PullRequests
                    |> List.exists (fun pr -> pr.Repo = issue.Repo && pr.ClosesIssue = issue.Number)

                if hasPrInLock then
                    if input.Verbose then eprintfn "[%s] PR already in lock file, skipping" repoStr
                    return { Repo = issue.Repo; Issue = issue.Number; Outcome = Skipped; LivePrs = [] }
                else
                    let! prs = client.FindPrsForIssue issue.Repo issue.Number
                    if not (List.isEmpty prs) then
                        if input.Verbose then eprintfn "[%s] PR found on GitHub, no nudge needed" repoStr
                        return { Repo = issue.Repo; Issue = issue.Number; Outcome = PrFoundLive; LivePrs = prs }
                    else
                        if input.DryRun then
                            if input.Verbose then eprintfn "[%s] DRY RUN: would nudge @copilot" repoStr
                            return { Repo = issue.Repo; Issue = issue.Number; Outcome = DryRunWouldNudge; LivePrs = [] }
                        else
                            if input.Verbose then eprintfn "[%s] Nudging @copilot (unassign + reassign)" repoStr
                            match! assignClient.UnassignIssue issue.Repo issue.Number "@copilot" with
                            | Error e -> eprintfn "[%s] Warning: failed to unassign @copilot: %s" repoStr e
                            | Ok ()   -> ()
                            match! assignClient.AssignIssue issue.Repo issue.Number "@copilot" with
                            | Error e -> eprintfn "[%s] Warning: failed to reassign @copilot: %s" repoStr e
                            | Ok ()   -> ()
                            return { Repo = issue.Repo; Issue = issue.Number; Outcome = NudgeSent; LivePrs = [] }
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
