# OrcAI CLI

A CLI tool for orchestrating bulk GitHub work across many repositories. From a single YAML config, OrcAI creates a GitHub Project, opens templated issues in every target repo, and hands them off to whoever (or whatever) does the work — a human teammate, a bot, or an AI agent like GitHub Copilot or OpenCode.

Full docs, features, and examples: [github.com/dburriss/orca](https://github.com/dburriss/orca)

## Prerequisites

- [`gh` CLI](https://cli.github.com/) installed and on `PATH`
- `gh auth login` (or a PAT/GitHub App — see the [CLI reference](https://github.com/dburriss/orca/blob/main/docs/cli-reference.md#authentication))

## Quick start

```bash
# 1. Scaffold a job config + issue template
orcai generate --name "Add AGENTS.md" --org my-github-org --repo repo-one --repo repo-two

# 2. Edit add-agents-md.yml (repos, labels, action) and add-agents-md.md (the task)

# 3. Run it
orcai run add-agents-md.yml
```

`run` finds or creates a GitHub Project, creates issues from your template, adds them to the project, and executes the configured `action` — assign `@copilot`/anyone, post a comment, or run a command per repo and open a PR with the result (`cmd-to-github`). A `<basename>.lock.json` file is written alongside the YAML so re-runs are fast and idempotent.

## Commands

| Command | Description |
|---------|-------------|
| `orcai generate` | Scaffold a YAML job config and stub issue template |
| `orcai run` | Execute a bulk job (globs, concurrency control, JSON output) |
| `orcai nudge` | Re-trigger stale issues with no linked PR |
| `orcai notify` | Post a templated comment to issues and/or PRs |
| `orcai validate` | Validate YAML config(s) and repo access |
| `orcai info` | Display the current state of a job |
| `orcai cleanup` | Tear down everything created by `run` |
| `orcai graph` | Render the `dependsOn` dependency graph |
| `orcai migrate` | Upgrade a job YAML/lock file to the current schema |
| `orcai auth pat/app/create-app/switch` | Manage credentials and profiles |

For full flag details, the YAML schema, config file options, and runnable examples, see the [CLI reference](https://github.com/dburriss/orca/blob/main/docs/cli-reference.md), [config reference](https://github.com/dburriss/orca/blob/main/docs/config.md), and [examples](https://github.com/dburriss/orca/tree/main/example).
