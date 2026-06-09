# Plan: Explicit errors in the lock file

## Context

The lock file (`<name>.lock.json`) records only successes today. Failures are stderr-only; when any repo fails, the lock isn't written at all. There's no on-disk signal that something needs attention and no way for a later run to make a different decision based on prior failures.

**The lock file becomes an input to subsequent runs.** Each failure is recorded with enough state for the tool to decide whether to retry, retry differently, or skip. Specifically:

- Each `(repo, category)` failure carries an attempt counter, a classified cause, first/last timestamps, and the last raw error message.
- The tool auto-retries failures up to a max-attempts cap.
- `UserError` causes are never auto-retried (input is wrong; retrying won't help) until the YAML changes.
- On success, the failure entry is removed — the lock stays clean.
- A repo listed in YAML that has no issue in the lock and no failure entry is just a **new addition**, not a failure; it goes through the normal first-run flow.

In-scope failure categories: `FindIssue`, `CreateIssue`, `ReopenIssue`, `AssignIssue`, `AddToProject`, `UpdateBody`. Out of scope: `PostComment`.

Backward compatibility: **shape-based, additive only**. New fields are optional with empty defaults on read, so existing `*.lock.json` files parse unchanged. No `schemaVersion` field needed for this change; one can be added later if a non-additive change is required.

## Schema change

New `failures` array on `LockFile`. Default `[]` on read.

```json
{
  "lockedAt": "…",
  "yamlHash": "…",
  "templateHash": "…",
  "project": { … },
  "repos": [ … ],
  "issues": [ … ],
  "pullRequests": [ … ],
  "skippedRepos": [ … ],
  "failures": [
    {
      "repo": "org/repo-a",
      "category": "AssignIssue",
      "cause": "UserError",
      "attempts": 2,
      "firstFailedAt": "2026-05-20T10:00:00Z",
      "lastFailedAt":  "2026-05-21T09:15:00Z",
      "lastMessage":   "could not resolve user 'dburris-typo'"
    }
  ]
}
```

Field semantics:
- `category` — `FindIssue | CreateIssue | ReopenIssue | AssignIssue | AddToProject | UpdateBody`. Each `(repo, category)` is unique.
- `cause` — `RateLimit | NotFound | Permission | UserError | NetworkTransient | Unknown`. Derived by a small classifier over `gh` stderr.
- `attempts` — incremented on each retry-and-fail. Starts at `1`.
- `firstFailedAt` / `lastFailedAt` — ISO timestamps; `firstFailedAt` preserved across retries.
- `lastMessage` — verbatim last error.

## Retry policy

- Default max attempts = **3** per `(repo, category)`. YAML-configurable as `failures.maxAttempts: <int>`.
- `attempts >= maxAttempts` → step is **skipped** on subsequent runs; entry remains so the user sees it. Clearing is manual (delete the entry, or bump `maxAttempts`, or fix the root cause so the next attempt succeeds).
- `cause = UserError` → **not** auto-retried, regardless of `attempts`. Entry is cleared automatically if `yamlHash` changes (the YAML was edited, give it another shot) or `templateHash` changes for `UpdateBody`.
- `cause` ∈ {`RateLimit`, `NetworkTransient`, `NotFound`, `Permission`, `Unknown`} → retried up to the cap. (In-process `withRetry` at `GhClient.fs:70-89` already handles transient retries within a single run; this is across-run.)
- On success → matching `(repo, category)` entry is **removed**.

## Execution flow

When a lock exists, current code branches on `yamlHash` to choose `runFull` vs `refreshBodies`. With failures-as-input, the flow per repo unifies:

```
for each repo in yaml.repos:
    issue = lock.issues.find(repo)
    failuresForRepo = lock.failures.filter(repo)

    if no issue:
        # Newly added repo OR prior terminal failure (FindIssue/CreateIssue/ReopenIssue)
        if any terminal failure has attempts >= max and cause ≠ UserError-with-stale-yaml:
            skip (record stays)
        else:
            run issue-creation flow (find/create/reopen + assign + add-to-project)
            update failures accordingly

    else:
        # Issue already exists; selectively retry partial failures and run body refresh
        for category in {AssignIssue, AddToProject, UpdateBody}:
            entry = failuresForRepo.find(category)
            if entry.attempts >= max: skip
            elif entry.cause = UserError and yamlHash unchanged (template unchanged for UpdateBody): skip
            else: retry the step; update or clear the entry

        # Body refresh is also driven by templateHash change (existing behavior)
        if templateHash changed and no UpdateBody skip: run UpdateBody
```

This replaces the rigid `runFull`-vs-`refreshBodies` branch with a per-repo decision driven by `(issue exists?, failures for repo, hash changes)`.

## Files to modify

### `src/OrcAI.Core/Domain.fs` (~lines 79–88)

Add types and extend `LockFile`:

```fsharp
type RepoFailureCategory =
    | FindIssue | CreateIssue | ReopenIssue
    | AssignIssue | AddToProject | UpdateBody

type RepoFailureCause =
    | RateLimit | NotFound | Permission
    | UserError | NetworkTransient | Unknown

type RepoFailure = {
    Repo: RepoFullName
    Category: RepoFailureCategory
    Cause: RepoFailureCause
    Attempts: int
    FirstFailedAt: System.DateTimeOffset
    LastFailedAt: System.DateTimeOffset
    LastMessage: string
}

type LockFile = {
    // existing fields…
    Failures: RepoFailure list   // NEW
}
```

### `src/OrcAI.Core/LockFile.fs` (~lines 43–51 and serialization)

- Add `RepoFailureDto` and add `failures: RepoFailureDto list` to `LockFileDto`.
- Serialize unions as case-name strings.
- On read: missing `failures` → `[]`.
- Add helpers:
  - `classifyCause: string -> RepoFailureCause` (pattern table below).
  - `mergeFailures: previous:RepoFailure list -> attempted:(RepoFullName * RepoFailureCategory * Result<unit,string>) list -> yamlHashChanged:bool -> templateHashChanged:bool -> now:DateTimeOffset -> RepoFailure list`. Increments counters on repeat failure, clears on success, drops UserError entries when relevant hash changed.

Classifier table (case-insensitive, first match wins):

| Pattern | Cause |
|---|---|
| `rate limit`, `secondary rate limit` | `RateLimit` |
| `404`, `not found` | `NotFound` |
| `403`, `permission`, `forbidden` | `Permission` |
| `could not resolve`, `no such user`, `invalid` | `UserError` |
| `timeout`, `connection`, `EOF`, `network` | `NetworkTransient` |
| (otherwise) | `Unknown` |

Unit-test `classifyCause` and `mergeFailures`.

### `src/OrcAI.GitHub/GhClient.fs` (lines 482–489)

Stop discarding `AddIssueToProject` result:

```fsharp
member _.AddIssueToProject project issue =
    async {
        let (OrgName orgStr) = project.Org
        match! runGhApi bucket retries ghToken $"project item-add …" with
        | Ok _ -> return Ok ()
        | Error e -> return Error e
    }
```

The interface already promises `Async<Result<unit, string>>`; only the impl was discarding.

### `src/OrcAI.Core/RunCommand.fs` (~lines 213–567)

Replace the binary `runFull` / `refreshBodies` branch with a unified per-repo executor.

1. Richer per-repo outcome so partial failures ride along with successes:

   ```fsharp
   type RepoOutcome = {
       Success: ProcessedRepo option   // None = terminal failure
       Attempted: (RepoFailureCategory * Result<unit,string>) list
   }
   ```

2. **Per-repo executor** (new function, supersedes `processRepo`):
   - Looks up the existing issue and prior failures for the repo.
   - Decides per category whether to attempt or skip based on `attempts`, `cause`, and hash changes.
   - Runs `FindIssue`/`CreateIssue`/`ReopenIssue` (if no issue yet) → `AssignIssue` → `AddToProject` → `UpdateBody` (if templateHash changed or there's a prior `UpdateBody` failure to retry).
   - Each attempted step yields `(category, Ok () | Error e)` into `Attempted`.

3. **At the end of the run**:

   ```fsharp
   let allResults  = repoResults |> Array.choose _.Success |> Array.toList
   let attempted   = repoResults |> Array.collect (fun r ->
                       r.Attempted |> List.map (fun (cat, res) -> repo, cat, res) |> List.toArray)
                     |> Array.toList

   let newFailures = mergeFailures previousLock.Failures attempted yamlHashChanged templateHashChanged now

   let lock = { … existing fields …; Failures = newFailures }

   if not input.DryRun then
       LockFile.write deps.FileSystem input.YamlPath lock   // always write (was: only when failures = 0)
   ```

   The lock is now **always written** (outside dry-run), and `mergeFailures` produces the correct counter/timestamp evolution and clears successes.

4. `refreshBodies` is folded into the per-repo executor — it's just the `UpdateBody` step when `templateHash` changed or there's a prior failure to retry. `StaleIssueRecreated` continues to work via the existing recreate path inside the executor when `UpdateIssue` returns the stale-issue error.

### Stdout summary

End of run:

```
12 successes, 2 failures, 1 skipped (max attempts) (lock: example/add-agents-md.lock.json)
  org/repo-a  AssignIssue   UserError      attempt 2/3 — could not resolve user 'dburris-typo'
  org/repo-b  AddToProject  RateLimit      attempt 1/3 — API rate limit exceeded
  org/repo-c  UpdateBody    Unknown        attempt 3/3 — SKIPPED
```

Concise; full state lives in the lock.

## Reuse / existing utilities

- `IssueOutcome.UpdateFailed` (`Domain.fs`) — already carries the message; feed into `mergeFailures` as `(repo, UpdateBody, Error e)`.
- `withRetry` / `ApiBucket` (`GhClient.fs:70-89`) — keep in-run transient retries; only persistent errors hit the lock.
- `eprintfn` lines at `RunCommand.fs:303,361` and `Comments.fs:32` — keep for live feedback; lock entries are additive. `PostComment` failures remain stderr-only.

## Backward compatibility

- All new fields are optional on read with empty/default values.
- Existing lock files (no `failures`) parse to `Failures = []`; `cleanup` and `info` continue to work unchanged.
- No `schemaVersion` field for this change since the change is purely additive. Add one later if non-additive evolution is needed; absence then means `1`.

## Verification

1. `dotnet build` from repo root.
2. Old-lock-compat: take an existing `*.lock.json` (no `failures`), run `info` and `cleanup` — both succeed unchanged.
3. Manual failure injection (against a test org):
   - Bad assignee handle → run records `AssignIssue / UserError`, issue still in `issues[]`.
   - Bad project number → `AddToProject` entry recorded.
   - Read-protected repo → `FindIssue` terminal failure; repo absent from `issues[]` but present in `failures[]`.
4. Re-run unchanged: `UserError` entries are not retried, `attempts` stays at the original value for them; transient/unknown entries retry, `attempts` increments, `firstFailedAt` preserved, `lastFailedAt` updates.
5. Fix the YAML (e.g., correct assignee) → `yamlHash` differs → `UserError` entries cleared automatically and retried; success → entry removed.
6. Drive a `(repo, category)` to `attempts = maxAttempts` → next run skips it and prints `SKIPPED`. Entry remains in lock.
7. Newly added repo in YAML (no lock entry) is processed normally with a fresh first-run flow.
8. Dry run still skips lock write.
9. Run with all repos failing → lock IS written (partial state), all failures recorded.

## Out of scope

- `PostComment` (nudge/notify/assign-comment) failures.
- CLI surface for inspecting/clearing failures (`cat <lock>` is sufficient; editing the lock is supported).
- `schemaVersion` field.
