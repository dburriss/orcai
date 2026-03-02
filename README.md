# Orca CLI

A CLI tool for automating bulk GitHub repository upgrades. It reads a YAML job configuration file and manages GitHub Projects, issues, and Copilot assignments across multiple repositories.

## Prerequisites

- **`gh` CLI**: Install from [cli.github.com](https://cli.github.com/) — must be installed and on `PATH`
- **Authentication**: One of the following must be configured before running any command other than `orca auth`:
  1. Stored PAT — `ORCA_PAT` env var, or `~/.config/orca/auth.json` with `"type": "pat"`
  2. Stored GitHub App — `ORCA_APP_ID` / `ORCA_APP_INSTALLATION_ID` / `ORCA_APP_KEY_PATH` / `ORCA_APP_PRIVATE_KEY` env vars, or `~/.config/orca/auth.json` with `"type": "app"`
  3. `GH_TOKEN` environment variable
  4. Ambient `gh` CLI auth (`gh auth token`)

## Usage

### YAML Config Format

All data commands require a YAML file:

```yaml
job:
  title: "My Project Title"
  org:   "my-github-org"

repos:
  - "repo-one"
  - "repo-two"

issue:
  template: "./issue-body.md"
  labels: ["migration", "automated"]
```

### auth

Store credentials for use by all other commands. Two subcommands are available: `pat` and `app`.

**PAT:**

```
orca auth pat --token <token>
```

| Flag | Required | Description |
|------|----------|-------------|
| `--token` | Yes | GitHub Personal Access Token (`ghp_...`). Requires `repo` and `project` scopes. |

**GitHub App:**

```
orca auth app --app-id <id> --key <path> --installation-id <id>
```

| Flag | Required | Description |
|------|----------|-------------|
| `--app-id` | Yes | GitHub App ID (shown on the App settings page) |
| `--key` | Yes | Path to the PEM private key file |
| `--installation-id` | Yes | Installation ID for the target organisation |

Credentials are stored in `~/.config/orca/auth.json` and validated immediately. Environment variables (`ORCA_PAT`, `ORCA_APP_ID`, etc.) override stored values at runtime without modifying the file.

See [docs/app-auth.md](docs/app-auth.md) for more details setting up GitHub App authentication.

### run

Execute a bulk upgrade job. For each repository in the YAML, orca will:

1. Find or create the GitHub Project for the org (idempotent)
2. Find or create an issue using the issue template (idempotent)
3. Add the issue to the GitHub Project (idempotent)
4. Assign `@copilot` to the issue if no assignees are set

```
orca run <yaml_file> [--verbose]
```

| Argument / Flag | Required | Description |
|-----------------|----------|-------------|
| `<yaml_file>` | Yes | Path to the YAML job configuration file |
| `--verbose` | No | Emit detailed per-repo progress messages |

On success a lock file (`<basename>.lock.json`) is written alongside the YAML. On subsequent runs, if the YAML is unchanged the lock file is used to short-circuit all network calls.

### info

Display a formatted snapshot of the current state of a job.

```
orca info <yaml_file> [--no-lock] [--save-lock]
```

| Argument / Flag | Required | Description |
|-----------------|----------|-------------|
| `<yaml_file>` | Yes | Path to the YAML job configuration file |
| `--no-lock` | No | Bypass the lock file and fetch live state from GitHub |
| `--save-lock` | No | After fetching live state from GitHub, persist a new lock file |

By default, reads from the lock file if it exists (no network calls). Use `--no-lock --save-lock` to force a fresh fetch and update the lock file.

### cleanup

Tear down everything that `run` created for the same YAML configuration.

```
orca cleanup <yaml_file> [--dryrun]
```

| Argument / Flag | Required | Description |
|-----------------|----------|-------------|
| `<yaml_file>` | Yes | Path to the YAML job configuration file |
| `--dryrun` | No | Preview all deletions without making any changes |

Closes open PRs linked to each issue, deletes each issue, deletes the GitHub Project, and removes the lock file.

---

# Orca Nushell Scripts

This repository contains Nushell scripts for managing GitHub projects and issues in bulk.

## Prerequisites

- **Nushell**: Install Nushell from [nushell.sh](https://www.nushell.sh/)
- **GitHub CLI**: Install the GitHub CLI from [cli.github.com](https://cli.github.com/)

## Authentication

### Local Environment
Run `gh auth login` to authenticate with GitHub CLI.

### CI Environment
Set the `GH_TOKEN` environment variable with a GitHub Personal Access Token that has the necessary permissions (project and issue management).

## Scripts

### orca.nu

Creates a GitHub project and adds issues to it across multiple repositories.

**Usage:**
```
./orca.nu [--verbose] <yaml_file>
```

**Options:**
- `--verbose`: Enable verbose output
- `yaml_file`: Path to YAML configuration file

**YAML Configuration Structure:**
```yaml
job:
  title: "Project Title"
  org: "organization-name"
repos:
  - "repo1"
  - "repo2"
issue:
  template: "path/to/issue-template.md"
  labels:
    - "label1"
    - "label2"
```

### cleanup.nu

Deletes a GitHub project and cleans up associated issues and PRs.

**Usage:**
```
./cleanup.nu [--dryrun] <yaml_file>
```

**Options:**
- `--dryrun`: Preview what would be deleted without actually deleting
- `yaml_file`: Path to YAML configuration file

**YAML Configuration Structure:**
```yaml
job:
  title: "Project Title"
  org: "organization-name"
```
