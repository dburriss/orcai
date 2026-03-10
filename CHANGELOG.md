# Changelog

## [Unreleased]

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
