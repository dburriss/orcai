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
    | Ok jobConfig ->

    match LockFile.tryRead deps.FileSystem input.YamlPath with
    | None -> Error "No lock file found — run 'orcai run' first."
    | Some lock ->

    let client = deps.GhClient
    let assignClient =
        match deps.CopilotClient, input.IsPrimaryAuthApp with
        | Some c, true -> c
        | _            -> client

    // Resolve effective assign/nudge config: YAML wins, then global/local config, then defaults.
    let pickAssign f =
        jobConfig.Assign |> Option.bind f
        |> Option.orElse (deps.Config.Assign |> Option.bind f)
    let pickNudge f =
        jobConfig.Nudge |> Option.bind f
        |> Option.orElse (deps.Config.Nudge |> Option.bind f)
    let assignTo     = pickAssign (fun a -> a.To)      |> Option.defaultValue "@copilot"
    let nudgeMode    = pickNudge  (fun n -> n.Mode)    |> Option.defaultValue "reassign"
    let nudgeComment = pickNudge  (fun n -> n.Comment)

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
                            if input.Verbose then eprintfn "[%s] DRY RUN: would nudge %s" repoStr assignTo
                            return { Repo = issue.Repo; Issue = issue.Number; Outcome = DryRunWouldNudge; LivePrs = [] }
                        else
                            // Post nudge comment when mode includes comment
                            if nudgeMode = "comment-only" || nudgeMode = "comment-and-reassign" then
                                match nudgeComment with
                                | Some tmpl ->
                                    let body = tmpl.Replace("{assignee}", assignTo)
                                    if input.Verbose then eprintfn "[%s] Posting nudge comment" repoStr
                                    match! client.PostComment issue.Repo issue.Number body with
                                    | Error e -> eprintfn "[%s] Warning: failed to post nudge comment: %s" repoStr e
                                    | Ok ()   -> ()
                                | None -> ()

                            // Unassign + reassign when mode includes reassign
                            if nudgeMode = "reassign" || nudgeMode = "comment-and-reassign" then
                                if input.Verbose then eprintfn "[%s] Nudging %s (unassign + reassign)" repoStr assignTo
                                match! assignClient.UnassignIssue issue.Repo issue.Number assignTo with
                                | Error e -> eprintfn "[%s] Warning: failed to unassign %s: %s" repoStr assignTo e
                                | Ok ()   -> ()
                                match! assignClient.AssignIssue issue.Repo issue.Number assignTo with
                                | Error e -> eprintfn "[%s] Warning: failed to reassign %s: %s" repoStr assignTo e
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
