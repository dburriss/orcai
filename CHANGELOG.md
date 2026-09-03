# Changelog

## [Unreleased]

## [0.10.4] - 2026-09-03

### Fixed

- `FetchReposState`'s two batches (`isArchived` and `search`, split in 0.10.3) now fetch their chunks concurrently instead of one at a time. Splitting the `search` batch down to 15 repos increased the number of sequential GraphQL round trips (e.g. 13 for 152 repos, up from 4); since this prefetch always runs single-threaded ahead of the per-repo fan-out regardless of `--parallel`/`--no-parallel`, running it sequentially made it the dominant cost of the whole run. Chunks are now dispatched with `Async.Parallel`, bounded/paced by the existing shared `ApiBucket` (which already rate-limits concurrent callers and pauses all of them together on a detected rate limit).

## [0.10.3] - 2026-09-02

### Fixed

- Repos were misreported as "not found or inaccessible" in large, chunk-boundary-aligned blocks during bulk repo-state fetch (`FetchReposState`), even though they exist and are accessible. Root cause: GitHub's edge/gateway can return a valid-JSON but non-GraphQL-shaped error envelope (e.g. `{"message": "We couldn't respond to your request in time..."}`, typically a 502/504 gateway timeout) for an expensive query — previously this was treated as a successful response with no data, so every repo in the chunk fell into the "not found" branch. It's now detected and treated as a retryable transient error like any other backoff-eligible failure.
- Reduced how often that gateway timeout is hit in the first place: `FetchReposState` now issues `isArchived` lookups (cheap) in batches of 100 as before, but the two `search` lookups per repo (open/closed issue, one of GitHub's most expensive GraphQL root fields) in much smaller batches of 15 — down from a combined batch of 50 that could put up to 100 `search` invocations in a single GraphQL call.

## [0.10.2] - 2026-09-02

### Fixed

- `orcai run --on-closed-issue` help text incorrectly said `create (default)`; the actual default is `skip` (since the 0.9.0 breaking change).
- Fixed misreporting and crashes when GitHub rate limits large jobs (100+ repos):
  - Repos are no longer misreported as "not found" when GitHub returns a secondary-rate-limit error during bulk repo/issue lookups (`ReposExist`/`FetchReposState`) — these errors are global GraphQL errors without a per-repo `path`, and were previously silently dropped instead of triggering a retry.
  - A malformed/non-JSON API response (e.g. an HTML rate-limit/abuse-detection page returned with a 200) no longer crashes the run with an uncaught JSON parse exception; it's now caught, classified as rate-limit-flavored, and retried.
  - Detecting a rate limit now pauses all concurrent in-flight/queued repo checks (not just the one that hit it) until the backoff window elapses, instead of letting other repos keep hammering the API. The internal request bucket also resets to empty for the pause, so requests resume at the normal pace once it lifts instead of releasing a stampede from a bucket that silently refilled during the pause.

## [0.10.1] - 2026-09-01

### Added

- `{repo}.replace.md` per-repo issue template override: replaces the base template content for that repo, while `{repo}.prepend.md` / `{repo}.append.md` still wrap around it if also present. Follows the same optional/independent, hash-based change detection as the existing prepend/append overrides.

## [0.10.0] - 2026-08-30

### Added

- Per-repo issue template overrides: `{repo}.prepend.md` / `{repo}.append.md` files placed next to `issue.template` are automatically prepended/appended to that repo's issue body. Both are optional and independent — a missing file is not an error, and repos without a matching file get the base template unchanged. No new YAML fields required. Editing an override file alone still updates existing issue bodies on the next `orcai run`, via the existing template-hash change detection.

## [0.9.0] - 2026-08-30

### Added

- `provider:` YAML field — selects the issue/project tracking backend for a job.
  - `type: github` (default; omitting `provider:` entirely preserves current behaviour, no change needed to existing YAMLs).
  - `type: local` — tracks project/issue state as YAML + Markdown files on disk instead of calling the GitHub API. Useful for trying out a job's fan-out behaviour without touching GitHub, or for tracking work in a repo synced via git rather than the GitHub API.
  - `root:` (optional, `local` only) — where the local store is written, resolved relative to the YAML file's directory (same convention as `issue.template`). Defaults to `.orcai-local` next to the YAML file.
  - A job using `provider: local` never requires or resolves GitHub authentication — `orcai run`/`generate`/etc. work with no `GH_TOKEN`, PAT, App credentials, or `gh` CLI login at all.
  - Known limitations: `provider: local` has no PR tracking or bulk repo inspection. `orcai nudge`'s closed-PR handling and `orcai info`'s PR summary always show zero PRs for local jobs, and any `dependsOn` condition needing bulk repo state is unavailable.
- `--provider local` flag on `orcai generate` — scaffolds a job YAML with the `provider: { type: local }` block instead of the default (GitHub).
- `action:` YAML field — typed, explicit action to execute after issue creation. Supported types:
  - `assign-copilot` (default when `action:` is absent) — assigns `@copilot`, with an optional trigger comment.
  - `assign` — assigns any GitHub user or bot (`to` required, `comment` optional).
  - `comment` — posts a comment only, no assignment (`comment` required).
  - `comment-and-assign` — posts a comment then assigns (`to` and `comment` required).
  - `cmd` — runs a shell command or script per repo (`execute` for a script path, `run` for an inline command; mutually exclusive). Supports `args` and `cwd`. Template variables use `{{var}}` syntax: `{{repo}}`, `{{org}}`, `{{issue_number}}`, `{{issue_url}}`, `{{job_title}}`, `{{issue_text}}`, `{{issue_hash}}`, `{{yaml_hash}}`, `{{project_number}}`, `{{run_datetime}}`.
  - `noop` — skip the action step entirely (replaces `job.skipCopilot: true`).
  - `cmd-checkout` — clones the target repo (bare, `--depth 1`) and runs the command inside it. Worktrees are reused when the same repo appears across multiple jobs. Extra template variables: `{{checkout_path}}` and `{{job_title_slug}}`.
  - `cmd-to-github` — checkout → run → commit all changes → push branch → open PR. Supports three write-back modes: `open-pr` (default), `push-branch`, and `fork-and-pr`. Optional fields: `branch`, `commitMessage`, `prTitle`, `prBody`, `errorIfNoDiff`.
    - `open-pr`/`fork-and-pr` append a `Closes #{{issue_number}}` line to the PR body by default (unless the body already references the issue), so GitHub links the PR to the issue and auto-closes it on merge.
    - The push (`open-pr`/`push-branch`/`fork-and-pr`) always uses `git push --force`. The orcai-owned branch is always force-pushed by design, and every run starts from a fresh clone with no local remote-tracking ref for it, so `--force-with-lease` would reject the push as stale as soon as any prior run had already pushed to that branch.
    - Per-step failures (`CmdToGithubCheckoutFailed`/`CmdToGithubNoDiff`/`CmdToGithubPushFailed`/`CmdToGithubOpenPrFailed`) are persisted in the lock file and cleared again the next time the step succeeds, so a one-off failure doesn't get reported forever.
- `copy:` list on `cmd`, `cmd-checkout`, and `cmd-to-github` actions — Docker-`COPY`-style staging of input files (e.g. a helper script or prompt file) from where `orcai` is invoked into the command's working directory before it runs. Each entry has `from` (static path or glob; zero matches is a hard error), `to` (exact file path on a single match, destination directory on a glob match), and `keep` (default `false` — deletes the copied file(s) again after the command finishes; for `cmd-to-github` this happens before the commit, so scratch files never leak into the PR diff).
- New global config fields (`~/.config/orcai/config.json` / `.orcai/config.json`):
  - `checkoutRoot` — override the directory where repos are cloned for `cmd-checkout` and `cmd-to-github`. Defaults to an OS temp directory scoped to the run.
  - `action.writeBack` — global default write-back mode for `cmd-to-github` (`open-pr` | `push-branch` | `fork-and-pr`). Nested under `action` in the config JSON. Overridden by `writeBack` in the job YAML.
  - `action.onClosedPr` — global default for `cmd-to-github`'s `onClosedPr` (`skip` | `recreate` | `reopen` | `fail`). Nested under `action` in the config JSON. Overridden by `onClosedPr` in the job YAML.
- `cmd-to-github` (`open-pr`/`fork-and-pr` write-back modes) is now idempotent by design instead of only by accident of a clean lock file. Before cloning, `orcai run` checks live GitHub state (a PR linked to the issue via `closingPullRequests`, and whether the branch already exists on the remote) — never the lock file, so behaviour is identical whether or not one is present:
  - An **open** PR with unchanged YAML/template hashes is left alone entirely — no clone, no re-run of `execute`, no push. This holds even with no lock file present at all (deleted, or a fresh checkout on CI): with no prior lock to compare against, the content is assumed unchanged rather than treated as changed, so a missing lock file never forces a redo/force-push of an already-open PR.
  - A **merged** PR is always left alone, regardless of hash changes.
  - A **closed** (unmerged) PR is handled per the new `onClosedPr` field (see below).
  - When no PR is found but the branch already exists on the remote and hashes are unchanged, only `gh pr create` is retried against the existing branch — still no clone/execute/push.
  - `onClosedPr` field on `cmd-to-github` (job YAML, mirrors `job.onClosedIssue`'s shape) — controls what happens when the only PR found for the branch is closed without merging. Values: `skip` (default — treat as an intentional decision, do nothing), `recreate` (redo the full run and open a brand-new PR), `reopen` (`gh pr reopen` plus a force-push of fresh content to the existing branch, no new PR), `fail` (record a failure requiring manual intervention). Has no effect with `writeBack: push-branch`, which never creates a PR.
- **GitHub App permission**: The **Contents** permission on the GitHub App must now be set to **Read & write** (instead of Read) to support push-based action types (`cmd-to-github` with `open-pr` or `push-branch`). OrcAI injects the resolved App/PAT token into the checkout git/PR subprocesses automatically — no separate credential setup is required.
- `dependsOn` YAML field — gates a downstream job on the completion state of one or more upstream jobs. Each entry specifies a `job` (relative path), `condition` (`pr_merged` | `issue_closed`), `scope` (`per_repo` | `all_repos`), and `untrackedRepos` (`include` | `skip`). Multiple entries use AND logic.
- `orcai run` now resolves `dependsOn` chains in topological order before executing. Passing a downstream YAML is sufficient — upstream dependencies are discovered and run automatically. The `scope: all_repos` option blocks the entire downstream run when any upstream repo has not met the condition; `scope: per_repo` (default) filters the downstream repo list individually.
- `orcai graph <yaml>` — new command that renders the `dependsOn` dependency tree as an ASCII diagram. File-system only; no GitHub API calls. Supports `--json` output.
- `orcai validate` now detects circular `dependsOn` references and missing upstream files, reporting them as configuration errors.
- `orcai nudge --on-closed-pr` — controls what happens when the only PRs found for an issue are closed without merging. Values: `skip` (default — don't nudge), `nudge` (re-trigger the assignee anyway), `fail` (report as a failure). Merged PRs are always treated as done and never trigger this flag.
- `orcai migrate <yaml>` — upgrades a job YAML and its sibling `.lock.json` in place to the current schema version, preserving old runtime behaviour (e.g. `assign:`/`skipCopilot` → the equivalent `action:` block, `onClosedIssue` default preserved explicitly). The lock file step is purely local — no GitHub calls — so migrating never forces a job's repos back onto the live lookup path the way deleting the lock file would. Supports `--dryrun` and `--json`; always backs up any file it changes to `<file>.bak` first. Designed to extend to future schema hops (v2→v3, etc.) without restructuring — each hop is an independent step.

### Changed

- **BREAKING**: Issue and project identifiers are now opaque strings instead of GitHub-shaped integers (internal groundwork for supporting non-GitHub providers in the future). User-visible effects:
  - **Lock files**: the `.lock.json` format has changed. Existing lock files fail to load with a message pointing at `orcai migrate <yaml>` (upgrades the lock file in place, no GitHub calls) as the recommended fix; deleting the file and re-running `orcai run` still works but re-fetches state from GitHub for every repo.
  - **`--json` output**: issue and project numbers in `orcai run --json`, `orcai info --json`, and `orcai cleanup --json` are now emitted as JSON strings instead of numbers (e.g. `"issueNumber": "42"` instead of `"issueNumber": 42`). Human-readable console output (e.g. `#42`) is unchanged.
- **BREAKING**: `onClosedIssue` default changed from `create` to `skip`. Previously, when a closed issue with a matching title was found, OrcAI would open a new issue alongside it. Now it treats the closed issue as already done and skips the repo. To restore the old behaviour, add `onClosedIssue: create` to the `job:` block in your YAML. The `redoOnClosed` YAML field and config option (added as a workaround for the wrong default on checkout actions) have been removed; use `onClosedIssue: create` instead.
- **BREAKING**: `assign:` YAML block removed. Validation fails with a migration message when `assign:` is present. Migrate to `action: { type: assign-copilot, ... }` or the appropriate action type.
- **BREAKING**: `job.skipCopilot` removed. Validation fails with a migration message when present. Use `action: { type: noop }` to skip assignment, or omit `action:` to assign `@copilot`.
- **BREAKING**: `--skip-copilot` CLI flag removed from `orcai run` and `orcai generate`. Use `action: { type: noop }` in the YAML instead.
- **BREAKING**: `skipCopilot` and `assign` fields removed from the global/local JSON config (`~/.config/orcai/config.json`). `action:` is per-job only.
- **BREAKING**: Top-level `writeBack` config key moved to `action.writeBack`. Update config files from `"writeBack": "..."` to `"action": { "writeBack": "..." }`.
- `orcai generate` no longer generates a `skipCopilot` comment line; generates an `action:` comment block instead.
- `orcai generate` now scaffolds a `version: 2` field at the top of every generated job YAML, so newly-created files self-declare their schema version the same way `orcai migrate` stamps it onto upgraded ones. The field is inert today (ignored by the parser) but lets future schema migrations detect a file's version without a structural heuristic.
- `orcai nudge` and `orcai notify` derive the `{assignee}` template variable from the job's `action:` type rather than `assign.to`.
- `orcai nudge` now surfaces PR state when checking for existing PRs. The `state` field (`OPEN`, `CLOSED`, `MERGED`) is stored on PR entries in the lock file; old lock files without the field default to `OPEN` on load.
- `orcai nudge` no longer treats a closed PR in the lock file as "PR exists — skip". Only open PRs in the lock suppress the live check. Closed PR entries (e.g. written by `orcai info --save-lock`) are now ignored by the lock-file fast-path, so nudge correctly proceeds to a live GitHub check for those issues.
- `orcai nudge --save-lock` now persists all discovered PRs with their state to the lock file, so closed PRs are visible via `orcai info`.

## [0.8.1] - 2026-06-16

### Added

- `verbose` flag on `orcai verify` command — prints detailed per-repo validation results to stderr

### Changed

- `orcai verify` does a single call now to check repositories but will ignore those already in the lock file
- performance improvements to `orcai verify` by using GraphQL to fetch multiple repositories in a single request instead of one request per repository
- performance improvements to `orcai run` by using GraphQL to fetch multiple repositories in a single request instead of one request per repository

## [0.8.0] - 2026-06-09

### Added

- `orcai run --dryrun` — preview what would be created, reopened, or updated without making any GitHub API calls or writing the lock file. Read-only lookups still run so the preview reflects current state. Outcomes are reported per repo as `would create`, `would reopen`, or `would update`, with a summary line and `dryRunWouldCreate` / `dryRunWouldReopen` / `dryRunWouldUpdate` counts in `--json`.

- `orcai notify` command — posts a templated comment to issues and/or PRs recorded in the lock file. Supports the same `{assignee}`, `{job.owner}`, and `{repo.codeowners}` template tokens as `nudge.comment`.
  - `--target issues|prs|both` — which lock file items to notify (default: `issues`).
  - `--state open|closed|all` — filter by current GitHub state before commenting (default: `open`); `closed` matches both closed and merged PRs; `all` skips the live state check entirely.
  - `--dryrun` — preview which items would be notified without posting any comments.
  - `--verbose` — print per-item progress to stderr.
  - `--template <string>` — inline comment template supplied directly on the CLI; overrides `notify.comment` from YAML/config.
  - `--data key=value` — inject an extra template variable (repeatable). E.g. `--data sprint=42`.
  - `--json-data <json>` — inject extra template variables as a JSON object string. Merged with `--data`; `--data` takes precedence on key conflicts. User-supplied values override built-in tokens (`{assignee}` etc.) when the same key is used.
- `notify` block in YAML job config and global/local JSON config — configures the comment template for `orcai notify`.
  - `notify.comment` — comment body template. Supports the same `{assignee}`, `{job.owner}`, and `{repo.codeowners}` tokens as `nudge.comment`.
- `orcai run` records repos that were skipped because they are archived in a new `skippedRepos` field in the lock file. The run summary and `--json` output include a `skippedArchived` count and status.
- `orcai run` detects when the lock file points to a deleted or transferred issue and recreates the issue in place instead of failing. New `staleIssueRecreated` count/status in the summary and `--json` output; the lock file is rewritten with the new issue numbers.
- `orcai run` now persists error information in the lock file when a repo fails to process, allowing errors to be surfaced in subsequent runs instead of being silently ignored as "not created". New `failures` field in the lock file maps repo, attempts, and action that failed.

### Changed

- `orcai nudge` and `orcai notify` rename `--dry-run` to `--dryrun` for consistency with `cleanup` (and the new `run --dryrun`). The old spelling is no longer accepted.

- Comment-building logic (template variable resolution + `PostComment`) extracted from `RunCommand` and `NudgeCommand` into a shared internal `Comments` module, used by all three comment-posting paths.

- `orcai run` now automatically updates issue bodies when the Markdown template changes. A `templateHash` field is stored in the lock file alongside the existing `yamlHash`, allowing the tool to detect which changed:
  - Either `.yml` or `.md` changed → structural re-run via `runFull`, honouring `onClosedIssue` policy. If the template hash changed, issue bodies are refreshed for any repos reconciled as `AlreadyExisted` or `Reopened`.
  - Neither changed → fast path, zero network calls (unchanged from before).
  - `--skip-lock` → structural re-run plus unconditional body refresh for `AlreadyExisted` / `Reopened`.
  - Old lock files without `templateHash` are treated as changed, triggering a one-time body sync on next run.

- `assign` block in YAML job config and global/local JSON config — configures who receives the issue and how they are triggered. Applies to both `orcai run` and `orcai nudge`.
  - `assign.to` — assignee handle (default: `@copilot`). Accepts any GitHub user, bot, or GitHub App bot handle. Note: assigning `@copilot` requires a PAT (`ORCAI_PAT`) regardless of primary auth method, as GitHub Copilot can only be assigned via a user-level token.
  - `assign.via` — trigger method: `assign` (default), `comment`, or `comment-and-assign`. Use `comment` for agents triggered by slash commands (e.g. OpenCode's `/opencode`).
  - `assign.comment` — comment body posted when `via` includes `comment`. Supports template tokens (see below).
- `nudge` block in YAML job config and global/local JSON config — configures how `orcai nudge` re-triggers the assignee on stale issues.
  - `nudge.mode` — `reassign` (default), `comment-only`, or `comment-and-reassign`.
  - `nudge.comment` — comment body posted on nudge. Supports template tokens (see below).
- Dynamic template tokens in `assign.comment` and `nudge.comment` — placeholders resolved at runtime:
  - `{assignee}` — the configured `assign.to` handle.
  - `{job.owner}` — who owns the orcai job. Resolved from `job.owner` in the YAML (highest priority), then the catch-all `*` owner from a `CODEOWNERS` file in the current repository (checked at `CODEOWNERS`, `.github/CODEOWNERS`, `docs/CODEOWNERS`). Left unreplaced if neither is found.
  - `{repo.codeowners}` — the catch-all `*` owner from the target repository's `CODEOWNERS` file (fetched from GitHub). Left unreplaced if no `CODEOWNERS` is present or it has no `*` rule.
- `job.owner` field in the YAML `job` block — statically sets the job owner for use in comment templates via `{job.owner}`. Overrides any CODEOWNERS-based discovery.
- `orcai nudge` command now documented in the CLI reference.
- Generated YAML scaffold now includes commented-out `assign:` and `nudge:` example blocks instead of the unused `copilot:` block.
- The `copilot:` block previously scaffolded by `orcai generate` has been removed. It was never parsed and is superseded by the `assign:` block.
- `--skip-copilot` is superseded by `assign.via: comment` (skips assignment while still allowing a trigger comment). The flag remains supported for backwards compatibility.
- `ORCAI_LOG_LEVEL` environment variable — controls log verbosity. Accepts any `Microsoft.Extensions.Logging.LogLevel` name (`Trace`, `Debug`, `Information`, `Warning`, `Error`, `Critical`, `None`). Defaults to `Warning`.
- Lock file schema: new `skippedRepos: string[]` field. Old lock files without this field still load (treated as empty); the field is populated on the next run.

### Fixed

- Template bumps now go through `runFull`, honouring `onClosedIssue` policy and refreshing the body of reopened issues. Previously, editing only the MD template could silently rewrite the body of a closed issue and skip the body refresh for reopened issues.

- `orcai run` no longer creates a duplicate issue when the GitHub API lookup itself fails. Transient `gh` errors during open- or closed-issue lookup (rate limits, network resets, exhausted retries) are now surfaced as a per-repo error instead of being silently treated as "no matching issue". This also restores `--on-closed-issue` semantics on lookup failures — the configured action (`reopen` / `skip` / `fail`) is no longer bypassed when the closed-issue query errors.

- Assignment via GitHub App auth now only requires a PAT (`ORCAI_PAT`) when the assignee is `@copilot`. Assigning human users or other bots with a GitHub App (which has `issues: write` permission) no longer warns or skips — the PAT constraint was previously applied to all assignees, not just Copilot.

- `orcai cleanup` no longer fails when a project, issue, or PR has already been deleted — the operation is treated as success and a warning is emitted instead.
- Issue lookup now uses GitHub's title search (`in:title`) with a 100-result limit, preventing missed matches on repos with more than 30 open or closed issues.
- PR lookup for an issue now queries GitHub's GraphQL API (`Issue.closingPullRequests`) instead of listing all PRs in the repo and filtering in memory — fixes silent data loss on repos with more than 30 PRs.
- `orcai run` no longer errors out on archived repositories. Each repo is pre-checked with `gh repo view --json isArchived`; archived repos are skipped with a single informational line instead of cascading `Repository was archived so is read-only` errors from label and issue writes.
- `orcai run --auto-create-labels` no longer produces spurious errors when a label already exists. Two fixes: (1) `gh label list` now uses `--limit 1000` so labels past page 1 are detected by the pre-check, and (2) `CreateLabel` is idempotent — a GitHub "already exists" / "already been taken" response is downgraded to success with a warning log.

## [0.6.0] - 2026-05-07

### Added

- Brace expansion in glob patterns — `"jobs/**/*.{yml,yaml}"` now matches both `.yml` and `.yaml` files in a single invocation, for `orcai run` and `orcai validate`.
- `onClosedIssue` field in YAML job config and `--on-closed-issue` flag on `orcai run` — controls behaviour when a matching closed issue already exists. Valid values: `create` (default, creates a new issue), `reopen` (reopens the closed issue), `skip` (leaves the repo untouched), `fail` (exits with an error).
- Run summary and `--json` output now include `reopened` and `skipped` counts when `--on-closed-issue` is `reopen` or `skip`.
- GitHub write calls are now rate-limited with a token-bucket (default 60 writes/min) and automatically retried with exponential backoff on rate-limit errors (up to 3 retries, starting at 60s, doubling each time, capped at 5 min).
- `writesPerMinute` and `rateLimitRetries` config keys — override the rate-limit defaults in `~/.config/orcai/config.json` or `.orcai/config.json`.

## [0.5.1] - 2026-03-17

### Fixed

- Fixed order of PAT and GitHub App authentication methods — PAT is now correctly used as a fallback when App auth fails due to insufficient permissions (e.g. for Copilot assignment), instead of being used as the primary method and causing failures when only App credentials are provided. Updated documentation to clarify this behavior. 

## [0.5.0] - 2026-03-16

### Added

- Use a PAT token in combination with GitHub App authenication to support assigning Copilot (since GitHub Apps don't have permission to assign Copilot, even if they have org-level permissions)

## [0.4.4] - 2026-03-16

### Added

- Extra callout in `auth create-app` instructions to guide users through the manual steps required to grant org permissions after app creation via the manifest flow. Permissions must be set before installing.

### Fixed

- Fixed a bug where `lockFilePath` produced backslashes on Windows, causing CI test failures; now produces forward-slash paths for consistency across platforms. Validated with unit tests on Windows and Linux.
- ORCAI_APP_PRIVATE_KEY environment variable is now supported for CI usage, allowing users to avoid writing the private key to disk. Updated documentation and CI example to reflect this.
 
## [0.4.3] - 2026-03-16

### Fixed

- Fixed a bug where `lockFilePath` produced backslashes on Windows, causing CI test failures; now produces forward-slash paths for consistency across platforms

## [0.4.2] - 2026-03-16

- scout: ENV VAR naming cleanup. Should have no user-facing impact since this is mostly a document update.

## [0.4.1] - 2026-03-15

### Fixed

- `lockFilePath` now produces forward-slash paths on Windows, fixing CI test failures

## [0.4.0] - 2026-03-13

### Added

- `orcai validate` command — validates one or more YAML job configs and verifies all listed repos are accessible via `gh repo view`; supports `--json`, `--no-parallel`, `--max-concurrency`, and `--continue-on-error`
- Glob pattern support for `orcai run` and `orcai validate` — pass a quoted glob (e.g. `"jobs/*.yml"`) to process multiple config files in one invocation
- `--max-concurrency <n>` flag on `orcai run` and `orcai validate` — limits the number of config files processed concurrently (default: 4); high values may hit GitHub rate limits
- `--no-parallel` flag on `orcai run` and `orcai validate` — disables all parallelism (both file-level and repo-level); overrides `--max-concurrency`
- `--continue-on-error` flag on `orcai run` and `orcai validate` — continues processing remaining files after a failure instead of stopping on the first error
- `--skip-lock` flag on `orcai run` — bypasses the lock file and always fetches live state from GitHub
- Layered config file support — `~/.config/orcai/config.json` (global) and `.orcai/config.json` (local, takes precedence); supports `skipCopilot`, `defaultLabels`, `autoCreateLabels`, `maxConcurrency`, `continueOnError`, and `defaultOrg`

### Changed

- [BREAKING] `orcai run --json` output shape changed to a filename-keyed object to support multi-file runs; field names also changed: `issuesCreated` → `created`, `issuesAlreadyExisted` → `alreadyExisted`, `issues` → `repos`; a per-file `"error"` key is included on failure
- [BREAKING] `orcai validate --json` output is now a filename-keyed object (consistent with `run --json`)
- Human-readable output for multi-file `run` and `validate` now prefixes each file's output with a `--- <filename> ---` header

### Fixed

- Fixed repo accessibility check in validation scripts

## [0.3.0] - 2026-03-11

### Changed

- [BREAKING] Renamed package from `Orca.Tool` to `OrcAI.Tool` and CLI command from `orca` to `orcai`
- [BREAKING] Renamed config directory from `~/.config/orca/` to `~/.config/orcai/`
- [BREAKING] Renamed environment variables: `ORCA_PAT` → `ORCAI_PAT`, `ORCA_APP_ID` → `ORCAI_APP_ID`, `ORCA_APP_INSTALLATION_ID` → `ORCAI_APP_INSTALLATION_ID`, `ORCA_APP_KEY_PATH` → `ORCAI_APP_KEY_PATH`, `ORCA_APP_PRIVATE_KEY` → `ORCAI_APP_PRIVATE_KEY`

## [0.2.1] - 2026-03-10

### Fixed

- Fixed removing of old pem file on Windows

## [0.2.0] - 2026-03-10

### Added

- `--json` flag on `orca info` — emits machine-readable JSON to stdout instead of the rich console output
- `--json` flag on `orca run` — emits a JSON summary of created/already-existing issues instead of the human-readable output
- `--json` flag on `orca cleanup` — emits a JSON list of cleaned-up (or would-be-cleaned-up) resources; includes a `dryRun` boolean so callers can tell whether changes were actually made
- `--force` flag on `orca cleanup` — skips the interactive confirmation prompt; cleanup proceeds immediately without asking
- Improved printed instructions and error messages for `orca auth create-app` to guide users through the manual steps required to grant org permissions after app creation via the manifest flow

### Fixed

- Fixed bug with `orca auth create-app` where the redirect URL only worked for organization-owned apps, not user-owned apps. The redirect URL is now determined dynamically based on the `owner.type` field in the manifest conversion response.

### Changed

- Orca.Cli renamed to Orca.Tool to avoid conflicts with other tools named "orca" and to make it clearer that this is a CLI tool. The command syntax remains `orca <command>` for ease of use.

## [0.1.1] - 2026-03-03

### Added

- `orca run` command — creates a GitHub Project, issues, and Copilot assignments from a YAML config file
- `orca cleanup` command — tears down a project, issues, and related PRs
- `orca info` command — displays project state from lock file
- `orca auth` command — configures PAT or GitHub App authentication
- Lock file support — idempotent runs tracked via `*.lock.json` alongside the YAML config
- `orca generate` command — generates a YAML config from a list of repos or orgs
