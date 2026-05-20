# Plan: Treat template hash like YAML hash — always re-run

## Context

`orcai run` currently has a four-branch dispatch
(`RunCommand.fs:536-589`):

```
both hashes match    → fast path
only YAML changed    → runFull
only template changed→ updateBodies   (problematic)
both changed         → runFull + body refresh for AlreadyExisted
no lock              → runFull
```

The `updateBodies` branch is the source of two real bugs:

1. It blindly calls `UpdateIssue` against every locked issue, silently
   rewriting the body of any issue the user has since closed.
   `OnClosedIssue` policy is honoured during `runFull` but ignored here.
2. The lock can drift (closed issue + a new open one with the same
   title) and `updateBodies` updates the wrong issue.

Separately, the `Both changed` branch filters body updates to
`AlreadyExisted` only. `ReopenIssue` (`OrcAI.GitHub/GhClient.fs:253-279`)
flips state without touching the body, so reopened issues keep the old
template body when the user just bumped it.

## Principle

A change to either the YAML *or* the template invalidates the lock the
same way. We should never mutate GitHub state from the lock-fast path —
mutations only happen after `runFull` has reconciled with live state
and we can commit a fresh lock.

## Approach

Collapse the dispatch to two branches and reuse `runFull` for all
non-fast-path work:

```
both hashes match    → fast path
anything differs     → runFull, then if lock.TemplateHash <> templateHash:
                         refresh body for AlreadyExisted + Reopened
no lock              → runFull
--skip-lock          → runFull, then refresh body for AlreadyExisted + Reopened
                       (unconditional — no prior hash to compare)
```

Why this works:

- `runFull` already handles open vs. closed correctly via
  `FindIssue` → `FindClosedIssue` → `OnClosedIssue`.
- The body-refresh step only runs against issues `runFull` just touched
  this run — `AlreadyExisted` (confirmed open) and `Reopened` (just
  reopened, body still stale). It never targets `Skipped`,
  `SkippedArchived`, `Created`, or the closed issue itself.
- The `lock.TemplateHash <> templateHash` guard avoids pointless body
  writes when the YAML changed but the template didn't (e.g. title or
  label tweak).
- `OnClosedIssue` doesn't need a second implementation. The state-aware
  precheck (`GetIssueState`) we considered is no longer needed — drop
  it from the design.

## Outcomes by repo

After the consolidated branch:

| `runFull` outcome | Template hash changed | Body refresh? | Final outcome |
|---|---|---|---|
| `Created` | yes/no | no | `Created` |
| `AlreadyExisted` | no | no | `AlreadyExisted` |
| `AlreadyExisted` | yes | yes | `Updated` |
| `Reopened` | no | no | `Reopened` |
| `Reopened` | yes | yes | `Reopened` (body refreshed) |
| `Skipped` | yes/no | no | `Skipped` |
| `SkippedArchived` | yes/no | no | `SkippedArchived` |

`Reopened` keeps its outcome rather than becoming `Updated` so the UI
can still distinguish "we reopened this" from "we just edited an open
issue".

## Files to modify

| File | Change |
|---|---|
| `src/OrcAI.Core/RunCommand.fs` | Collapse `executeSingle` dispatch to two branches. Keep an internal `refreshBodies` helper that calls `UpdateIssue` for `AlreadyExisted`/`Reopened`. Delete `updateBodies` and the parts of `applyBodyUpdates` not needed by the new helper. |
| `src/OrcAI.Core/GhClient.fs` | No change. (No new `GetIssueState`.) |
| `src/OrcAI.GitHub/GhClient.fs` | No change. |
| `tests/OrcAI.Core.Tests/RunCommandTests.fs` | Update existing template-bump tests to reflect the new dispatch. Add tests for `Reopened` body refresh and for "template changed but locked issue is closed under `skip`". |
| `tests/OrcAI.Core.Tests/FakeGhClient.fs` | No new members. |
| `docs/state-and-idempotency.md` | Replace the four-branch description with the two-branch one. |
| `CHANGELOG.md` | Entry under the unreleased section. |

## Step-by-step

### 1. Rewrite `executeSingle` dispatch

Replace `RunCommand.fs:531-589` (including the `--skip-lock` branch)
with:

```fsharp
if input.SkipLock then
    if input.Verbose then eprintfn "--skip-lock set, bypassing lock file."
    match runFull deps input mergedConfig yamlHash templateHash with
    | Error e         -> Error e
    | Ok fullResult   -> refreshBodies deps input mergedConfig fullResult
else

match LockFile.tryRead deps.FileSystem input.YamlPath with
| Some lock when lock.YamlHash = yamlHash && lock.TemplateHash = templateHash ->
    if input.Verbose then eprintfn "Lock file found and hashes match — nothing to do."
    let results = lock.Issues |> List.map (fun i -> { Issue = i; Outcome = AlreadyExisted })
    Ok { Lock = lock; Results = results; Source = FromLockFile }

| Some lock ->
    if input.Verbose then
        eprintfn "Lock file found but hashes changed — re-running."
    match runFull deps input mergedConfig yamlHash templateHash with
    | Error e -> Error e
    | Ok fullResult when lock.TemplateHash = templateHash ->
        // YAML changed but template didn't — no body refresh needed.
        Ok fullResult
    | Ok fullResult ->
        refreshBodies deps input mergedConfig fullResult

| None ->
    runFull deps input mergedConfig yamlHash templateHash
```

Note: `--skip-lock` always calls `refreshBodies` (no template hash guard
since the prior lock is intentionally discarded). This matches the
"force everything to reconcile" intent of the flag. The `no lock` branch
does **not** refresh — there are no pre-existing issues from a prior
run to refresh; `runFull` just created them with the current body.

### 2. Add `refreshBodies` helper

Body refresh targets only the outcomes that need it, in parallel,
respecting the existing stale-issue recovery so a deleted/transferred
issue is recreated rather than silently failing the refresh.

```fsharp
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
                    let (RepoName repoStr) = r.Issue.Repo
                    let (IssueNumber issueN) = r.Issue.Number
                    match! deps.GhClient.UpdateIssue r.Issue.Repo r.Issue.Number config.IssueTitle config.IssueBody with
                    | Ok () ->
                        let newOutcome =
                            match r.Outcome with
                            | Reopened -> Reopened   // keep distinction
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
        // Reuse the existing stale-recreate pass for any refresh that hit a
        // deleted/transferred issue, against the project recorded in fullResult.Lock.
        let hasStale = refreshed |> List.exists (fun r -> r.Outcome = StaleIssueRecreated)
        let recoveredRefreshed, newRefByRepo =
            if not hasStale then
                refreshed, Map.empty
            else
                let processParams = resolveProcessParams deps input config fullResult.Lock.Project
                recreateStaleIssues deps processParams refreshed
        // Merge refreshed results back into fullResult.Results.
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
        if not (Map.isEmpty newRefByRepo) then
            LockFile.write deps.FileSystem input.YamlPath finalLock
        Ok { fullResult with Lock = finalLock; Results = finalResults }
```

### 3. Delete dead code

After step 2, `updateBodies` and `applyBodyUpdates`
(`RunCommand.fs:399-500`) are unused. Remove them. `recreateStaleIssues`
stays — `refreshBodies` reuses it. `isStaleIssue` stays.

`IssueOutcome.Updated` and `UpdateFailed` stay — both are still
produced by `refreshBodies`.

### 4. Tests

Edit `tests/OrcAI.Core.Tests/RunCommandTests.fs`:

- Existing test "template-only change → updateBodies" → re-target at
  the new path. Assert `runFull` ran (FindIssue called for each repo)
  and bodies were refreshed for the open ones.
- Existing test "both hashes changed → runFull + body update" →
  still passes; same behaviour, narrower implementation.
- **New**: template bumped + one repo's issue was closed manually +
  `onClosedIssue: skip` → that repo reports `Skipped`, no `UpdateIssue`
  call against it; other repos report `Updated`.
- **New**: template bumped + one repo's issue was closed manually +
  `onClosedIssue: reopen` → that repo reports `Reopened` and
  `UpdateIssue` was called against it (body refresh after reopen).
- **New**: YAML changed but template unchanged (e.g. a label added) →
  `runFull` runs, **no** `UpdateIssue` calls.
- **New**: `--skip-lock` with no MD edit → `runFull` runs and bodies
  are refreshed for `AlreadyExisted` (assert `UpdateIssue` was called
  for each open repo).
- Drop any test that exercised the old `updateBodies` directly.

### 5. Docs

`docs/state-and-idempotency.md`:

- Replace the existing dispatch description under "How the Layers
  Interact" with the two-branch version.
- Under "Closed issues", add a sentence: "Template bumps go through the
  same `runFull` path as YAML changes, so the `onClosedIssue` policy
  applies uniformly."

### 6. Changelog

Suggested wording under unreleased:

```
- fix: Template bumps now go through `runFull`, honouring `onClosedIssue`
  policy and refreshing the body of reopened issues. Previously, editing
  only the MD template could silently rewrite the body of a closed
  issue and skip the body refresh for reopened issues.
```

---

## Trade-offs

| Aspect | Before | After |
|---|---|---|
| API calls when template changes | `UpdateIssue` per repo (1/repo) | Full `runFull` walk (≈3-5/repo: FindIssue, project add, assign check, UpdateIssue) |
| Correctness around closed issues | Broken — ignores `OnClosedIssue` | Honoured via `runFull` |
| Drift recovery (closed + new open with same title) | Wrong issue updated | New open issue picked up by `FindIssue` |
| `Reopened` body refresh | Skipped | Done |
| Code surface | Two paths (`updateBodies` + `runFull` + merge) | One path (`runFull` + small `refreshBodies`) |
| Project add / Copilot assign on template bump | Skipped | Re-checked (idempotent; assign skipped when already assigned) |

The extra API calls on template bump are acceptable: this runs only
when the user actually edited the MD, and `runFull` is already what we
do on a YAML change.

## Edge cases

| Case | Behaviour |
|---|---|
| Template hash empty in old lock (pre-feature) | First post-upgrade `run` sees mismatch → `runFull` → body refresh runs. Same as treating it as "changed". |
| `UpdateIssue` errors mid-refresh | Repo reports `UpdateFailed`; lock keeps the existing ref; `runFull` already wrote the lock once at its own boundary so the project/labels/etc. are persisted. Next run retries the body. |
| Stale issue detected during refresh | `StaleIssueRecreated` → `recreateStaleIssues` → new issue, lock rewritten. Same as today's `Both changed` path. |
| YAML-only change | `lock.TemplateHash = templateHash` guard skips `refreshBodies` entirely — no extra API calls. |
| `--skip-lock` after a previous successful run | `runFull` re-reconciles every repo, then `refreshBodies` re-writes the body of every open / reopened issue. Lock is overwritten on success. |
| `--skip-lock` on a clean checkout (no prior lock) | `runFull` creates issues with the current body; `refreshBodies` then runs against `AlreadyExisted`/`Reopened` outcomes only — typically none, so it's a no-op. |

## Verification

```bash
# 1. Initial state
orcai run example/add-agents-md.yml
# → Created for each repo, lock written

# 2. YAML-only change (e.g. add a label) — body refresh must NOT run
vim example/add-agents-md.yml
orcai run example/add-agents-md.yml --verbose
# → AlreadyExisted for each repo (no UpdateIssue calls visible in --verbose)

# 3. Close one issue manually on GitHub. Set onClosedIssue: skip in YAML.

# 4. Template-only change
vim example/add-agents-md.md
orcai run example/add-agents-md.yml --verbose
# → Closed-issue repo: Skipped
# → Other repos: Updated
gh issue view <closed-number> --repo <org/repo>  # body unchanged

# 5. Switch to onClosedIssue: reopen and re-run
orcai run example/add-agents-md.yml --on-closed-issue reopen
# → Previously-skipped repo: Reopened, with the new body

# 6. --skip-lock force-refresh (no file edits)
orcai run example/add-agents-md.yml --skip-lock --verbose
# → Every open repo: Updated (bodies re-pushed even though nothing changed locally)
```
