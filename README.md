# OrcAI CLI

A CLI tool for automating bulk GitHub repository upgrades. It reads a YAML job configuration file and manages GitHub Projects, issues, and Copilot assignments across multiple repositories.

## Installation

OrcAI is distributed as a .NET global tool. Requires [.NET 10](https://dotnet.microsoft.com/download) or later.

```bash
dotnet tool install --global OrcAI.Tool
```

Then run it as `orcai`.

## Prerequisites

- **`gh` CLI**: Install from [cli.github.com](https://cli.github.com/) — must be installed and on `PATH`
- **Authentication**: The easiest option is to ensure `gh` is authenticated (`gh auth login`). OrcAI will use it automatically. For other methods see [docs/cli-reference.md](docs/cli-reference.md).

## Quick start

### 1. Authenticate

The simplest option — if you already use the `gh` CLI, just make sure it's authenticated:

```bash
gh auth login
```

That's it. OrcAI will pick up the token automatically.

For PAT, GitHub App, or environment variable auth see [docs/cli-reference.md](docs/cli-reference.md).

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
| `orcai auth pat/app/create-app/switch` | Store credentials or switch profiles for all other commands |
| `orcai generate` | Scaffold a YAML job config and stub issue template |
| `orcai run` | Execute a bulk upgrade job (supports globs, concurrency control, JSON output) |
| `orcai validate` | Validate YAML config(s) and verify all repos are accessible |
| `orcai info` | Display the current state of a job |
| `orcai cleanup` | Tear down everything created by `run` |

For full flag details, output formats, lock file schema, and advanced usage see [docs/cli-reference.md](docs/cli-reference.md).

The original Nushell scripts (`orca.nu`, `cleanup.nu`) are documented in [docs/nushell-scripts.md](docs/nushell-scripts.md).
