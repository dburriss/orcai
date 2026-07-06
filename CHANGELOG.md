# Changelog

## [Unreleased]

### Added

- `action:` YAML field — typed, explicit action to execute after issue creation. Supported types:
  - `assign-copilot` (default when `action:` is absent) — assigns `@copilot`, with an optional trigger comment.
  - `assign` — assigns any GitHub user or bot (`to` required, `comment` optional).
  - `comment` — posts a comment only, no assignment (`comment` required).
  - `comment-and-assign` — posts a comment then assigns (`to` and `comment` required).
  - `cmd` — runs a shell command or script per repo (`execute` for a script path, `run` for an inline command; mutually exclusive). Supports `args` and `cwd`. Template variables use `{{var}}` syntax: `{{repo}}`, `{{org}}`, `{{issue_number}}`, `{{issue_url}}`, `{{job_title}}`, `{{issue_text}}`, `{{issue_hash}}`, `{{yaml_hash}}`, `{{project_number}}`, `{{run_datetime}}`.
  - `noop` — skip the action step entirely (replaces `job.skipCopilot: true`).
  - `cmd-checkout` — clones the target repo (bare, `--depth 1`) and runs the command inside it. Worktrees are reused when the same repo appears across multiple jobs. Extra template variables: `{{checkout_path}}` and `{{job_title_slug}}`.
  - `cmd-to-pr` — checkout → run → commit all changes → push branch → open PR. Supports three write-back modes: `pr-to-origin` (default), `commit-to-origin`, and `fork-and-pr`. Optional fields: `branch`, `commitMessage`, `prTitle`, `prBody`, `errorIfNoDiff`.

- New global config fields (`~/.config/orcai/config.json` / `.orcai/config.json`):
  - `checkoutRoot` — override the directory where repos are cloned for `cmd-checkout` and `cmd-to-pr`. Defaults to an OS temp directory scoped to the run.
  - `writeBack` — global default write-back mode for `cmd-to-pr` (`pr-to-origin` | `commit-to-origin` | `fork-and-pr`). Overridden by `writeBack` in the job YAML.

- **GitHub App permission**: The **Contents** permission on the GitHub App must now be set to **Read & write** (instead of Read) to support push-based action types (`cmd-to-pr` with `pr-to-origin` or `commit-to-origin`). OrcAI configures git credentials automatically using the same token — no separate credential setup is required.

### Changed

- **BREAKING**: `onClosedIssue` default changed from `create` to `skip`. Previously, when a closed issue with a matching title was found, OrcAI would open a new issue alongside it. Now it treats the closed issue as already done and skips the repo. To restore the old behaviour, add `onClosedIssue: create` to the `job:` block in your YAML. The `redoOnClosed` YAML field and config option (added as a workaround for the wrong default on checkout actions) have been removed; use `onClosedIssue: create` instead.
- **BREAKING**: `assign:` YAML block removed. Validation fails with a migration message when `assign:` is present. Migrate to `action: { type: assign-copilot, ... }` or the appropriate action type.
- **BREAKING**: `job.skipCopilot` removed. Validation fails with a migration message when present. Use `action: { type: noop }` to skip assignment, or omit `action:` to assign `@copilot`.
- **BREAKING**: `--skip-copilot` CLI flag removed from `orcai run` and `orcai generate`. Use `action: { type: noop }` in the YAML instead.
- **BREAKING**: `skipCopilot` and `assign` fields removed from the global/local JSON config (`~/.config/orcai/config.json`). `action:` is per-job only.
- `orcai generate` no longer generates a `skipCopilot` comment line; generates an `action:` comment block instead.
- `orcai nudge` and `orcai notify` derive the `{assignee}` template variable from the job's `action:` type rather than `assign.to`.

---

### Added

- `dependsOn` YAML field — gates a downstream job on the completion state of one or more upstream jobs. Each entry specifies a `job` (relative path), `condition` (`pr_merged` | `issue_closed`), `scope` (`per_repo` | `all_repos`), and `untrackedRepos` (`include` | `skip`). Multiple entries use AND logic.
- `orcai run` now resolves `dependsOn` chains in topological order before executing. Passing a downstream YAML is sufficient — upstream dependencies are discovered and run automatically. The `scope: all_repos` option blocks the entire downstream run when any upstream repo has not met the condition; `scope: per_repo` (default) filters the downstream repo list individually.
- `orcai graph <yaml>` — new command that renders the `dependsOn` dependency tree as an ASCII diagram. File-system only; no GitHub API calls. Supports `--json` output.
- `orcai validate` now detects circular `dependsOn` references and missing upstream files, reporting them as configuration errors.
- `orcai nudge --on-closed-pr` — controls what happens when the only PRs found for an issue are closed without merging. Values: `skip` (default — don't nudge), `nudge` (re-trigger the assignee anyway), `fail` (report as a failure). Merged PRs are always treated as done and never trigger this flag.

### Changed

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
