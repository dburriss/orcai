# OrcAI CLI Reference

Complete reference for all `orcai` commands, flags, configuration, and output formats.

---

## Table of Contents

- [Commands overview](#commands-overview)
- [generate](#generate)
- [auth](#auth)
  - [auth pat](#auth-pat)
  - [auth app](#auth-app)
  - [auth create-app](#auth-create-app)
  - [auth switch](#auth-switch)
- [run](#run)
- [validate](#validate)
- [info](#info)
- [cleanup](#cleanup)
- [YAML configuration](#yaml-configuration)
- [Layered configuration](#layered-configuration)
- [Lock file format](#lock-file-format)
- [Authentication](#authentication)
- [Environment variables](#environment-variables)
- [Exit codes](#exit-codes)

---

## Commands overview

| Command | Description |
|---------|-------------|
| `orcai generate` | Scaffold a YAML job config and a stub issue template |
| `orcai auth pat/app/create-app/switch` | Manage stored auth profiles and active credentials |
| `orcai run` | Execute a bulk upgrade job (supports globs, concurrency control, JSON output) |
| `orcai validate` | Validate YAML configs and verify repository access |
| `orcai info` | Display the current state of a job |
| `orcai cleanup` | Tear down everything created by `run` |

---

## generate

Scaffold a new YAML job definition and a Markdown issue template. This is the recommended starting point for any job.

```
orcai generate --name <name> --org <org> [--repo <repo>...] [--output <path>] [--skip-copilot] [--interactive]
```

### Flags

| Flag | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `--name` | string | Conditional | — | Job name used as both the GitHub Project title and issue title. Required unless `--interactive`. |
| `--org` | string | Conditional | — | GitHub organization slug. Required unless `--interactive`. |
| `--repo` | string | No | — | Repository short-name to include (exclude the `org/` prefix). Repeatable. |
| `--output` | string | No | `<slug>.yml` | Output YAML path. Defaults to the slugified job name in the current directory. |
| `--skip-copilot` | flag | No | false | Set `skipCopilot: true` in the generated config to avoid assigning `@copilot`. |
| `--interactive` | flag | No | false | Prompt for missing values and present an interactive Spectre.Console picker to choose repositories. |

### Output

Two files are created (or left untouched if already present):

- `<slug>.yml` — YAML job configuration ready for `orcai run`.
- `<slug>.md` — Issue template written only if the file does not already exist.

```
Generated:
  /path/to/my-job.yml
  /path/to/my-job.md
```

When `--interactive` is set and repos are not passed explicitly, OrcAI calls `gh repo list <org> --json name --limit 1000` and shows a paginated multi-select prompt (20 items per page). Use **Space** to toggle a repo and **Enter** to confirm.

---

## auth

Configure authentication for all other commands. Credentials are persisted to `~/.config/orcai/auth.json` and recalled on every invocation. Environment variables with the `ORCAI_*` prefix silently override stored values at runtime.

### auth pat

Store a GitHub Personal Access Token.

```
orcai auth pat --token <token>
```

#### Flags

| Flag | Type | Required | Description |
|------|------|----------|-------------|
| `--token` | string | Yes | GitHub PAT (e.g. `ghp_...`) with `repo`, `project`, and `issues` scopes. |

#### Behavior

1. Saves the token under the `pat` profile in `~/.config/orcai/auth.json` and marks it as active.
2. Validates the token by running `gh auth status` with it injected as `GH_TOKEN`.
3. Prints the validation output on success.

#### Example

```
orcai auth pat --token ghp_xxxxxxxxxxxxxxxxxxxx
```

### auth app

Store GitHub App credentials for CI-friendly authentication.

```
orcai auth app --app-id <id> --key <path> --installation-id <id>
```

#### Flags

| Flag | Type | Required | Description |
|------|------|----------|-------------|
| `--app-id` | string | Yes | GitHub App ID (integer shown on the App general settings page). |
| `--key` | string | Yes | Path to the PEM private key file (PKCS#1 or PKCS#8). |
| `--installation-id` | string | Yes | Installation ID for the target organization. |

#### Behavior

1. Stores the details under a profile named after the App ID and sets it as active.
2. Generates a short-lived JWT (RS256, 10-minute TTL with 60-second clock-skew buffer).
3. Exchanges the JWT for an installation token via `/app/installations/{id}/access_tokens`.
4. Validates the token with `gh auth status`.

#### Example

```
orcai auth app \
  --app-id 123456 \
  --key /path/to/private-key.pem \
  --installation-id 78901234
```

### auth create-app

Register a GitHub App automatically via the manifest flow, save the PEM key, and print next steps for installing the app.

```
orcai auth create-app [--app-name <name>] [--org <org>] [--port <port>]
```

#### Flags

| Flag | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `--app-name` | string | Conditional | `orcai-<org>-gh-app` when `--org` is supplied; required if `--org` is omitted | Name for the new GitHub App. |
| `--org` | string | Conditional | — | Register the app under an organization. Required unless `--app-name` is provided (name must be supplied to register under a user account). |
| `--port` | int | No | `9876` | Local callback port for the OAuth manifest redirect. |

#### Behavior

1. Spins up a temporary HTTP server and redirects the browser to GitHub with a manifest payload.
2. Receives the conversion response, saves the PEM to `~/.config/orcai/<app-name>.pem`, and prints where to install the app.
3. In interactive mode, prompts for the installation ID to complete the `auth app` config; otherwise, instructs the user to run `orcai auth app --app-id ... --key ... --installation-id <id>` when ready.

#### Example

```
orcai auth create-app --app-name orca-bot --org my-org
orcai auth create-app --port 8080 --app-name personal-orcai
```

### auth switch

Change the active profile stored in `~/.config/orcai/auth.json` without re-running `auth pat/app`.

```
orcai auth switch <profile>
```

Switch only succeeds if the target profile exists in the config file. Use `orcai auth` to list or edit profiles manually.

---

## run

Execute a bulk upgrade job defined in one or more YAML files. Globs are supported when quoted (e.g. `"jobs/*.yml"`).

```
orcai run <yaml_file_or_glob> [--verbose] [--auto-create-labels] [--skip-copilot]
         [--skip-lock] [--max-concurrency <n>] [--no-parallel] [--continue-on-error]
         [--json]
```

### Arguments and flags

| Argument / Flag | Type | Required | Default | Description |
|-----------------|------|----------|---------|-------------|
| `<yaml_file_or_glob>` | positional | Yes | — | YAML job file path or quoted glob (e.g. `"jobs/*.yml"`). |
| `--verbose` | flag | No | false | Emit per-repo status messages to stderr. |
| `--auto-create-labels` | flag | No | false | Create missing labels in each repo before applying them. |
| `--skip-copilot` | flag | No | false | Skip assigning `@copilot`. Honored even if the flag is set in `job.skipCopilot`. |
| `--skip-lock` | flag | No | false | Always fetch live state from GitHub instead of using the lock file. |
| `--max-concurrency` | int | No | `4` | Maximum number of config files processed concurrently. High values may hit GitHub rate limits. |
| `--no-parallel` | flag | No | false | Disable all parallelism — files and repo checks run sequentially. Overrides `--max-concurrency`. |
| `--continue-on-error` | flag | No | false | Continue processing remaining files when one fails instead of stopping. |
| `--json` | flag | No | false | Emit machine-readable JSON output to stdout instead of the human summary. |

### Behavior

For each repository listed in every config file (processed concurrently by default):

1. Finds or creates the GitHub Project (idempotent).
2. Finds or creates an issue using `job.title` and the issue template. Open issues are matched by title.
3. Adds the issue to the project.
4. Assigns `@copilot` if the issue has no assignees, unless skipped.

When processing multiple files, the human-readable output prints `--- <filename> ---` before each file's summary.

### Lock files

Successful runs write a `<basename>.lock.json` next to the YAML file. Subsequent runs skip network calls if the YAML hash matches. Delete the lock file, modify the YAML, or pass `--skip-lock` to force a fresh sync.

### JSON output (`--json`)

The output is a filename-keyed JSON object:

```json
{
  "jobs/my-upgrade.yml": {
    "created": 3,
    "alreadyExisted": 1,
    "repos": [
      { "repo": "org/repo-one", "issueNumber": 7, "status": "created" },
      { "repo": "org/repo-two", "issueNumber": 12, "status": "alreadyExisted" }
    ]
  },
  "jobs/other.yml": {
    "error": "YAML 'job.title' is required."
  }
}
```

### Examples

```sh
orcai run jobs/my-upgrade.yml
orcai run jobs/my-upgrade.yml --auto-create-labels --skip-copilot
orcai run "jobs/*.yml" --continue-on-error --json
orcai run "jobs/*.yml" --max-concurrency 2
orcai run "jobs/*.yml" --no-parallel
```

---

## validate

Validate YAML job configs and confirm all listed repositories are accessible.

```
orcai validate <yaml_file_or_glob> [--no-parallel] [--max-concurrency <n>] [--continue-on-error] [--json]
```

### Flags

| Argument / Flag | Type | Required | Default | Description |
|-----------------|------|----------|---------|-------------|
| `<yaml_file_or_glob>` | positional | Yes | — | Path or glob for config file(s). |
| `--no-parallel` | flag | No | false | Validate files and repos sequentially. Overrides `--max-concurrency`. |
| `--max-concurrency` | int | No | `4` | Concurrent config files validated. |
| `--continue-on-error` | flag | No | false | Continue validating remaining files after a failure. |
| `--json` | flag | No | false | Emit machine-readable JSON instead of human output. |

### Behavior

For each file:

1. Parses the YAML.
2. Validates the schema (required fields, issue template exists).
3. Calls `gh repo view <org/repo>` for each repo to ensure access.

Exit code `0` if every file and repo is valid; `1` otherwise.

### JSON output

```json
{
  "jobs/my-upgrade.yml": {
    "valid": true,
    "configErrors": [],
    "repoErrors": []
  },
  "jobs/broken.yml": {
    "valid": false,
    "configErrors": ["YAML 'job.title' is required."],
    "repoErrors": [
      { "repo": "my-org/missing", "error": "Could not resolve to a Repository." }
    ]
  }
}
```

---

## info

Show the current state of a job using the lock file or live GitHub data.

```
orcai info <yaml_file> [--skip-lock] [--save-lock] [--json]
```

### Flags

| Flag | Type | Required | Description |
|------|------|----------|-------------|
| `--skip-lock` | flag | No | Always fetch live state from GitHub. |
| `--save-lock` | flag | No | After fetching live state, overwrite the lock file. |
| `--json` | flag | No | Emit JSON instead of the Spectre.Console tables. |

### Output

- **Metadata grid** — project title, URL, data source (`lock file` or `GitHub (live)`), lock timestamp, YAML SHA-256 hash, repo count, issue count, PR count.
- **Issues table** — one row per repo with repo link, issue number, linked PRs, and assignees.

### JSON output

```json
{
  "project": "my-org / My Upgrade",
  "url": "https://github.com/users/my-org/projects/13",
  "source": "lock file",
  "lockedAt": "2026-03-02T20:34:21.046+00:00",
  "yamlHash": "...",
  "repoCount": 2,
  "issueCount": 2,
  "prCount": 1,
  "issues": [
    { "repo": "my-org/repo-one", "issueNumber": 7, "prNumbers": [3], "assignees": ["copilot"] }
  ]
}
```

---

## cleanup

Tear down every resource created by `orcai run` for a YAML config.

```
orcai cleanup <yaml_file> [--dryrun] [--force] [--json]
```

### Flags

| Flag | Type | Required | Description |
|------|------|----------|-------------|
| `--dryrun` | flag | No | Show what would be deleted without making changes. |
| `--force` | flag | No | Skip the interactive confirmation prompt. |
| `--json` | flag | No | Emit JSON describing cleaned-up resources. |

### Behavior

1. Locates every managed issue, closes any open PRs that reference it, then deletes the issue.
2. Deletes the GitHub Project.
3. Removes the lock file.

If the lock file exists, it is used to find exact project/issue numbers; otherwise heuristics (project title, issue title) are used.

### Dry-run output

```
DRY RUN: Would close PR #3 in my-org/repo-one
DRY RUN: Would delete issue #7 in my-org/repo-one
DRY RUN: Would delete project "My Upgrade" (#13) in my-org
Dry run complete. No changes were made.
```

### JSON output

```json
{
  "dryRun": true,
  "resources": [
    { "type": "pr", "repo": "org/repo", "number": 3, "org": null, "name": null },
    { "type": "project", "org": "org", "name": "My Upgrade", "number": 13 }
  ]
}
```

---

## YAML configuration

```yaml
job:
  title: "My Project Title"
  org:   "my-github-org"
  skipCopilot: false

repos:
  - "repo-one"
  - "repo-two"

issue:
  template: "./issue-body.md"
  labels:
    - "migration"
    - "automated"
```

`job.title` and `job.org` are required. `repos` must be a non-empty list. `issue.template` must point to a real Markdown file relative to the YAML. Missing labels will cause an error during `run` unless `--auto-create-labels` is supplied.

---

## Layered configuration

OrcAI loads configuration from two JSON files:

1. **Global** – `~/.config/orcai/config.json`
2. **Local** – `.orcai/config.json` in the current working directory (takes precedence over global).

Each file can contain these optional fields:

| Field | Description |
|-------|-------------|
| `skipCopilot` | Set default `--skip-copilot`. |
| `defaultLabels` | List of labels applied to every issue. |
| `autoCreateLabels` | Default for `--auto-create-labels`. |
| `maxConcurrency` | Default for `--max-concurrency`. |
| `continueOnError` | Default for `--continue-on-error`. |
| `defaultOrg` | Default GitHub org for `generate`.

Values from the local config override the global config when present.

Example `~/.config/orcai/config.json`:

```json
{
  "skipCopilot": true,
  "defaultLabels": ["migration", "orcai"],
  "maxConcurrency": 3
}
```

---

## Lock file format

Lock files are written as `<basename>.lock.json` next to the YAML config.

```json
{
  "lockedAt": "2026-03-02T20:34:21.046+00:00",
  "yamlHash": "<sha256>",
  "project": {
    "org": "my-org",
    "number": 13,
    "title": "My Project Title",
    "url": "https://github.com/users/my-org/projects/13"
  },
  "repos": ["my-org/repo-one", "my-org/repo-two"],
  "issues": [
    {
      "repo": "my-org/repo-one",
      "number": 7,
      "url": "https://github.com/my-org/repo-one/issues/7",
      "assignees": ["copilot"]
    }
  ],
  "pullRequests": [
    {
      "repo": "my-org/repo-one",
      "number": 3,
      "url": "https://github.com/my-org/repo-one/pull/3",
      "closesIssue": 7
    }
  ]
}
```

Lock files are consumed by `run`, `info`, and `cleanup`. Deleting the file forces a live refresh.

---

## Authentication

Credentials are resolved in the following order (highest priority first):

1. `ORCAI_PAT` environment variable.
2. GitHub App environment variables: `ORCAI_APP_ID`, `ORCAI_APP_INSTALLATION_ID`, `ORCAI_APP_PRIVATE_KEY` or `ORCAI_APP_KEY_PATH`.
3. Stored config in `~/.config/orcai/auth.json` (`pat` or `app` profile).
4. `GH_TOKEN` environment variable.
5. Ambient `gh auth` session (`gh auth token`).

When using a GitHub App, `orcai` generates a JWT, exchanges it for an installation token, and injects the token into `gh` subprocesses.

---

## Environment variables

| Variable | Description |
|----------|-------------|
| `ORCAI_PAT` | GitHub PAT. Overrides stored PAT config. |
| `ORCAI_APP_ID` | GitHub App ID. Overrides stored profile value. |
| `ORCAI_APP_INSTALLATION_ID` | Installation ID. Overrides stored value. |
| `ORCAI_APP_PRIVATE_KEY` | Raw PEM content for the App key (highest priority). |
| `ORCAI_APP_KEY_PATH` | Path to the App PEM file. Ignored if `ORCAI_APP_PRIVATE_KEY` is set. |
| `GH_TOKEN` | GitHub token used when no OrcAI-specific credentials are available. |

No message is printed when env vars override stored config.

---

## Exit codes

| Code | Meaning |
|------|---------|
| `0` | Success |
| `1` | Error — message printed to stderr |

Subcommands return `1` when validation fails, GitHub calls fail, or `run`/`validate` encounter invalid files.
