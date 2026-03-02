# Changelog

## [Unreleased]

## [0.1.0] - 2026-03-02

### Added
- `orca run` command — creates a GitHub Project, issues, and Copilot assignments from a YAML config file
- `orca cleanup` command — tears down a project, issues, and related PRs
- `orca info` command — displays project state from lock file
- `orca auth` command — configures PAT or GitHub App authentication
- Lock file support — idempotent runs tracked via `*.lock.json` alongside the YAML config
- Nushell reference scripts (`orca.nu`, `cleanup.nu`) for Phase 1 usage
