# Changelog

## [Unreleased]

### Added

- `assign` block in YAML job config and global/local JSON config — configures who receives the issue and how they are triggered. Applies to both `orcai run` and `orcai nudge`.
  - `assign.to` — assignee handle (default: `@copilot`). Works for bots, GitHub Apps, and human users.
  - `assign.via` — trigger method: `assign` (default), `comment`, or `comment-and-assign`. Use `comment` for agents triggered by slash commands (e.g. OpenCode's `/opencode`).
  - `assign.comment` — comment body posted when `via` includes `comment`. Supports `{assignee}` placeholder.
- `nudge` block in YAML job config and global/local JSON config — configures how `orcai nudge` re-triggers the assignee on stale issues.
  - `nudge.mode` — `reassign` (default), `comment-only`, or `comment-and-reassign`.
  - `nudge.comment` — comment body posted on nudge. Supports `{assignee}` placeholder.
- `orcai nudge` command now documented in the CLI reference.
- Generated YAML scaffold now includes commented-out `assign:` and `nudge:` example blocks instead of the unused `copilot:` block.

### Changed

- The `copilot:` block previously scaffolded by `orcai generate` has been removed. It was never parsed and is superseded by the `assign:` block.
- `--skip-copilot` is superseded by `assign.via: comment` (skips assignment while still allowing a trigger comment). The flag remains supported for backwards compatibility.

- `ORCAI_LOG_LEVEL` environment variable — controls log verbosity. Accepts any `Microsoft.Extensions.Logging.LogLevel` name (`Trace`, `Debug`, `Information`, `Warning`, `Error`, `Critical`, `None`). Defaults to `Warning`.

### Fixed

- `orcai cleanup` no longer fails when a project, issue, or PR has already been deleted — the operation is treated as success and a warning is emitted instead.
- Issue lookup now uses GitHub's title search (`in:title`) with a 100-result limit, preventing missed matches on repos with more than 30 open or closed issues.
- PR lookup for an issue now queries GitHub's GraphQL API (`Issue.closingPullRequests`) instead of listing all PRs in the repo and filtering in memory — fixes silent data loss on repos with more than 30 PRs.

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
