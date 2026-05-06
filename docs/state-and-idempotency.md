# State and Idempotency

OrcAI uses two layered guards to avoid creating duplicate GitHub resources: a **lock file fast path** and **per-resource API-level checks**. This document describes both layers, their interaction, and known gaps.

---

## Layer 1 — Lock File

A successful `orcai run` writes a `<stem>.lock.json` file alongside the YAML config. It records:

- `yamlHash` — SHA-256 of the raw YAML file bytes
- `lockedAt` — timestamp of the run
- All created resources: `project`, `repos`, `issues`, `pullRequests`

On subsequent runs, if the lock file exists and its `yamlHash` matches the current YAML content, the run short-circuits immediately — no GitHub API calls are made. Every issue is reported as `AlreadyExisted` and the result source is `FromLockFile`.

### Lock file lifecycle

| Event | Effect on lock file |
|---|---|
| All repos succeed | Written (or overwritten) |
| Any repo fails | Not written — no partial lock |
| YAML content changes | Hash mismatch → full re-run → lock overwritten on success |
| `orcai cleanup` succeeds | Lock file deleted |
| `--skip-lock` flag | Lock file ignored entirely; always full run |

### Hash computation

The hash is `SHA256(raw YAML bytes)`. Any change to the file — including whitespace — invalidates the lock and triggers a full run.

---

## Layer 2 — API-Level Idempotency

When no valid lock file exists, `runFull` is called and each resource is checked individually against the GitHub API before being created.

### Projects

`FindProject` calls `gh project list --owner <org> --format json` and matches on **exact `title` equality**. If a project with that title exists, it is reused and no new project is created.

### Issues

`FindIssue` calls `gh issue list --repo <repo> --state open --json title,number,url,assignees` and matches on **exact `title` equality**. The outcome is tracked with a discriminated union:

| `FindIssue` result | Action | `IssueOutcome` |
|---|---|---|
| `Some issue` | Skip creation | `AlreadyExisted` |
| `None` | Create issue | `Created` |

The `assignees` field is fetched at this point and feeds directly into the Copilot check below.

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
  ├─ Lock file exists AND yamlHash matches?
  │     YES → return all issues as AlreadyExisted (no API calls)
  │     NO  ↓
  │
  ├─ FindProject → reuse or create
  │
  └─ For each repo:
        ├─ FindIssue (open, by title) → reuse or create
        └─ hasCopilot? → skip or assign
```

---

## GitHub Issue States

GitHub issues have exactly two states: `open` and `closed`. There is no draft, pending, or in-progress state on the issue itself. The `state_reason` field (`completed`, `not_planned`, `reopened`) is supplementary and does not add a third state. The `--state open` filter in `FindIssue` is exhaustive for live issues.

---

## Known Gaps

### Closed issues are not detected

Status: Done

`FindIssue` only searches open issues. If an issue created by a previous run was manually closed, a subsequent run (without a valid lock file) will create a duplicate open issue.

**Workaround:** Keep the lock file intact, or manually reopen the issue before running again.

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
