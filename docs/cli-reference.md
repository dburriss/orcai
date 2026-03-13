# Orca CLI Reference

Complete reference for all `orca` commands, flags, configuration, and output formats.

---

## Table of Contents

- [Commands overview](#commands-overview)
- [generate](#generate)
- [auth](#auth)
  - [auth pat](#auth-pat)
  - [auth app](#auth-app)
  - [auth create-app](#auth-create-app)
- [run](#run)
- [validate](#validate)
- [info](#info)
- [cleanup](#cleanup)
- [YAML configuration](#yaml-configuration)
- [Lock file format](#lock-file-format)
- [Authentication](#authentication)
- [Environment variables](#environment-variables)
- [Exit codes](#exit-codes)

---

## Commands overview

| Command | Description |
|---------|-------------|
| `orca generate` | Scaffold a new YAML job config and stub issue template |
| `orca auth pat` | Store a Personal Access Token |
| `orca auth app` | Store GitHub App credentials |
| `orca auth create-app` | Register a new GitHub App via browser |
| `orca run` | Execute a bulk upgrade job |
| `orca validate` | Validate a YAML job config and verify all repos are accessible |
| `orca info` | Display the current state of a job |
| `orca cleanup` | Tear down everything created by `run` |

---

## generate

Scaffold a new YAML job config file and a stub Markdown issue template. This is the recommended starting point for a new job.

```
orca generate --name <name> --org <org> [--repo <repo>...] [--output <path>] [--skip-copilot] [--interactive]
```

### Flags

| Flag | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `--name` | string | Conditional | — | Job name used as the GitHub Project title and issue title. Required unless `--interactive` is set. |
| `--org` | string | Conditional | — | GitHub organisation slug. Required unless `--interactive` is set. |
| `--repo` | string | No | — | Repo short-name to include (no `org/` prefix). Repeatable: `--repo repo-a --repo repo-b`. |
| `--output` | string | No | `<slug>.yml` | Output YAML file path. Defaults to the slugified name in the current directory. |
| `--skip-copilot` | flag | No | false | Emit `skipCopilot: true` in the generated config, disabling `@copilot` assignment. |
| `--interactive` | flag | No | false | Prompt for any missing values and show a paginated TUI multi-select for repo selection fetched live from GitHub. |

### Output

Two files are created (or updated):

- `<slug>.yml` — YAML job configuration file ready for use with `orca run`.
- `<slug>.md` — Stub Markdown issue template (only written if the file does not already exist).

The slug is derived from `--name`: lowercased, non-alphanumeric characters replaced with `-`, consecutive dashes collapsed, leading/trailing dashes trimmed.

```
Generated:
  /path/to/my-job.yml
  /path/to/my-job.md
```

### Interactive mode

When `--interactive` is set, orca calls `gh repo list <org> --json name --limit 1000` and presents a paginated Spectre.Console multi-select prompt (20 items per page). Use **Space** to toggle a repo and **Enter** to confirm the selection.

### Examples

```sh
# Minimal — generates my-upgrade.yml and my-upgrade.md
orca generate --name "My Upgrade" --org my-org

# With repos and a custom output path
orca generate --name "My Upgrade" --org my-org \
  --repo api-service --repo web-frontend \
  --output jobs/my-upgrade.yml

# Interactive — prompts for name/org and shows repo picker
orca generate --interactive
```

---

## auth

Configure authentication used by all other commands. Credentials are stored in `~/.config/orca/auth.json` and validated immediately after saving.

### auth pat

Store a GitHub Personal Access Token.

```
orca auth pat --token <token>
```

#### Flags

| Flag | Type | Required | Description |
|------|------|----------|-------------|
| `--token` | string | Yes | GitHub Personal Access Token (`ghp_...`). Requires `repo` and `project` scopes. |

#### Behavior

1. Saves the token to `~/.config/orca/auth.json` as `{"type":"pat","token":"..."}`.
2. Runs `gh auth status` with the token injected as `GH_TOKEN`.
3. Prints the validation output on success.

#### Example

```sh
orca auth pat --token ghp_xxxxxxxxxxxxxxxxxxxx
```

---

### auth app

Store GitHub App credentials.

```
orca auth app --app-id <id> --key <path> --installation-id <id>
```

#### Flags

| Flag | Type | Required | Description |
|------|------|----------|-------------|
| `--app-id` | string | Yes | GitHub App ID (integer shown on the App settings page) |
| `--key` | string | Yes | Path to the PEM private key file. PKCS#1 and PKCS#8 formats are both accepted. |
| `--installation-id` | string | Yes | Installation ID for the target organisation |

#### Behavior

1. Saves config to `~/.config/orca/auth.json` as `{"type":"app",...}`.
2. Generates a short-lived JWT (RS256, 10-minute TTL, 60-second clock-skew buffer).
3. Exchanges the JWT for an installation access token via `POST /app/installations/{id}/access_tokens`.
4. Validates the token with `gh auth status`.

#### Example

```sh
orca auth app \
  --app-id 123456 \
  --key /path/to/private-key.pem \
  --installation-id 78901234
```

See [app-auth.md](app-auth.md) for a full walkthrough including how to create the App and find the IDs.

---

### auth create-app

Register a new GitHub App via the GitHub App Manifest flow (browser-based).

```
orca auth create-app [--app-name <name>] [--org <org>] [--port <port>]
```

#### Flags

| Flag | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `--app-name` | string | No | `orca` | Name for the new GitHub App |
| `--org` | string | No | — | Register the app under an organisation (omit to register under your personal account) |
| `--port` | int | No | `9876` | Local port for the OAuth callback redirect |

#### Behavior

1. Starts a local HTTP server on `localhost:<port>`.
2. Opens the default browser to the local server, which auto-submits a manifest form to GitHub.
3. Waits up to 120 seconds for GitHub to redirect back with a `?code=` parameter.
4. Exchanges the code for app credentials (App ID, PEM key, webhook secret) via `POST /app-manifests/{code}/conversions`.
5. Saves the PEM to `~/.config/orca/app.pem`.
6. Writes partial config to `~/.config/orca/auth.json` (without installation ID).
7. In interactive mode, prints step-by-step instructions for finding the installation ID, then prompts for it and completes validation. In non-interactive mode, prints the same instructions along with the command to run after installing.

The app is created with these permissions: Issues (write), Pull requests (read), Metadata (read), Organization projects (write), Projects (write). No webhooks are configured.

#### Example

```sh
# Register under an organisation
orca auth create-app --app-name orca-bot --org my-org

# Register under your personal account with a custom port
orca auth create-app --port 8080
```

---

## run

Execute a bulk upgrade job against every repository listed in the YAML config. Accepts a single file path or a glob pattern to run multiple configs at once.

```
orca run <yaml_file_or_glob> [--verbose] [--auto-create-labels] [--skip-copilot]
         [--skip-lock] [--max-concurrency <n>] [--no-parallel] [--continue-on-error]
         [--json]
```

### Arguments and flags

| Argument / Flag | Type | Required | Default | Description |
|-----------------|------|----------|---------|-------------|
| `<yaml_file_or_glob>` | positional | Yes | — | Path or glob pattern for YAML job config file(s). Quote glob patterns to prevent shell expansion (e.g. `"configs/*.yaml"`). |
| `--verbose` | flag | No | false | Emit detailed per-repo progress messages to stderr |
| `--auto-create-labels` | flag | No | false | Create any labels that don't exist in a repo before applying them to the issue |
| `--skip-copilot` | flag | No | false | Skip assigning `@copilot` to issues. Also honoured if `job.skipCopilot: true` is set in the YAML. |
| `--skip-lock` | flag | No | false | Bypass the lock file and always fetch live state from GitHub |
| `--max-concurrency` | int | No | `4` | Maximum number of config files processed concurrently. High values may hit GitHub rate limits. |
| `--no-parallel` | flag | No | false | Disable all parallelism — files are processed sequentially and repo checks within each file also run sequentially. Overrides `--max-concurrency`. |
| `--continue-on-error` | flag | No | false | Continue processing remaining files when one fails, instead of stopping on the first error. |
| `--json` | flag | No | false | Emit machine-readable JSON output to stdout (see [JSON output](#run-json-output) below). |

### What it does

For each repository in the YAML (processed in parallel by default):

1. **Project** — finds or creates the GitHub Project for the org (idempotent).
2. **Issue** — finds or creates an issue with `job.title` as the title and the template file as the body (idempotent, matched on open issues by title).
3. **Project card** — adds the issue to the project (idempotent).
4. **Copilot** — assigns `@copilot` to the issue if it has no assignees, unless `--skip-copilot` or `job.skipCopilot: true`.

When processing multiple files (via glob), each file's output is preceded by a `--- <filename> ---` header.

### Lock file

On success, a lock file `<basename>.lock.json` is written next to the YAML. On subsequent runs, if the YAML content is unchanged (SHA-256 match), the lock file is used to short-circuit all network calls. To force a re-run, delete the lock file, use `--skip-lock`, or modify the YAML.

### Run JSON output

With `--json`, output is a filename-keyed object. Each key is the path to the YAML file:

```json
{
  "path/to/config.yaml": {
    "created": 3,
    "alreadyExisted": 1,
    "repos": [
      { "repo": "org/repo-one", "issueNumber": 7, "status": "created" },
      { "repo": "org/repo-two", "issueNumber": 12, "status": "alreadyExisted" }
    ]
  },
  "path/to/other.yaml": {
    "error": "YAML 'job.title' is required."
  }
}
```

### Examples

```sh
# Single file
orca run jobs/my-upgrade.yml
orca run jobs/my-upgrade.yml --verbose
orca run jobs/my-upgrade.yml --auto-create-labels --skip-copilot

# Glob — run all configs under a directory (quote to prevent shell expansion)
orca run "jobs/*.yml"
orca run "jobs/*.yml" --continue-on-error --json

# Limit concurrency to avoid rate limits
orca run "jobs/*.yml" --max-concurrency 2

# Run sequentially (no parallelism at all)
orca run "jobs/*.yml" --no-parallel
```

---

## validate

Validate one or more YAML job configs and verify that all listed repositories are accessible.

```
orca validate <yaml_file_or_glob> [--no-parallel] [--max-concurrency <n>]
              [--continue-on-error] [--json]
```

### Arguments and flags

| Argument / Flag | Type | Required | Default | Description |
|-----------------|------|----------|---------|-------------|
| `<yaml_file_or_glob>` | positional | Yes | — | Path or glob pattern for YAML job config file(s). Quote glob patterns to prevent shell expansion (e.g. `"configs/*.yaml"`). |
| `--no-parallel` | flag | No | false | Check repositories sequentially instead of in parallel. Also disables file-level concurrency. Overrides `--max-concurrency`. |
| `--max-concurrency` | int | No | `4` | Maximum number of config files validated concurrently. |
| `--continue-on-error` | flag | No | false | Continue validating remaining files when one fails. |
| `--json` | flag | No | false | Emit machine-readable JSON output to stdout (see [JSON output](#validate-json-output) below). |

### What it does

For each matched YAML file:

1. Checks the file exists on disk and can be parsed.
2. Validates the schema — all required fields present, template file exists.
3. For each repo in the config, calls `gh repo view <org/repo>` to confirm it is accessible with the current credentials.

Exit code is `0` if all files are valid, `1` if any file or repo check fails.

### Validate JSON output

With `--json`, output is a filename-keyed object:

```json
{
  "path/to/config.yaml": {
    "valid": true,
    "configErrors": [],
    "repoErrors": []
  },
  "path/to/other.yaml": {
    "valid": false,
    "configErrors": ["YAML 'job.title' is required."],
    "repoErrors": [
      { "repo": "my-org/missing-repo", "error": "Could not resolve to a Repository." }
    ]
  }
}
```

### Examples

```sh
# Validate a single file
orca validate jobs/my-upgrade.yml

# Validate all configs under a directory
orca validate "jobs/*.yml"

# Validate with JSON output, continuing past failures
orca validate "jobs/*.yml" --continue-on-error --json

# Validate sequentially (no parallelism)
orca validate "jobs/*.yml" --no-parallel
```

---

## info

Display a rich snapshot of the current state of a job using Spectre.Console.

```
orca info <yaml_file> [--skip-lock] [--save-lock]
```

### Arguments and flags

| Argument / Flag | Type | Required | Description |
|-----------------|------|----------|-------------|
| `<yaml_file>` | positional | Yes | Path to the YAML job configuration file |
| `--skip-lock` | flag | No | Bypass the lock file and always fetch live state from GitHub |
| `--save-lock` | flag | No | After fetching live state from GitHub, persist a new lock file |

### Output

**Metadata grid** — project name (with hyperlink), URL, data source (`lock file` or `GitHub (live)`), lock timestamp, YAML SHA-256, repo count, issue count, PR count.

**Issues table** — one row per repo with columns: Repo (hyperlink), Issue number, linked PR numbers, Assignees.

### Modes

| Command | Behaviour |
|---------|-----------|
| `orca info job.yml` | Reads lock file if it exists; falls back to live fetch. |
| `orca info job.yml --skip-lock` | Always fetches live from GitHub. |
| `orca info job.yml --skip-lock --save-lock` | Fetches live from GitHub and updates the lock file. |
| `orca info job.yml --save-lock` | Reads lock file if present; if no lock file, fetches live and saves. |

### Examples

```sh
# Show current state (uses lock file if available)
orca info jobs/my-upgrade.yml

# Force live fetch and refresh the lock file
orca info jobs/my-upgrade.yml --skip-lock --save-lock
```

---

## cleanup

Tear down everything that `run` created for the same YAML configuration.

```
orca cleanup <yaml_file> [--dryrun]
```

### Arguments and flags

| Argument / Flag | Type | Required | Description |
|-----------------|------|----------|-------------|
| `<yaml_file>` | positional | Yes | Path to the YAML job configuration file |
| `--dryrun` | flag | No | Preview all deletions without making any changes |

### What it does

For each managed issue:

1. Finds and closes any open PRs that reference the issue (`closingIssuesReferences`).
2. Deletes the issue.

Then:

3. Deletes the GitHub Project.
4. Removes the lock file.

If a lock file exists it is used for exact project number and issue list (avoids extra API calls). If not, the project is found by title and issues are looked up by title in each repo.

### Dry-run output

With `--dryrun`, each action is printed as `DRY RUN: Would ...` and no changes are made:

```
DRY RUN: Would close PR #3 in my-org/repo-one
DRY RUN: Would delete issue #7 in my-org/repo-one
DRY RUN: Would delete project "My Upgrade" (#13) in my-org
Dry run complete. No changes were made.
```

### Examples

```sh
# Preview what would be deleted
orca cleanup jobs/my-upgrade.yml --dryrun

# Actually delete everything
orca cleanup jobs/my-upgrade.yml
```

---

## YAML configuration

Full schema with all supported fields:

```yaml
job:
  title: "My Project Title"   # GitHub Project title and issue title (required)
  org:   "my-github-org"      # GitHub organisation slug (required)
  skipCopilot: false          # Disable @copilot assignment for this job (optional, default false)

repos:                        # Short repo names — no org/ prefix (required, non-empty)
  - "repo-one"
  - "repo-two"

issue:
  template: "./issue-body.md" # Path to Markdown template, relative to the YAML file (required)
  labels:                     # Labels to apply to created issues (optional)
    - "migration"
    - "automated"

copilot:                      # Optional section — parsed but not currently used by the CLI
  agent: "default"
  run_args: []
  max_runs: 1
```

### Validation rules

| Field | Rule |
|-------|------|
| `job.title` | Must be non-empty |
| `job.org` | Must be non-empty |
| `repos` | Must be non-empty list |
| `issue.template` | Must be non-empty and the referenced file must exist |
| `issue.labels` | Optional; missing labels will cause an error during `run` unless `--auto-create-labels` is set |

---

## Lock file format

Written as `<basename>.lock.json` alongside the YAML file. Pretty-printed JSON.

```json
{
  "lockedAt": "2026-03-02T20:34:21.046+00:00",
  "yamlHash": "<sha256-hex of raw YAML bytes>",
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

The lock file is consumed by `run` (idempotency check), `info` (default data source), and `cleanup` (exact issue/project lookup). Delete it to force a fresh run.

---

## Authentication

### Priority order

Checked at every command invocation, highest priority first:

1. `ORCA_PAT` environment variable → PAT auth (stored config not read)
2. `ORCA_APP_ID` / `ORCA_APP_INSTALLATION_ID` / `ORCA_APP_PRIVATE_KEY` / `ORCA_APP_KEY_PATH` env vars, overlaid on `~/.config/orca/auth.json` (type=app)
3. `GH_TOKEN` environment variable → raw token, no validation
4. Ambient `gh` CLI auth — runs `gh auth token` and uses the result

### Stored config file

Location: `~/.config/orca/auth.json`

PAT format:
```json
{ "type": "pat", "token": "ghp_..." }
```

GitHub App format:
```json
{ "type": "app", "appId": "123456", "keyPath": "/path/to/key.pem", "installationId": "78901234" }
```

---

## Environment variables

| Variable | Description |
|----------|-------------|
| `ORCA_PAT` | GitHub Personal Access Token. When set, stored config is ignored. |
| `ORCA_APP_ID` | GitHub App ID. Overrides `appId` in stored config. |
| `ORCA_APP_INSTALLATION_ID` | Installation ID for the target org. Overrides `installationId` in stored config. |
| `ORCA_APP_PRIVATE_KEY` | Raw PEM content of the App private key. No key file is read when set. Takes precedence over `ORCA_APP_KEY_PATH`. |
| `ORCA_APP_KEY_PATH` | Path to the PEM private key file. Overrides `keyPath` in stored config. Ignored if `ORCA_APP_PRIVATE_KEY` is set. |
| `GH_TOKEN` | Standard GitHub token env var. Used as fallback after all Orca-specific env vars. |

See [AUTH-ENV-VARS.md](AUTH-ENV-VARS.md) for detailed examples including CI pipeline patterns.

---

## Exit codes

| Code | Meaning |
|------|---------|
| `0` | Success |
| `1` | Error — message printed to stderr |

All commands print a human-readable error message to stderr on failure. Partial failures during `run` (e.g. one repo fails) result in exit code `1` and no lock file is written.
