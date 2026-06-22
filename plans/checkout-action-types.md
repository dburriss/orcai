# Plan: Checkout-based action types

## Goal

Enable action types that execute against a checked-out copy of a target repository.
The first deliverable is a low-level `cmd-checkout` action type that clones the repo,
runs a command inside it, and exits. Higher-level action types (e.g. `cmd-to-pr`)
build on top of this primitive.

This is primarily designed for use in GitHub Actions workflows, where the runner
environment is ephemeral and cloning target repos is routine.

---

## Primitives and layering

```
cmd              — run a command with template vars only (existing)
cmd-checkout     — run a command inside a checked-out repo (new low-level primitive)
cmd-to-pr    — checkout → run → commit all changes → push → open PR (builds on cmd-checkout)
```

Each higher-level type is implemented in terms of the primitive below it.
`cmd` is unchanged.

---

## Checkout behaviour

### Clone strategy

- Always use `--depth 1` (shallow clone) by default.
- Always check out the default branch by default.
- Auth: orcai configures `git` to use the same token already used for GitHub API
  calls (`gh auth git-credential`). No separate credential setup required from the user.
  The token must have **Contents: Read & write** on target repos for push-based action
  types (see auth docs update below).

### Worktree reuse across jobs

When a glob matches multiple repos, each repo is a separate clone. However, if the
same repo is targeted by more than one job in a single orcai config file, orcai reuses
a single base clone and creates a **git worktree** per job. This avoids redundant
clones of the same repo within a single run.

Worktree root path: `<checkout_root>/<org>/<repo>/<repo>.git/`
Worktree path per job: `<checkout_root>/<org>/<repo>/<repo>.<normalized-branch-name>/`

`checkout_root` defaults to a temp directory scoped to the run. It can be overridden
in global or local config:

```yaml
# orcai.yml (global or local)
checkout_root: "/tmp/orcai-checkouts" # linux and macOS; use a valid path on Windows
```

### Cleanup

Checkout directories are deleted after the run completes (success or failure).

---

## `cmd-checkout` action type

Identical to `cmd` but the working directory is the root of the checked-out repo.
The `cwd` field is interpreted relative to the checkout root.

```yaml
# String form (shell): sh -c / cmd /C wraps the command; shell syntax works
action:
  type: cmd-checkout
  execute: "./scripts/inspect.sh --flag"
  cwd: "./subdir"                   # relative to checkout root

# List form (exec): argv passed directly, no shell
action:
  type: cmd-checkout
  execute: ["./scripts/inspect.sh", "--flag"]
  cwd: "./subdir"
```

All existing `cmd` template variables are available, plus:

| variable | value |
|---|---|
| `{{checkout_path}}` | absolute path to the checkout root for this repo |
| `{{default_branch}}` | the repo's default branch name |

---

## `cmd-to-pr` action type

Checkout → run cmd → diff worktree → commit all changed files → push branch → open PR.

### YAML schema

```yaml
# String form (shell): the whole string is passed to sh -c / cmd /C
action:
  type: cmd-to-pr
  execute: "./scripts/upgrade.sh --target 6.0"
  cwd: "./subdir"                   # relative to checkout root; optional

  # Write-back config
  writeBack: pr-to-origin           # or: commit-to-origin, fork-and-pr
  errorIfNoDiff: false              # default: false (exit 0, no diff → skip silently)

  # Branch / commit / PR metadata (all optional — defaults shown)
  branch: "orcai/{{job_title_slug}}"
  commitMessage: "[{{issue_number}}] {{job_title}}"
  prTitle: "[{{issue_number}}] {{job_title}}"
  prBody: ""                        # empty by default; supports template vars

# List form (exec): argv passed directly, no shell
action:
  type: cmd-to-pr
  execute: ["./scripts/upgrade.sh", "--target", "6.0"]
  writeBack: pr-to-origin
```

### Write-back modes

| mode | behaviour |
|---|---|
| `pr-to-origin` | push branch to origin, open PR against default branch |
| `commit-to-origin` | push directly to `branch` on origin (no PR) |
| `fork-and-pr` | `gh repo fork`, push to fork, open PR against origin's default branch |

`write_back` can be set at global or local config level to avoid repeating it per job:

```yaml
# orcai.yml
write_back: pr-to-origin
```

Job-level `write_back` overrides the global value.

### Branch naming

Branch name is derived from `job.title`, normalised:
- Lowercased
- Spaces and non-alphanumeric characters replaced with `-`
- Consecutive `-` collapsed
- Truncated to fit git's branch name limits (no hard limit in git, but keep under 100
  chars to avoid filesystem issues)

Default pattern: `orcai/{{job_title_slug}}`

If the branch already exists on the remote, orcai force-pushes (the branch is
orcai-owned; its history is not preserved across runs).

### Execution flow

1. Clone repo (or reuse worktree if same repo already cloned for another job)
2. Run cmd in `cwd` (relative to checkout root)
3. On non-zero exit: record failure, skip steps 4–7
4. Diff worktree
5. If diff is empty:
   - `error_if_no_diff: true` → record as failure
   - `error_if_no_diff: false` (default) → skip silently, record as "no changes"
6. Stage all changed files (`git add -A`), commit with `commit_message`
7. Push branch; open PR (or commit directly) according to `write_back`
8. Record PR URL / branch in lock file

---

## Idempotency

### "Done" definition

A repo+job combination is considered **done** when the lock file contains a successful
result for it. The state of the issue or PR on GitHub does not matter — closed, merged,
or abandoned all count as done.

This replaces the current confusing default where closed issues trigger re-runs.
The new default is: **existing work = done, regardless of state.**

### Opting back in to re-runs

```yaml
# orcai.yml or per-job
redo_on_closed: true   # re-run the action if the issue or PR is closed/merged
```

This flag applies to all action types. It replaces the current `skip_closed_issues`
behaviour (which had the inverse default and caused unexpected re-runs).

> **Note:** `skip_closed_issues` should be deprecated and removed at the same time
> this is introduced, with a hard validation error pointing to `redo_on_closed`.

### Lock file entries for `cmd-to-pr`

```json
{
  "repo": "org/repo",
  "job": "Upgrade to .NET 10",
  "outcome": "pr-opened",
  "pr_url": "https://github.com/org/repo/pulls/42",
  "branch": "orcai/upgrade-to-dotnet-10"
}
```

Possible outcomes: `pr-opened`, `committed`, `no-changes`, `cmd-failed`, `push-failed`.

---

## Auth requirements (docs update)

When implementing checkout-based action types, update `docs/app-auth.md`:

- Upgrade **Contents** permission from `Read` to `Read & write` for the GitHub App.
- Add a note that push-based action types (`cmd-to-pr`, `commit-to-origin`,
  `fork-and-pr`) require this elevated permission.
- Add a note that orcai configures `git` credentials automatically using the same token;
  no separate credential setup is needed. If push is denied, orcai fails fast with a
  clear error message (not a silent hang or cryptic git failure).

---

## Implementation order

1. **Checkout infrastructure** — clone, worktree reuse, credential setup, cleanup
2. **`cmd-checkout`** — lowest friction; validates the checkout primitive in isolation
3. **`redo_on_closed` + deprecate `skip_closed_issues`** — fix the idempotency default
   before introducing new action types that rely on it
4. **`cmd-to-pr` with `pr-to-origin`** — most common write-back mode first
5. **`commit-to-origin`** — simpler than fork (no fork step)
6. **`fork-and-pr`** — requires `gh repo fork` + fork remote management

---

## Out of scope

- Custom branch base (always default branch for now)
- Sparse checkout / partial clone
- Updating an existing PR (current run always force-pushes and the PR updates naturally)
- Multiple commits per run
- `cmd-checkout` with write-back (users who need that should use `cmd-to-pr`)
