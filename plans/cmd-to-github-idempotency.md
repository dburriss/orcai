# Plan: Idempotent `cmd-to-github` re-runs

## Context

`cmd-to-github` has no `shouldAttempt`-style gating at all (unlike `FindIssue`/`AssignIssue`/`AddToProject`), so every non-fast-path `orcai run` unconditionally re-clones, re-runs the `execute` command (often a slow/costly/nondeterministic AI agent), and force-pushes the branch — even when a PR already exists and there's nothing left to do. The only "skip" signal today is `gh pr create` failing with "already exists", discovered *after* redoing all the work.

This surfaced from a real bug report: after deleting the lock file to test idempotency, the push failed with `git push --force-with-lease ... (stale info)` (fixed separately — see the `--force` change already shipped), and separately the lock file kept reporting a stale `CmdToGithubPushFailed` forever because success was never `record`ed (also fixed already — see the `record ... (Ok ())` additions). Both of those are shipped. This plan is the next layer: make the whole action idempotent by design, not just by accident of a clean lock file.

**Explicit non-goal:** relying on the lock file as the source of truth. The lock file must remain purely a cache/fast-path optimization (skip *all* GitHub calls when YAML/template hash is unchanged and there are no recorded failures — this already exists and is unaffected by this plan). Every check that decides whether to skip/retry/redo the `cmd-to-github` action itself must hold **even with no lock file at all** (deleted, corrupted, or a fresh checkout on CI). Correctness comes from live GitHub state, exactly like `FindIssue` already does today (a real API call on every non-fast-path run, never inferred from prior bookkeeping).

---

## Decision table

Before any clone/execute/push, for each repo running a `cmd-to-github` action:

| PR state (live check) | Hashes unchanged | Hashes changed |
|---|---|---|
| **OPEN** | Skip entirely — done | Redo: clone, rerun `execute`, commit, force-push (updates the open PR's diff automatically) — `gh pr edit` if title/body are templated |
| **MERGED** | Skip — done | Skip — done. Work already shipped; a later instruction change starts a new cycle, it doesn't retroactively edit merged work (consistent with how closed/merged issues aren't retroactively edited) |
| **CLOSED** (not merged) | `onClosedPr` setting applies | Same — closed was an explicit decision independent of instruction changes; `recreate` naturally picks up new instructions since it's a full redo anyway |
| **None found, branch exists on remote** | Skip clone/execute/push; retry `gh pr create` only (pure API call, no checkout needed) | Full run — the existing branch reflects stale instructions |
| **None found, no branch** | Full run | Full run |

`Hashes changed` = `yamlHashChanged || templateHashChanged`, already computed per-run in `RunCommand.fs` (currently only drives issue `refreshBodies`).

---

## Live checks (no lock file dependency)

1. **PR state for the issue** — reuse `IPullRequestLinker.FindPrsForIssue` (already used by `nudge`; GraphQL `closingPullRequests` + cross-referenced-timeline fallback). This is reliable now because `cmd-to-github` always writes `Closes #{{issue_number}}` into the PR body (already shipped), so `closingPullRequests` will find it without guessing from branch names.
2. **Branch existence on remote** — `git ls-remote --heads <url> <branch>`. No clone required.

Both are cheap relative to a full clone + `execute` + push, and both run on every non-fast-path invocation — with or without a lock file — so correctness doesn't depend on local state surviving between runs.

---

## New setting: `onClosedPr`

Mirrors `onClosedIssue`'s shape exactly (see `plans/closed-issue-options.md`).

```
action:
  type: cmd-to-github
  onClosedPr: skip   # default; or: recreate | reopen | fail
```

| Value | Behavior when the found PR is CLOSED (not merged) |
|---|---|
| `skip` (default) | Treat as an intentional decision (rejected/abandoned); do nothing |
| `recreate` | Redo the full action (clone, execute, commit, force-push) and open a brand-new PR |
| `reopen` | `gh pr reopen`, then force-push fresh content onto its existing branch — no new PR |
| `fail` | Record a failure requiring manual intervention; never silently redo or reopen |

**Default `skip`** — consistent with `onClosedIssue`'s default (`skip`, per `plans/closed-issue-options.md`'s amendment) and with `orcai nudge --on-closed-pr`'s default (`skip`). All three "was this closed on purpose?" settings in the codebase should agree.

Global default settable via `config.json` (`action.onClosedPr`, same convention as `action.writeBack`); overridden by the job YAML.

---

## New types

### `ClosedPrAction` reuse
`Domain.fs` already has `ClosedPrAction = Nudge | Skip | Fail` for `nudge`'s `--on-closed-pr`. That shape doesn't match what `cmd-to-github` needs (`recreate`/`reopen` instead of `nudge`). Add a **separate** type rather than overload the existing one — the two settings solve different problems (nudge re-triggers an assignee; this redoes/reopens a PR):

```fsharp
/// Behavior when cmd-to-github finds a closed (unmerged) PR for the branch.
type ClosedPrWriteBackAction = SkipClosedPr | RecreatePr | ReopenPr | FailOnClosedPr
```

### `CmdToGithubConfig` addition
```fsharp
type CmdToGithubConfig =
    { // ...existing fields...
      OnClosedPr : ClosedPrWriteBackAction option }  // None = resolve from OrcAIConfig at runtime, like WriteBack
```

---

## Files to change

| File | What changes |
|---|---|
| `src/OrcAI.Core/Domain.fs` | Add `ClosedPrWriteBackAction`; add `OnClosedPr` field to `CmdToGithubConfig` |
| `src/OrcAI.Core/YamlConfig.fs` | Parse `onClosedPr:` (job YAML), same `None`/string-match pattern as `writeBack` |
| `src/OrcAI.Core/OrcAIConfig.fs` | Add `action.onClosedPr` global default field, same convention as `action.writeBack` |
| `src/OrcAI.Core/RunCommand.fs` | Before the clone step in the `CmdToGithub` branch: call `FindPrsForIssue`, branch on state per the decision table; `git ls-remote` helper for the branch-exists check; wire `yamlHashChanged`/`templateHashChanged` (already in scope) into the branch |
| `src/OrcAI.Core/CheckoutManager.fs` | Add `branchExistsOnRemote` helper (`git ls-remote --heads`) |
| `src/OrcAI.GitHub/GhClient.fs` | `ReopenPr` method if not already present (check `ClosePr` sibling) |
| `docs/cli-reference.md`, `docs/config.md` | Document `onClosedPr` alongside `writeBack` |
| `tests/OrcAI.Core.Tests/RunCommandTests.fs`, `CheckoutManagerTests.fs` | New tests per row of the decision table |

---

## Test plan (bug-fix convention: failing test per behavior, then implement)

1. PR open, hashes unchanged → `execute` never invoked (assert via a `FakeGhClient`/counter that the checkout step is never reached).
2. PR open, hashes changed → `execute` invoked, branch force-pushed, no second PR created.
3. PR merged → skipped regardless of hash state.
4. PR closed, `onClosedPr: skip` (default) → no action taken.
5. PR closed, `onClosedPr: recreate` → full redo, new PR opened.
6. PR closed, `onClosedPr: reopen` → `gh pr reopen` called, branch force-pushed, no new PR.
7. No PR, branch exists on remote, hashes unchanged → clone/execute/push skipped, `gh pr create` retried directly.
8. No PR, branch exists, hashes changed → full redo (stale branch content is not trusted).
9. No PR, no branch → full run (today's behavior, unchanged).
10. All of the above with **no lock file present** (delete it before the test run) — must produce identical outcomes to the lock-file-present case, proving correctness doesn't depend on local state.

---

## Out of scope

- Detecting a PR was closed/merged *while `orcai run` isn't running* is inherently a live check — this plan adds it to `run` itself; it does not change `nudge`'s existing separate `--on-closed-pr` mechanism (different purpose: re-triggering an assignee, not managing the PR/branch).
- `gh pr edit` for title/body refresh on the "OPEN + hashes changed" path is included in scope for the branch/diff update, but a full templated-body-refresh parity with `refreshBodies` (issue side) is left as a follow-up if needed — flag it but don't block this plan on it.
