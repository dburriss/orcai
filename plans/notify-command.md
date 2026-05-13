# Plan: `notify` command

## Context

Add a new `orcai notify` command that posts a templated comment to items in the lock file. It is essentially `nudge --mode comment-only` promoted to a first-class subcommand — no reassignment logic.

The command defaults to notifying open issues in the lock file, with optional filters for target type (issues, PRs, or both) and GitHub state (open, closed, or all).

The comment-building logic (build template vars → render → PostComment) is currently duplicated in `NudgeCommand.fs` and `RunCommand.fs`. Extracting it to a shared `Comments.fs` module is the right refactor before adding a third consumer.

---

## What `notify` does

1. Parses the YAML config (same as `nudge`)
2. Reads the lock file — errors if missing
3. Resolves target items from the lock file based on `--target` flag:
   - `issues` (default): `lock.Issues`
   - `prs`: `lock.PullRequests`
   - `both`: `lock.Issues` + `lock.PullRequests`
4. For each item, optionally filters by live GitHub state (`--state open|closed|all`):
   - `open` (default): fetch live state via `IGhClient`; only notify open items
   - `closed`: fetch live state via `IGhClient`; only notify closed items
   - `all`: no live check, proceed immediately
5. For each remaining item:
   - If `--dry-run` → `DryRunWouldNotify`
   - Else posts the templated comment → `Notified`
6. Outputs a Spectre.Console table (same style as nudge)

---

## Flags

```
orcai notify <yaml-file> [--target issues|prs|both] [--state all|open|closed]
                         [--dry-run] [--save-lock] [--verbose]
```

| Flag | Default | Description |
|---|---|---|
| `--target` | `issues` | Which lock file items to notify: `issues`, `prs`, or `both` |
| `--state` | `open` | Filter by current GitHub state: `all`, `open`, or `closed` |
| `--dry-run` | off | Preview without posting |
| `--save-lock` | off | Write discovered PRs back to lock file (inherited from nudge) |
| `--verbose` | off | Verbose output |

---

## Config

New `notify:` YAML section with a single `comment` field (no mode — always comment-only):

```yaml
notify:
  comment: "Hey {assignee}, this issue still needs attention. CC {job.owner}."
```

Same template variables as nudge: `{assignee}`, `{job.owner}`, `{repo.codeowners}`.

Config precedence: YAML job config wins over global/local `OrcAIConfig`, same pattern as nudge.

---

## Files to change

### New files

| File | Purpose |
|---|---|
| `src/OrcAI.Core/Comments.fs` | Shared `buildCommentVars` and `postTemplatedComment` helpers |
| `src/OrcAI.Core/NotifyCommand.fs` | `notify` command logic |
| `tests/OrcAI.Core.Tests/NotifyCommandTests.fs` | Unit tests |

### Modified files

| File | Change |
|---|---|
| `src/OrcAI.Core/Domain.fs` | Add `NotifyConfig = { Comment: string option }` and `Notify: NotifyConfig option` to `JobConfig` |
| `src/OrcAI.Core/GhClient.fs` | Add `GetIssueState` and `GetPrState` to `IGhClient` |
| `src/OrcAI.Core/YamlConfig.fs` | Add `YamlNotify` DTO, `notify` field on `YamlRoot`, parse `notifyConfig`, set `Notify` in `JobConfig` |
| `src/OrcAI.Core/OrcAIConfig.fs` | Add `Notify: NotifyConfig option` to `OrcAIConfig`, `NotifyConfigDto`, merge, ofDto |
| `src/OrcAI.Core/NudgeCommand.fs` | Replace inline comment block (lines 89–104) with call to `Comments.postTemplatedComment` |
| `src/OrcAI.Core/RunCommand.fs` | Replace inline comment block (lines 196–208) with call to `Comments.postTemplatedComment` |
| `src/OrcAI.Core/OrcAI.Core.fsproj` | Add `Comments.fs` (after `GhClient.fs`) and `NotifyCommand.fs` (after `NudgeCommand.fs`) |
| `src/OrcAI.GitHub/GhClient.fs` | Implement `GetIssueState` and `GetPrState` via `gh issue view` / `gh pr view` |
| `src/OrcAI.Tool/Args.fs` | Add `NotifyArgs` DU; add `Notify` case to `OrcAIArgs` |
| `src/OrcAI.Tool/Program.fs` | Add `| Notify args ->` dispatch arm |
| `tests/OrcAI.Core.Tests/FakeGhClient.fs` | Add stub implementations of `GetIssueState` / `GetPrState` |
| `tests/OrcAI.Core.Tests/OrcAI.Core.Tests.fsproj` | Add `NotifyCommandTests.fs` |

---

## Key implementation details

### New `IGhClient` methods

```fsharp
// Returns "OPEN" | "CLOSED" or None if not found
abstract GetIssueState : repo:RepoName -> issue:IssueNumber -> Async<string option>
// Returns "OPEN" | "CLOSED" | "MERGED" or None if not found
abstract GetPrState    : repo:RepoName -> pr:PrNumber       -> Async<string option>
```

Implemented via:
- `gh issue view {n} --repo {r} --json state --jq '.state'`
- `gh pr view {n} --repo {r} --json state --jq '.state'`

State filtering treats `--state closed` as matching `"CLOSED"` and `"MERGED"` for PRs.

### `Comments.fs` (after `GhClient.fs` in compile order)

```fsharp
module OrcAI.Core.Comments

open OrcAI.Core.Domain
open OrcAI.Core.GhClient

let buildCommentVars (assignTo: string) (jobOwner: string option) (repoOwners: string option) : Map<string, string> =
    [ "assignee", assignTo
      yield! jobOwner   |> Option.map (fun v -> "job.owner",       v) |> Option.toList
      yield! repoOwners |> Option.map (fun v -> "repo.codeowners", v) |> Option.toList ]
    |> Map.ofList

let postTemplatedComment (client: IGhClient) (repo: RepoName) (issue: IssueNumber)
        (assignTo: string) (jobOwner: string option) (template: string) (verbose: bool) (label: string) : Async<unit> =
    async {
        let (RepoName repoStr) = repo
        let! codeownersContent = client.FetchCodeowners repo
        let repoOwners         = codeownersContent |> Option.bind Codeowners.parseCatchAll
        let vars               = buildCommentVars assignTo jobOwner repoOwners
        let body               = renderTemplate vars template
        if verbose then eprintfn "[%s] Posting %s comment" repoStr label
        match! client.PostComment repo issue body with
        | Error e -> eprintfn "[%s] Warning: failed to post %s comment: %s" repoStr label e
        | Ok ()   -> ()
    }
```

### `NotifyCommand.fs`

- Input: `{ YamlPath; DryRun; Verbose; SaveLock; Target; State }`
  - `Target`: `"issues"` (default) | `"prs"` | `"both"`
  - `State`: `"open"` (default) | `"all"` | `"closed"`
- Outcomes: `Skipped | Notified | DryRunWouldNotify`
- Config pick: YAML `jobConfig.Notify` wins over `deps.Config.Notify`
- Posts comment via `Comments.postTemplatedComment`
- Does NOT need `IsPrimaryAuthApp` (no assignment)
- PRs: unwrap `PrNumber` → use as `IssueNumber` for `PostComment` (GitHub's comment API treats PRs as issues)
- State `"all"`: skip live state fetch entirely (no extra API calls); `"open"` / `"closed"` fetch state live

### `NotifyArgs`

```fsharp
type NotifyArgs =
    | [<MainCommand; Mandatory>] Yaml_File of path: string
    | Dry_Run
    | Save_Lock
    | Verbose
    | Target of target: string   // "issues" | "prs" | "both"  (default: "issues")
    | State  of state:  string   // "open" | "all" | "closed"  (default: "open")
```

---

## Verification

```bash
# Build
dotnet build OrcAI.sln

# Tests
dotnet test tests/OrcAI.Core.Tests/OrcAI.Core.Tests.fsproj

# Manual dry-run (default: open issues)
orcai notify my-job.yml --dry-run --verbose

# All issues regardless of state
orcai notify my-job.yml --state all --dry-run

# Open PRs
orcai notify my-job.yml --target prs --state open --dry-run

# Both issues and PRs, closed
orcai notify my-job.yml --target both --state closed --dry-run
```
