# State and Idempotency

OrcAI uses two layered guards to avoid creating duplicate GitHub resources: a **lock file fast path** and **per-resource API-level checks**. This document describes both layers, their interaction, and known gaps.

---

## Layer 1 — Lock File

A successful `orcai run` writes a `<stem>.lock.json` file alongside the YAML config. It records:

- `yamlHash` — SHA-256 of the raw YAML file bytes
- `templateHash` — SHA-256 of the raw MD template file bytes
- `lockedAt` — timestamp of the run
- All created resources: `project`, `repos`, `issues`, `pullRequests`

On subsequent runs, if the lock file exists and **both** `yamlHash` and `templateHash` match the current files, the run short-circuits immediately — no GitHub API calls are made. Every issue is reported as `AlreadyExisted` and the result source is `FromLockFile`. If either hash differs, the run falls through to `runFull`.

### Lock file lifecycle

| Event | Effect on lock file |
|---|---|
| All repos succeed | Written (or overwritten) |
| Any repo fails | Not written — no partial lock |
| YAML content changes | Hash mismatch → full re-run → lock overwritten on success |
| MD template content changes | Hash mismatch → full re-run → body refresh → lock overwritten on success |
| `orcai cleanup` succeeds | Lock file deleted |
| `--skip-lock` flag | Lock file ignored entirely; always full run + body refresh |

### Hash computation

Both hashes are `SHA256(raw file bytes)`. Any change to either the YAML or the MD template — including whitespace — invalidates the lock and triggers a full run. A template-only change additionally refreshes the body of every issue that `runFull` reconciled as `AlreadyExisted` or `Reopened`.

---

## Layer 2 — API-Level Idempotency

When no valid lock file exists, `runFull` is called and each resource is checked individually against the GitHub API before being created.

### Projects

`FindProject` calls `gh project list --owner <org> --format json` and matches on **exact `title` equality**. If a project with that title exists, it is reused and no new project is created.

### Issues

`FindIssue` calls `gh issue list --repo <repo> --state open --json title,number,url,assignees` and matches on **exact `title` equality**. The outcome is tracked with a discriminated union:

| `FindIssue` result | Action | `IssueOutcome` |
|---|---|---|
| `Ok (Some issue)` | Skip creation | `AlreadyExisted` |
| `Ok None` | Create issue | `Created` |
| `Error e` | Abort the repo with an error | _no outcome — repo reported as failed_ |

The `assignees` field is fetched at this point and feeds directly into the Copilot check below.

A lookup error (transient `gh` failure after `runGhApi`'s retry budget is exhausted) short-circuits the repo rather than being treated as "no matching issue". This prevents the duplicate-issue path where a temporary rate-limit or network blip during `FindIssue`/`FindClosedIssue` would otherwise cause `CreateIssue` to fire against a repo that already has the target issue. The lock file is not written for failed repos, so the next run retries cleanly.

### Copilot Assignment

After issue find-or-create, the application checks whether `@copilot` is already assigned before calling `AssignIssue`:

```
hasCopilot = issue.Assignees contains "copilot" (case-insensitive)
```

| Condition | Action |
|---|---|
| `hasCopilot = true` | Skip assignment |
| `--skip-copilot` flag or `skipCopilot: true` in YAML | Skip assignment |
| GitHub App auth without a PAT-based `CopilotClient` | Warn and skip (Apps cannot assign `@copilot`) |
| Otherwise | Call `AssignIssue` |

For freshly created issues, `Assignees` is always `[]`, so assignment always proceeds.

### Pull Requests

PRs are not created by OrcAI — that is delegated entirely to Copilot. `FindPrsForIssue` is only called during `orcai cleanup` to locate PRs linked to an issue (via `closingIssuesReferences`) so they can be closed before the issue is deleted. There is no create-or-skip logic for PRs.

---

## How the Layers Interact

```
orcai run
  │
  ├─ --skip-lock?
  │     YES → runFull, then refresh body for AlreadyExisted + Reopened
  │     NO  ↓
  │
  ├─ Lock file exists AND yamlHash matches AND templateHash matches?
  │     YES → return all issues as AlreadyExisted (no API calls)
  │     NO  ↓
  │
  ├─ Lock file exists but a hash changed?
  │     YES → runFull, then if templateHash differs: refresh body
  │           for AlreadyExisted + Reopened
  │     NO (no lock at all) → runFull (no body refresh — issues
  │                           were just created with the current body)
  │
  └─ runFull:
        ├─ FindProject → reuse or create
        └─ For each repo:
              ├─ FindIssue (open, by title) → reuse if found
              ├─ Else FindClosedIssue (closed, by title) → onClosedIssue action: create | reopen | skip | fail
              └─ hasCopilot? → skip or assign
```

A change to either the YAML or the MD template invalidates the lock the same way. Body-refresh after `runFull` only targets `AlreadyExisted` and `Reopened` outcomes — never `Skipped`, `SkippedArchived`, `Created`, or a closed issue that the policy left alone.

---

## GitHub Issue States

GitHub issues have exactly two states: `open` and `closed`. There is no draft, pending, or in-progress state on the issue itself. The `state_reason` field (`completed`, `not_planned`, `reopened`) is supplementary and does not add a third state. `FindIssue` queries `--state open` and `FindClosedIssue` queries `--state closed`; together they are exhaustive.

## Closed issues

`processRepo` first calls `FindIssue` (open) and reuses any exact-title match. If that misses, it calls `FindClosedIssue` (closed). When a closed match exists, the next step is governed by `job.onClosedIssue` (or `--on-closed-issue`):

| Action | Behaviour |
|--------|-----------|
| `create` (default) | Open a new issue alongside the closed one. This is what produces duplicates after a manual close. |
| `reopen` | Reopen the closed issue and proceed to project add + assign. |
| `skip` | Leave the repo untouched, report `skipped`, and do not add to project or assign. |
| `fail` | Treat the closed match as a hard error for that repo. |

Title matching is exact and case-sensitive — any drift in `job.title` between runs (date stamps, version numbers, trailing whitespace) bypasses both checks. To stop duplicates on re-runs of a stable title, set `onClosedIssue: reopen` (or `skip`) in YAML or pass `--on-closed-issue reopen` on the CLI.

Template bumps go through the same `runFull` path as YAML changes, so the `onClosedIssue` policy applies uniformly — editing only the MD template will never silently rewrite the body of a closed issue.

---

## Known Gaps

### Copilot non-delivery is not detected

Status: By design

Once `@copilot` appears in an issue's assignees, the application considers the work done. It does not poll for PR creation or verify that Copilot actually produced output. If Copilot was assigned but failed silently or was unresponsive, a subsequent run will still see `hasCopilot = true` and skip re-assignment.

**Workaround:** Manually unassign `@copilot` from the issue and delete the lock file (or change the YAML), then re-run.

### Lock file and live state can diverge

Status: By design

If GitHub resources are modified or deleted outside of OrcAI after a successful run, the lock file still reflects the original state. OrcAI will treat the run as complete and make no corrections.

**Workaround:** Use `--skip-lock` to force a full re-run, which will re-check each resource against the live API.

### Title is the only uniqueness key

Status: By design

Both project and issue detection rely solely on exact title matching. Renaming a resource in GitHub (without changing the YAML) will cause OrcAI to treat it as missing and create a duplicate on the next lock-invalidating run.
