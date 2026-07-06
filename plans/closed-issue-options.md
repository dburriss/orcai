# Plan: `--on-closed-issue` Flag

> **Amended 2026-06-23:** The original plan set the default to `create` to preserve existing behaviour. After implementation it was decided the correct default is `skip` — a closed issue with a matching title should be treated as already done unless the caller explicitly opts in to `create` or `reopen`. As part of this correction, the `redoOnClosed` YAML field and config option (which had been added to work around the wrong default for checkout actions) were removed entirely. The implementation is now simpler: the default is `skip` for all action types, and users who want the old behaviour set `onClosedIssue: create`.

## Context

`FindIssue` only searches open issues. When a closed issue exists with a matching title and no valid lock file, OrcAI currently creates a duplicate. This plan adds a `--on-closed-issue` flag (and matching YAML field) to give users explicit control over what happens in that scenario.

---

## Flag

```
--on-closed-issue <create|reopen|skip|fail>
```

Also settable in YAML as `onClosedIssue: reopen` (consistent with `skipCopilot`).

**Default: `create`** — preserves today's behavior, no surprises on upgrade.

---

## Behaviors

| Value | What happens when a closed issue with a matching title is found |
|---|---|
| `create` (default) | Ignore it; create a new open issue as before |
| `reopen` | Reopen the closed issue; continue (add to project, assign copilot) |
| `skip` | Treat it as already done; do not create, reopen, or act on it |
| `fail` | Log an error and skip the repo; no lock file written if any repo fails |

---

## New Types and Values

### `ClosedIssueAction` (new, in `Domain.fs`)
```fsharp
type ClosedIssueAction = | Create | Reopen | Skip | Fail
```

### `IssueOutcome` additions
```fsharp
type IssueOutcome = | Created | AlreadyExisted | Reopened | Skipped
```
- `Reopened` — closed issue was reopened
- `Skipped` — closed issue found, action was `skip`; issue ref is the closed one
- `Fail` does not need an outcome variant — `processRepo` returns `None` (same as any other error)

---

## Files to Change

| File | What changes |
|---|---|
| `src/OrcAI.Core/Domain.fs` | Add `ClosedIssueAction` type |
| `src/OrcAI.Core/GhClient.fs` | Add `FindClosedIssue` and `ReopenIssue` to `IGhClient` |
| `src/OrcAI.GitHub/GhClient.fs` | Implement both new methods |
| `src/OrcAI.Core/Config.fs` | Add `OnClosedIssue: ClosedIssueAction` field to `JobConfig` (default `Create`) |
| `src/OrcAI.Core/RunCommand.fs` | Add `Reopened`/`Skipped` to `IssueOutcome`; thread `ClosedIssueAction` through `processRepo` |
| `src/OrcAI.Tool/Program.fs` | Parse `--on-closed-issue` CLI flag; handle new outcomes in display |
| `tests/OrcAI.Core.Tests/RunCommandTests.fs` | Stub new interface members; add tests for each action |

---

## Step 1 — `src/OrcAI.Core/Domain.fs`

Add new type:
```fsharp
type ClosedIssueAction = | Create | Reopen | Skip | Fail
```

---

## Step 2 — `src/OrcAI.Core/GhClient.fs`

Add to `IGhClient` after `FindIssue` (line 25):
```fsharp
abstract FindClosedIssue : repo:RepoName -> title:string -> Async<IssueRef option>
abstract ReopenIssue     : repo:RepoName -> issue:IssueNumber -> Async<Result<IssueRef, string>>
```

---

## Step 3 — `src/OrcAI.GitHub/GhClient.fs`

**`FindClosedIssueImpl`** — identical to `FindIssueImpl` but with `--state closed`:
```
gh issue list --repo <org/repo> --state closed --json title,number,url,assignees
```

**`ReopenIssueImpl`** — two calls:
1. `gh issue reopen <number> --repo <org/repo>` — exits 0 on success; plain-text output, ignored
2. `gh issue view <number> --repo <org/repo> --json number,url,assignees` — reconstruct `IssueRef` with fresh assignees

Wire both into the interface dispatch block.

---

## Step 4 — `src/OrcAI.Core/Config.fs`

Add `OnClosedIssue` to `JobConfig` with default `Create`:
```fsharp
OnClosedIssue : ClosedIssueAction  // default: Create
```

Add YAML deserialization mapping for `onClosedIssue` string values → `ClosedIssueAction`.

---

## Step 5 — `src/OrcAI.Core/RunCommand.fs`

**Extend union (line 38):**
```fsharp
type IssueOutcome = | Created | AlreadyExisted | Reopened | Skipped
```

**Thread `ClosedIssueAction` into `processRepo`** — add parameter `closedIssueAction: ClosedIssueAction`.

**Extend find/create block** — after `FindIssue` returns `None`:
```fsharp
| None ->
    let! closedOpt = client.FindClosedIssue repo config.IssueTitle
    match closedOpt with
    | None ->
        let! result = client.CreateIssue repo config.IssueTitle config.IssueBody config.Labels
        return result |> Result.map (fun issue -> (issue, Created))
    | Some closed ->
        match closedIssueAction with
        | Create ->
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
```

**Note on `Skip`:** downstream steps (add to project, assign copilot) must be bypassed for the `Skipped` outcome. Lock file records the closed issue ref.

---

## Step 6 — `src/OrcAI.Tool/Program.fs`

**Parse CLI flag** — add `--on-closed-issue` option to the `run` command parser, mapping string → `ClosedIssueAction` (default `Create`). CLI flag overrides the YAML config value if explicitly passed.

**Handle new outcomes in 3 display sites:**

- JSON per-issue status: add `"reopened"` and `"skipped"` cases
- JSON summary counts: add `reopened` and `skipped` counters (additive, non-breaking)
- Spectre table status column: `"[yellow]reopened[/]"` and `"[grey]skipped[/]"`

Update plain-text summary line to include reopened/skipped counts when non-zero.

---

## Step 7 — `tests/OrcAI.Core.Tests/RunCommandTests.fs`

**Stubs on existing fakes** (compile fix):
```fsharp
member _.FindClosedIssue _ _ = async { return None }
member _.ReopenIssue _ _     = notImpl "ReopenIssue"
```

**New fake clients** for each non-`create` action path:
- `ReopeningGhClient` — `FindClosedIssue` returns `Some`; `ReopenIssue` returns `Ok`; `CreateIssue` throws
- `SkippingGhClient` — same setup; verifies `AddIssueToProject` / `AssignIssue` are NOT called
- `FailingClosedGhClient` — `FindClosedIssue` returns `Some`; verifies `processRepo` returns `None`

**New tests:**
1. `reopen action reopens closed issue and returns Reopened outcome`
2. `reopen action does not call CreateIssue when closed issue exists`
3. `skip action returns Skipped outcome without creating or reopening`
4. `skip action does not add issue to project or assign copilot`
5. `fail action returns error and does not create or reopen`
6. `create action (default) creates new issue even when closed issue exists`

---

## Verification

1. `dotnet build` — compiler flags unhandled match arms for `Reopened`/`Skipped`
2. `dotnet test` — all existing tests pass; new tests pass
3. Manual smoke tests:
   - Create issue via OrcAI, close it on GitHub, delete lock file
   - Re-run with each `--on-closed-issue` value and verify expected behavior
   - Verify `create` (default) behavior is unchanged from today
