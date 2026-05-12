# Plan: `orcai nudge` command

## Context

When `orcai run` assigns `@copilot` to issues, Copilot sometimes doesn't start working and never creates a PR. The current manual fix is to unassign then reassign Copilot. This plan implements `orcai nudge` to automate that.

The lock file (`<yaml>.lock.json`) records `pullRequests` populated via `orcai info --save-lock`. If an issue already has a PR entry in the lock file, we can skip the live check entirely — Copilot delivered. Only issues with no PR in the lock file need a live `FindPrsForIssue` call; if that also returns nothing, we nudge by unassigning then reassigning `@copilot`.

---

## Approach

**New command:** `orcai nudge <yaml-file> [--dry-run] [--save-lock] [--verbose]`

Algorithm per issue in lock file:
1. If issue already has a PR in `lock.PullRequests` → **skip** (no GitHub call)
2. Otherwise → call `FindPrsForIssue` live
3. If PR found live → no action; if `--save-lock`, record the PR in the lock file
4. If no PR found → nudge: `UnassignIssue @copilot`, then `AssignIssue @copilot`

After processing all issues, if `--save-lock` and any new PRs were discovered, write an updated lock file (same pattern as `orcai info --save-lock`).

Requires a lock file. If none exists, error with: _"No lock file found — run `orcai run` first."_

Uses `CopilotClient` (PAT-based) when primary auth is a GitHub App, same pattern as `RunCommand`.

---

## Files to change

### 1. `src/OrcAI.Core/GhClient.fs`
Add to `IGhClient` interface (after `AssignIssue`):
```fsharp
abstract UnassignIssue : repo:RepoName -> issue:IssueNumber -> assignee:string -> Async<Result<unit, string>>
```

### 2. `src/OrcAI.GitHub/GhClient.fs`
Add implementation after `AssignIssue` (~line 403):
```fsharp
member _.UnassignIssue repo issue assignee =
    async {
        let (RepoName repoStr)   = repo
        let (IssueNumber issueN) = issue
        match! runGhWrite bucket retries ghToken $"issue edit {issueN} --repo {repoStr} --remove-assignee {assignee}" with
        | Error e -> return Error e
        | Ok _    -> return Ok ()
    }
```

### 3. `src/OrcAI.Core/NudgeCommand.fs` (new file)
```fsharp
module OrcAI.Core.NudgeCommand

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
```

### 4. `src/OrcAI.Core/OrcAI.Core.fsproj`
Add `NudgeCommand.fs` after `InfoCommand.fs`:
```xml
<Compile Include="NudgeCommand.fs" />
```

### 5. `src/OrcAI.Tool/Args.fs`
Add `NudgeArgs` and `Nudge` to `OrcAIArgs`:
```fsharp
[<CliPrefix(CliPrefix.DoubleDash)>]
type NudgeArgs =
    | [<MainCommand; Mandatory>] Yaml_File of path: string
    | Dry_Run
    | Save_Lock
    | Verbose
    interface IArgParserTemplate with
        member a.Usage =
            match a with
            | Yaml_File _ -> "Path to the YAML job configuration file."
            | Dry_Run     -> "Preview which issues would be nudged without making any changes."
            | Save_Lock   -> "Write discovered PRs back to the lock file."
            | Verbose     -> "Enable verbose output."
```

Add `| [<SubCommand>] Nudge of ParseResults<NudgeArgs>` to `OrcAIArgs` and its `Usage` match arm.

### 6. `src/OrcAI.Tool/Program.fs`
Add a `Nudge args` case in the `main` match block, mirroring the `Info` case pattern. Wire up `--save-lock` from `NudgeArgs.Save_Lock`. Print a results table using Spectre.Console showing repo, issue number, and outcome.

---

## Verification

1. `dotnet build` — ensure everything compiles
2. `orcai nudge jobs/my-upgrade.yml --dry-run --verbose` — verify it reads lock file, calls `FindPrsForIssue` for issues without lock-file PRs, prints what it would do
3. `orcai nudge jobs/my-upgrade.yml` on a stuck issue — confirm it unassigns then reassigns Copilot
4. `orcai nudge jobs/my-upgrade.yml` when all issues have PRs in lock file — confirm zero network calls, all `Skipped`
5. `orcai nudge jobs/my-upgrade.yml --save-lock` when live PRs are found — confirm lock file is updated with the new PRs
6. `orcai nudge nonexistent.lock.yml` — confirm error message
