# OrcAI Configuration Reference

OrcAI loads settings from two optional JSON config files, applied in layers:

1. **Global** — `~/.config/orcai/config.json`
2. **Local** — `.orcai/config.json` in the current working directory

Local values take precedence over global values. Any key can be omitted; CLI flags always override config file values.

---

## Config keys

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `skipCopilot` | bool | `false` | Skip assignment entirely. Equivalent to `--skip-copilot`. Superseded by `assign.via`. |
| `defaultLabels` | string[] | `[]` | Labels applied to every issue in addition to any labels in the YAML job config. |
| `autoCreateLabels` | bool | `false` | Create missing labels in each repo before applying them. Equivalent to `--auto-create-labels`. |
| `maxConcurrency` | int | `4` | Maximum number of config files processed concurrently. Equivalent to `--max-concurrency`. |
| `continueOnError` | bool | `false` | Continue processing remaining files after a failure. Equivalent to `--continue-on-error`. |
| `defaultOrg` | string | — | Default GitHub org used by `orcai generate` when `--org` is not supplied. |
| `writesPerMinute` | int | `60` | Token-bucket capacity for GitHub write calls per minute. Reduce if you hit secondary rate limits. |
| `rateLimitRetries` | int | `3` | Maximum number of automatic retries when a GitHub rate-limit error is encountered. |

### `assign` block

Controls how `orcai run` (and the re-trigger in `orcai nudge`) reaches the assignee. All fields are optional; omitting the block keeps the existing `@copilot` assign behaviour.

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `assign.to` | string | `"@copilot"` | Assignee handle (e.g. `"@copilot"`, `"devon.burriss"`, `"opencode-agent[bot]"`). Not required when `via` is `"comment"`. Note: assigning `@copilot` requires a PAT (`ORCAI_PAT`); all other handles work with GitHub App auth directly. |
| `assign.via` | string | `"assign"` | How to trigger the assignee. `"assign"` — assign the issue. `"comment"` — post a comment only (no assignment). `"comment-and-assign"` — post a comment and assign. |
| `assign.comment` | string | — | Comment body posted when `via` includes `"comment"`. Supports template tokens: `{assignee}`, `{job.owner}`, `{repo.codeowners}`. |

### `nudge` block

Controls how `orcai nudge` re-triggers the assignee on stale issues (those without a linked PR).

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `nudge.mode` | string | `"reassign"` | `"reassign"` — unassign then reassign (default). `"comment-only"` — post a comment only. `"comment-and-reassign"` — post a comment and reassign. |
| `nudge.comment` | string | — | Comment body posted by nudge. Supports template tokens: `{assignee}`, `{job.owner}`, `{repo.codeowners}`. |

### Template tokens

`assign.comment` and `nudge.comment` support the following `{token}` placeholders:

| Token | Resolved from |
|-------|--------------|
| `{assignee}` | The `assign.to` handle. |
| `{job.owner}` | `job.owner` in the YAML (if set), otherwise the catch-all `*` owner from a `CODEOWNERS` file in the current repo (`CODEOWNERS`, `.github/CODEOWNERS`, or `docs/CODEOWNERS`). Left unreplaced if neither is found. |
| `{repo.codeowners}` | The catch-all `*` owner from the target repository's `CODEOWNERS` file (fetched from GitHub). Left unreplaced if absent or no `*` rule exists. |

---

## Examples

**Global config** (`~/.config/orcai/config.json`) — sensible defaults for all projects:

```json
{
  "defaultLabels": ["orcai"],
  "maxConcurrency": 4,
  "writesPerMinute": 60,
  "rateLimitRetries": 3
}
```

**Local config with OpenCode** (`.orcai/config.json`):

```json
{
  "defaultOrg": "my-github-org",
  "assign": {
    "to": "opencode-agent[bot]",
    "via": "comment",
    "comment": "/opencode please work on this issue"
  },
  "nudge": {
    "mode": "comment-only",
    "comment": "/opencode this issue seems stuck, please continue"
  }
}
```

**Local config with a human assignee** (`.orcai/config.json`):

```json
{
  "assign": {
    "to": "devon.burriss",
    "via": "assign",
    "comment": "Hey, this issue is ready for you"
  },
  "nudge": {
    "mode": "comment-and-reassign",
    "comment": "Hey {assignee}, any update on this one?"
  }
}
```

With both files present, the effective config merges them with local values winning on any key that appears in both. Within the `assign` and `nudge` blocks, merging is also field-level — a local `assign.to` overrides a global `assign.to` without discarding the global `assign.via`.

---

## Precedence order (highest to lowest)

1. CLI flags (e.g. `--max-concurrency 2`)
2. Local config (`.orcai/config.json`)
3. Global config (`~/.config/orcai/config.json`)
4. Built-in defaults

---

## Environment variables

| Variable | Default | Description |
|----------|---------|-------------|
| `ORCAI_LOG_LEVEL` | `Warning` | Minimum log level for diagnostic output. Accepts any [`LogLevel`](https://learn.microsoft.com/en-us/dotnet/api/microsoft.extensions.logging.loglevel) name: `Trace`, `Debug`, `Information`, `Warning`, `Error`, `Critical`, `None`. |
| `ORCAI_WRITES_PER_MINUTE` | — | Overrides `writesPerMinute` for a single invocation. Useful for smoke-testing the throttle without editing config files (e.g. `ORCAI_WRITES_PER_MINUTE=12 orcai run job.yml`). Must be a positive integer; non-positive or non-numeric values are ignored. |
| `ORCAI_RATE_LIMIT_RETRIES` | — | Overrides `rateLimitRetries` for a single invocation. Must be a positive integer. |

Precedence for these two knobs: env var > config (local/global) > built-in default.

### Examples

Suppress "already deleted" warnings during cleanup:

```sh
ORCAI_LOG_LEVEL=Error orcai cleanup job.yml --force
```

Enable verbose diagnostic output:

```sh
ORCAI_LOG_LEVEL=Debug orcai cleanup job.yml --force
```
