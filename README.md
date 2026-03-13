# OrcAI CLI

A CLI tool for automating bulk GitHub repository upgrades. It reads a YAML job configuration file and manages GitHub Projects, issues, and Copilot assignments across multiple repositories.

## Prerequisites

- **`gh` CLI**: Install from [cli.github.com](https://cli.github.com/) — must be installed and on `PATH`
- **Authentication**: One of the following must be configured before running any command other than `orcai auth`:
  1. Stored PAT — `ORCAI_PAT` env var, or `~/.config/orcai/auth.json` with `"type": "pat"`
  2. Stored GitHub App — `ORCAI_APP_ID` / `ORCAI_APP_INSTALLATION_ID` / `ORCAI_APP_KEY_PATH` / `ORCAI_APP_PRIVATE_KEY` env vars, or `~/.config/orcai/auth.json` with `"type": "app"`
  3. `GH_TOKEN` environment variable
  4. Ambient `gh` CLI auth (`gh auth token`)

## Quick start

### 1. Authenticate

```bash
# Store a Personal Access Token
orcai auth pat --token ghp_xxxxxxxxxxxxxxxxxxxx

# — or — store GitHub App credentials
orcai auth app --app-id 123456 --key /path/to/private-key.pem --installation-id 78901234

# — or — register a new GitHub App via browser
orcai auth create-app --app-name orca-bot --org my-org
```

See [docs/app-auth.md](docs/app-auth.md) for a GitHub App walkthrough and [docs/AUTH-ENV-VARS.md](docs/AUTH-ENV-VARS.md) for environment variable reference.

### 2. Run a job

```bash
# Single config file
orcai run jobs/my-upgrade.yml

# All configs in a directory (quote the glob to prevent shell expansion)
orcai run "jobs/*.yml" --continue-on-error --json

# Limit concurrency to avoid rate limits
orcai run "jobs/*.yml" --max-concurrency 2
```

`run` finds or creates a GitHub Project, creates issues from your template, adds them to the project, and optionally assigns `@copilot`. On success a lock file (`<basename>.lock.json`) is written alongside the YAML for fast idempotent re-runs.

## Commands

| Command | Description |
|---------|-------------|
| `orca auth pat/app/create-app` | Store credentials for all other commands |
| `orca generate` | Scaffold a YAML job config and stub issue template |
| `orca run` | Execute a bulk upgrade job (supports globs, concurrency control, JSON output) |
| `orca validate` | Validate YAML config(s) and verify all repos are accessible |
| `orca info` | Display the current state of a job |
| `orca cleanup` | Tear down everything created by `run` |

For full flag details, output formats, lock file schema, and advanced usage see [docs/cli-reference.md](docs/cli-reference.md).

The original Nushell scripts (`orca.nu`, `cleanup.nu`) are documented in [docs/nushell-scripts.md](docs/nushell-scripts.md).
