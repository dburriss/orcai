# Graceful failure handling in `orcai run`

## Context

The `coolblue-development/marge` GitHub Actions run dated 2026-05-18 ([run 26037920400](https://github.com/coolblue-development/marge/actions/runs/26037920400/job/76541125707)) completed across 152 repos with 84 already-existing issues, but produced large amounts of `Error ...` noise in the log for cases that are not really errors. The job overall passed because of `--continue-on-error`, but the stderr is hard to scan and several failures are recoverable.

The four classes of failure observed, and the chosen handling:

| # | Failure | Cause | Handling |
|---|---|---|---|
| 1 | `GraphQL: Could not resolve to an issue or pull request with the number of N` on `gh issue edit` | Lock points to a deleted/transferred issue | Drop from lock, recreate in-place |
| 2 | `Repository was archived so is read-only (updateIssue)` on `gh issue edit` | Repo archived since last run | Pre-check + skip + record `skippedRepos` in lock |
| 3 | `HTTP 403: Repository was archived so is read-only.` on `gh label create` | Same as #2 — archived repo | Covered by archived pre-check in #2 |
| 4 | `label with name "marge" already exists` on `gh label create` | `gh label list` defaults to 30 results — labels past page 1 are missed by the pre-check | Fix pagination + make `CreateLabel` idempotent |
| 5 | `could not add label: 'marge' not found` on `gh issue create` | Cascade from #3/#4 | Goes away once #2 and #4 are fixed |

Plus: per-repo per-job failures currently log as `eprintfn "[repo] Error ..."` and are tallied only as an anonymous integer at `RunCommand.fs:288`. Add a structured end-of-run summary that categorises succeeded / skipped-archived / stale-recreated / failed.

## Critical files

- `src/OrcAI.Core/GhClient.fs` — `IGhClient` interface
- `src/OrcAI.GitHub/GhClient.fs` — production gh CLI implementation (lines 296, 310, 380, 470)
- `src/OrcAI.Core/RunCommand.fs` — per-repo orchestration (lines 40, 78, 110, 169, 281, 307, 328)
- `src/OrcAI.Core/Domain.fs` — `LockFile` and `IssueRef` records
- `src/OrcAI.Core/LockFile.fs` — JSON DTO layer
- `src/OrcAI.Tool/Program.fs` — CLI summary output (lines 230, 352)
- `tests/OrcAI.Core.Tests/FakeGhClient.fs` — test fake
- `tests/OrcAI.Core.Tests/RunCommandTests.fs`, `LockFileTests.fs` — tests

## Implementation

### Step 1 — Fix `gh label list` pagination (one-line, lands first)

`src/OrcAI.GitHub/GhClient.fs:296` — change `gh label list --repo {repoStr} --json name` to `gh label list --repo {repoStr} --json name --limit 1000`. Matches the `ListRepos` convention at line 459. No interface change, no new tests required.

### Step 2 — Idempotent `CreateLabel`

`src/OrcAI.GitHub/GhClient.fs:307–313` — pattern-match the gh stderr for `already been taken` / `already exists` (case-insensitive) and downgrade to `Ok ()` with a `logger.LogWarning`. Mirrors the precedent at `DeleteIssue` (line 394) and `ClosePr` (line 448).

Test (`RunCommandTests.fs`): fake `CreateLabel` returns `Error "Name has already been taken"`; assert `ensureLabelsExist` returns `Ok ()` and `execute` succeeds.

### Step 3 — Add `IsArchived` to `IGhClient`

Add a new method (alongside existing `RepoExists`, not replacing it — keeps `ValidateCommand.fs` and other callers untouched):

```fsharp
abstract IsArchived : repo:RepoName -> Async<Result<bool, string>>
```

Production impl in `src/OrcAI.GitHub/GhClient.fs`: `gh repo view {repoStr} --json isArchived`, parse `isArchived` boolean. Error path returns `Error e` (e.g. repo doesn't exist or auth failure).

Update `FakeGhClient.fs` to add an `IsArchived` handler with default `Ok false`.

### Step 4 — Archived-repo pre-check + lock recording

`src/OrcAI.Core/RunCommand.fs`:

1. Add to `IssueOutcome` (line 40): `SkippedArchived`.
2. Add a placeholder helper for the archived case (no real issue exists):
   ```fsharp
   let private archivedPlaceholder (repo: RepoName) : IssueRef =
       let (RepoName r) = repo
       { Repo = repo; Number = IssueNumber 0; Url = $"https://github.com/{r}"; Assignees = [] }
   ```
3. In `processRepo` (line 125, before step 0), call `client.IsArchived repo`:
   - `Ok true` → `eprintfn "[repo] Repo is archived — skipping."` then `return Some { Issue = archivedPlaceholder repo; Outcome = SkippedArchived }`.
   - `Ok false` or `Error _` → proceed (non-fatal; archive errors will still be caught downstream).
4. In `runFull` (lines 287–301):
   - `successes` should include `SkippedArchived` as recorded repos but exclude them when building `lock.Issues`.
   - Add `lock.SkippedRepos` (new field, see step 5).

### Step 5 — Lock file: additive `skippedRepos` field

`src/OrcAI.Core/Domain.fs` — extend `LockFile`:
```fsharp
type LockFile =
    { ...
      SkippedRepos : RepoName list }   // new — archived repos skipped this run
```

`src/OrcAI.Core/LockFile.fs` — extend `LockFileDto` with `[<JsonPropertyName("skippedRepos")>] skippedRepos: string[]`. In `ofDto`, null-guard the same way `templateHash` is guarded at line 104 (`if isNull dto.skippedRepos then [] else ...`). In `toDto`, serialise from `lock.SkippedRepos`.

Tests (`LockFileTests.fs`): round-trip; old lock JSON without `skippedRepos` still deserialises (use a raw JSON literal).

### Step 6 — Stale-issue detection + recovery

`src/OrcAI.Core/RunCommand.fs`:

1. Add to `IssueOutcome`: `StaleIssueRecreated`.
2. Add helper:
   ```fsharp
   let private isStaleIssue (e: string) =
       e.Contains("Could not resolve to an issue", StringComparison.OrdinalIgnoreCase)
   ```
3. Extract the per-job parameter resolution from `runFull` (assignTo / assignVia / assignComment / jobOwner / closedIssueAction / skipCopilot, lines 268–280) into a private helper `resolveProcessParams : OrcAIDeps -> RunInput -> JobConfig -> ProcessParams`. This is so the stale recovery path and `runFull` share the same logic.
4. In `applyBodyUpdates` (line 318): when the error matches `isStaleIssue`, emit `eprintfn "[repo] Stale issue #N — will recreate."` and return `{ Outcome = StaleIssueRecreated; ... }` (carry the old `IssueRef` so the caller knows which repo to recover).
5. In `updateBodies` (line 336) and the `Both changed` branch of `executeSingle` (line 392–409): after `applyBodyUpdates`, identify entries with `Outcome = StaleIssueRecreated`, then for each such repo invoke `processRepo` (using the same resolved params and the project from the existing lock) — the recreate path inside `processRepo` will call `FindIssue` (returns None since the old issue is gone), then `CreateIssue`. Replace the stale entry in the result list with the recreate outcome (now `Created`, falling back to `UpdateFailed` if recreate also fails). Drop stale `IssueRef`s from the rewritten lock; add the new ones.

Tests (`RunCommandTests.fs`):
- `applyBodyUpdates` returns `StaleIssueRecreated` when `UpdateIssue` returns the stale error.
- `updateBodies` recovery: after stale detection, `CreateIssue` is called for that repo and the new `IssueRef` is in `lock.Issues`; the old number is gone.
- Non-stale repos are unaffected when one is stale.
- Recreate failure surfaces as `UpdateFailed` for that repo without blocking others.

### Step 7 — Structured end-of-run summary

`src/OrcAI.Tool/Program.fs`:

1. Extend the human-readable summary (lines 362–373) to include archived and stale counts:
   ```
   ✓ N/M repos succeeded (N created, N already existed, N skipped-archived, N stale-recreated, N failed)
   ```
2. Extend the Spectre verbose table (lines 381–397): add `SkippedArchived` → `"[grey]skipped (archived)[/]"` and `StaleIssueRecreated` → `"[yellow]stale — recreated[/]"`. When the issue number is `0` (archived placeholder), render `[dim]-[/]` instead of `#0`.
3. Extend `printRunJsonMulti` (lines 230–249) with the two new outcomes as JSON status strings `"skippedArchived"` and `"staleIssueRecreated"`, plus aggregated counts.

No new tests for `Program.fs` (display only) — covered by manual verification.

## Commit order

1. `--limit 1000` for `gh label list` (one-line, no interface change)
2. Idempotent `CreateLabel`
3. `IsArchived` interface + fake + production impl
4. Archived pre-check in `processRepo` + `SkippedArchived` outcome + lock `SkippedRepos` field
5. Stale-issue detection + `resolveProcessParams` extraction + recovery in `updateBodies` and `Both changed`
6. `Program.fs` summary output

Each commit compiles and tests green.

## Verification

**Unit tests** — run `dotnet test`:
- `CreateLabel` idempotency: fake returns "already taken" error → run succeeds.
- `processRepo` skips archived: fake `IsArchived = Ok true` → outcome `SkippedArchived`.
- Lock round-trip preserves `SkippedRepos`; old locks without the field still parse.
- `applyBodyUpdates` returns `StaleIssueRecreated` on the matching gh stderr.
- `updateBodies` recovery: after stale detection, `CreateIssue` is invoked and new `IssueRef` is in the rewritten lock.

**Manual end-to-end** against a test org:
- Archived: add an archived repo to `job.yml`, run `dotnet orcai run --auto-create-labels job.yml --verbose` → log shows "Repo is archived — skipping"; lock file `skippedRepos` contains the repo.
- Stale: run once to populate lock, manually delete one issue on GitHub, edit the template (forces `updateBodies` path), re-run → log shows "Stale issue — will recreate"; lock now has the new issue number.
- Label idempotency: pre-create the label in a test repo, run with `--auto-create-labels` → no error, no log noise.
- Label pagination: in a test repo with >30 labels (e.g. add 35 throwaway labels), put a post-page-1 label in the YAML → `ListLabels` now returns it and `CreateLabel` is not called.

**Replay against marge**: rerun the marge workflow against the same set of 152 repos. Expected stderr should contain only the archived-skip and stale-recreate informational lines (no `Error finding/creating issue`, no `could not add label`, no `Repository was archived`-prefixed errors). The final summary line should list non-zero counts for `skipped-archived` and `stale-recreated` instead of opaque error tallies.
