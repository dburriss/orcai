# Orca CLI — Project Overview

## Goal

Orca is a CLI tool for automating bulk GitHub repository upgrades. Given a YAML configuration file describing a project, target repositories, and an issue template, Orca creates and manages all the associated GitHub entities — projects, issues, and Copilot assignments — in one command. It also handles teardown and provides visibility into the current state of a job.

---

## Features

### `run` command

Executes a job defined in a YAML configuration file. For each repository listed:

- Creates a GitHub Project for the org (idempotent — skips creation if it already exists).
- Creates an issue in each repository using the provided issue template (idempotent — skips if the issue already exists).
- Adds each issue to the GitHub Project.
- Assigns the issue to `@copilot` if it has no assignees.

Supports a `--verbose` flag for detailed output.

---

### `cleanup` command

Tears down everything created by a `run` command for the same YAML configuration:

- Closes any open PRs that reference the managed issues.
- Deletes the managed issues from each repository.
- Deletes the GitHub Project.

Supports a `--dryrun` flag to preview what would be deleted without making any changes.

---

### Lock files

When a job is run, Orca can persist a lock file alongside the YAML configuration file (e.g. `<name>.lock.json`). The lock file captures a snapshot of the job at the time it was run, including:

- Date the lock was created.
- A hash of the original YAML configuration.
- The GitHub Project details.
- The list of repositories.
- The issues created (URLs, numbers, repos).
- Any associated PRs.
- Assignees at the time of the run.

Lock files allow subsequent commands to work without making additional GitHub API calls.

---

### `info` command

Displays the current state of a job defined in a YAML configuration file.

- By default, reads from the lock file if one exists.
- If no lock file is present, fetches live state from GitHub by looking up each entity.
- Supports a `--no-lock` flag to bypass the lock file and always fetch live state.
- Supports a `--save-lock` flag to write a new lock file after fetching live state.

---

### `auth` command

Configures authentication for Orca. Two modes are supported (mutually exclusive):

- **PAT token**: For local use or simple automation. Stores a GitHub Personal Access Token with the necessary project and issue permissions.
- **GitHub App**: For CI pipelines. Authenticates using a GitHub App installation, enabling Orca to run without a personal token.

---

## Authentication summary

| Context | Method |
|---|---|
| Local development | `gh auth login` or PAT via `auth` command |
| CI pipeline | `GH_TOKEN` environment variable or GitHub App via `auth` command |
