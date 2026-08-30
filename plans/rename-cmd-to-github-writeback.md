# Plan: Rename `cmd-to-pr` action type and write-back modes

## Goal

Fix misleading naming in the checkout-based action type:

- `cmd-to-pr` implies a PR is always opened, but the `commit-to-origin` write-back
  mode never opens one — it just pushes a branch. The action type name should not
  promise an outcome one of its own modes doesn't deliver.
- The whole action type shells out to `gh` for push/PR operations, so it is
  GitHub-only. Nothing in the current names signals that, which matters now that
  `provider: local` exists alongside `provider: github`.
- `commit-to-origin` is also misleading on its own: it does not commit to origin's
  default branch, it pushes a new branch to origin (same as `pr-to-origin` minus the
  `gh pr create` step).

Everything below is still `[Unreleased]` in `CHANGELOG.md` — no shipped release uses
these names, so this is a straight rename with no back-compat aliases needed.

## Renames

| old | new |
|---|---|
| action type `cmd-to-pr` | `cmd-to-github` |
| `CmdToPrConfig` (type) | `CmdToGithubConfig` |
| `ActionConfig.CmdToPr` (case) | `ActionConfig.CmdToGithub` |
| write-back mode `pr-to-origin` | `open-pr` |
| write-back mode `commit-to-origin` | `push-branch` |
| write-back mode `fork-and-pr` | `fork-and-pr` (unchanged) |
| `WriteBackMode.PrToOrigin` | `WriteBackMode.OpenPr` |
| `WriteBackMode.CommitToOrigin` | `WriteBackMode.PushBranch` |
| `WriteBackMode.ForkAndPr` | `WriteBackMode.ForkAndPr` (unchanged) |
| `RepoFailureCategory.CmdToPr*` cases | `RepoFailureCategory.CmdToGithub*` |

`writeBack:` stays as the YAML/config field name (it's already accurate — it
describes writing changes back to GitHub, and covers all three modes including the
no-PR one).

## Files to change

Source:
- `src/OrcAI.Core/Domain.fs` — `WriteBackMode`, `CmdToPrConfig`, `ActionConfig.CmdToPr`, `RepoFailureCategory.CmdToPr*`, doc comments
- `src/OrcAI.Core/YamlConfig.fs` — action-type string match (`"cmd-to-pr"`), write-back mode string match (`"pr-to-origin"`, `"commit-to-origin"`, `"fork-and-pr"`), error message listing valid action types
- `src/OrcAI.Core/RunCommand.fs` — `effectiveWriteBack` match arms, config-default string match, log messages
- `src/OrcAI.Core/CheckoutManager.fs` — comment referencing `cmd-to-pr`
- `src/OrcAI.Core/LockFile.fs` — any `CmdToPr*` failure-category references
- `src/OrcAI.Tool/Program.fs` — any references (generate scaffolding, help text, etc.)

Tests:
- `tests/OrcAI.Core.Tests/YamlConfigTests.fs`
- `tests/OrcAI.Core.Tests/CheckoutManagerTests.fs`

Docs:
- `docs/config.md`
- `docs/cli-reference.md`
- `docs/app-auth.md`
- `docs/AUTH-ENV-VARS.md`
- `CHANGELOG.md` (update the `[Unreleased]` entries in place — no shipped version to preserve history for)

Examples:
- `example/opencode-cmd-to-pr/` — rename directory to `example/opencode-cmd-to-github/`, update `add-agents-md.yml`, `orcai-bulk-pr.yml`, `README.md`
- `example/README.md`

Other plans referencing old names (update for consistency, don't rewrite their history/intent):
- `plans/checkout-action-types.md`
- `plans/checkout-auth-token-propagation.md`
- `plans/copy-into-cmd-actions.md`
- `plans/local-provider.md`
- `plans/split-provider-interface.md`

Knowledge base:
- `knowledge/opencode-headless-cli.md`

## Steps

1. Rename in `Domain.fs` (types, cases, doc comments) — this drives compiler errors
   everywhere else that needs updating.
2. Fix `YamlConfig.fs` parsing (action-type match, write-back mode match, error
   message text) and its tests.
3. Fix `RunCommand.fs` (match arms on `WriteBackMode`, default-resolution string
   match, log/error strings) and `CheckoutManager.fs` comment.
4. Fix `LockFile.fs` / `Program.fs` if the compiler flags anything there.
5. Run `dotnet build` to confirm no remaining references to old identifiers; run
   `dotnet test` to confirm existing tests pass with updated names.
6. Update docs (`config.md`, `cli-reference.md`, `app-auth.md`,
   `AUTH-ENV-VARS.md`, `CHANGELOG.md`).
7. Rename `example/opencode-cmd-to-pr/` → `example/opencode-cmd-to-github/` and fix
   its YAML/README contents; update `example/README.md`.
8. Update cross-references in other `plans/*.md` files and `knowledge/*.md`.

## Out of scope

- No new write-back modes or behavior changes — this is a pure rename.
- No backward-compatible aliases for old YAML values (`cmd-to-pr`,
  `pr-to-origin`, `commit-to-origin`) since nothing has shipped yet.
