# Changelog

## [Unreleased]

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
