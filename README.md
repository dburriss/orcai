# OrcAI CLI

<p align="center">
  <img src="assets/orcai_banner.png" alt="OrcAI" />
</p>

A CLI tool for orchestrating bulk GitHub work across many repositories. From a single YAML config, OrcAI creates a GitHub Project, opens templated issues in every target repo, and hands them off to whoever (or whatever) does the work — a human teammate, a bot, or an AI agent like GitHub Copilot or OpenCode.

## Features

- **Declarative YAML jobs** — one config defines the project, target repos, issue template, assignment behaviour, nudge policy, and notification template.
- **Bulk, idempotent issue creation** across any number of repos — a lock file makes re-runs free; glob and brace expansion (`"jobs/**/*.{yml,yaml}"`) for fanning out.
- **GitHub Project auto-management** — finds or creates the board and links every issue to it.
- **Assign to anyone** — `assign.to` accepts any GitHub user, bot, or GitHub App handle. Default is `@copilot`, but humans, OpenCode, and custom App bots all work.
- **Comment-based triggers** — `assign.via: comment` posts a slash command (e.g. `/opencode`) instead of assigning, for agents that listen for mentions. `comment-and-assign` does both.
- **Tag anyone in templated comments** — `assign.comment`, `nudge.comment`, and `notify` all support `{assignee}`, `{job.owner}`, and `{repo.codeowners}` tokens, resolved from YAML and CODEOWNERS files at runtime.
- **`orcai nudge`** — re-trigger stale issues (no linked PR yet) by reassignment, comment, or both.
- **`orcai notify`** — broadcast a templated comment to issues and/or PRs from the lock file; filter by state, dry-run, and inject extra `--data key=value` template variables.
- **Auto issue-body updates** — when the Markdown template changes, existing issues' bodies are updated without re-running the structural work (hash-based detection).
- **Dependent jobs** — `dependsOn` gates a downstream job on the completion of an upstream one (`pr_merged` or `issue_closed`), either per-repo (filter eligible repos individually) or all-repos (block the run until the entire upstream batch is done). `orcai run` resolves the full dependency chain automatically; `orcai graph` renders it as an ASCII tree; `orcai validate` catches cycles and missing upstream files.
- **Robust at scale** — built-in rate limiting (60 writes/min, configurable) with exponential-backoff retry; closed-issue policy (`create`/`reopen`/`skip`/`fail`); concurrency control; `--continue-on-error`; JSON output for CI.
- **Multiple auth methods** — ambient `gh` CLI, PAT, or GitHub App (manifest flow supported via `orcai auth create-app`). A PAT is only required when the assignee is `@copilot`.

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

`run` finds or creates a GitHub Project, creates issues from your template, adds them to the project, and triggers the configured assignee — whether that's `@copilot`, another bot, an AI agent like OpenCode, or a human teammate. Triggering can be via assignment, a templated comment (e.g. a slash command), or both. On success a lock file (`<basename>.lock.json`) is written alongside the YAML for fast idempotent re-runs.

## Commands

| Command | Description |
|---------|-------------|
| `orcai auth pat/app/create-app/switch` | Store credentials or switch profiles for all other commands |
| `orcai generate` | Scaffold a YAML job config and stub issue template |
| `orcai run` | Execute a bulk upgrade job (supports globs, concurrency control, JSON output) |
| `orcai nudge` | Re-trigger stale issues with no linked PR (reassign, comment, or both) |
| `orcai notify` | Post a templated comment to issues and/or PRs from the lock file |
| `orcai validate` | Validate YAML config(s) and verify all repos are accessible |
| `orcai info` | Display the current state of a job |
| `orcai cleanup` | Tear down everything created by `run` |
| `orcai graph` | Render the `dependsOn` dependency graph as an ASCII tree |

For full flag details, output formats, lock file schema, and advanced usage see [docs/cli-reference.md](docs/cli-reference.md). For config file settings see [docs/config.md](docs/config.md).

The original Nushell scripts (`orca.nu`, `cleanup.nu`) are documented in [docs/nushell-scripts.md](docs/nushell-scripts.md).
