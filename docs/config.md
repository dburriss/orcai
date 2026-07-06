# OrcAI Configuration Reference

OrcAI loads settings from two optional JSON config files, applied in layers:

1. **Global** — `~/.config/orcai/config.json`
2. **Local** — `.orcai/config.json` in the current working directory

Local values take precedence over global values. Any key can be omitted; CLI flags always override config file values.

---

## Config keys

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `defaultLabels` | string[] | `[]` | Labels applied to every issue in addition to any labels in the YAML job config. |
| `autoCreateLabels` | bool | `false` | Create missing labels in each repo before applying them. Equivalent to `--auto-create-labels`. |
| `maxConcurrency` | int | `4` | Maximum number of config files processed concurrently. Equivalent to `--max-concurrency`. |
| `continueOnError` | bool | `false` | Continue processing remaining files after a failure. Equivalent to `--continue-on-error`. |
| `defaultOrg` | string | — | Default GitHub org used by `orcai generate` when `--org` is not supplied. |
| `writesPerMinute` | int | `60` | Token-bucket capacity for GitHub write calls per minute. Reduce if you hit secondary rate limits. |
| `rateLimitRetries` | int | `3` | Maximum number of automatic retries when a GitHub rate-limit error is encountered. |
| `checkoutRoot` | string | temp dir | Root directory for repo checkouts used by `cmd-checkout` and `cmd-to-pr`. Defaults to an OS temp directory scoped to the run. |

> **Note**: `action:` is per-job only and cannot be set in the global or local JSON config. The `action` block below sets global defaults for action fields that individual job YAML files can override. See the [YAML configuration reference](cli-reference.md#action-block) for all available action types and per-job options.

### `nudge` block

Controls how `orcai nudge` re-triggers the assignee on stale issues (those without a linked PR).

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `nudge.mode` | string | `"reassign"` | `"reassign"` — unassign then reassign (default). `"comment-only"` — post a comment only. `"comment-and-reassign"` — post a comment and reassign. |
| `nudge.comment` | string | — | Comment body posted by nudge. Supports template tokens: `{assignee}`, `{job.owner}`, `{repo.codeowners}`. |

### Template tokens

`nudge.comment` supports the following `{token}` placeholders:

| Token | Resolved from |
|-------|--------------|
| `{assignee}` | Assignee derived from the job's `action:` type (e.g. `@copilot` for `assign-copilot`). |
| `{job.owner}` | `job.owner` in the YAML (if set), otherwise the catch-all `*` owner from a `CODEOWNERS` file in the current repo (`CODEOWNERS`, `.github/CODEOWNERS`, or `docs/CODEOWNERS`). Left unreplaced if neither is found. |
| `{repo.codeowners}` | The catch-all `*` owner from the target repository's `CODEOWNERS` file (fetched from GitHub). Left unreplaced if absent or no `*` rule exists. |

### `action` block

Sets global defaults for action fields. Individual job YAML files override these values. The `action:` type itself is always per-job and cannot be set here.

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `action.writeBack` | string | `"pr-to-origin"` | Default write-back mode for `cmd-to-pr` jobs. Overridden by `writeBack` in the job YAML. Values: `pr-to-origin`, `commit-to-origin`, `fork-and-pr`. |

---

## Migration

### 0.9.0

- **`assign` config key removed.** The top-level `assign` JSON config key (available up to 0.8.1) has been removed. Use `action: { type: assign-copilot, ... }` or the appropriate `action` type in each job YAML instead.
- **`skipCopilot` config key removed.** The top-level `skipCopilot` JSON config key (available up to 0.8.1) has been removed. Use `action: { type: noop }` in the job YAML to skip assignment.
- **`redoOnClosed` config key removed.** Use `onClosedIssue: create` in the job YAML to re-run on closed issues.
- **`writeBack` moved to `action.writeBack`.** The top-level `writeBack` key is now nested under `action`. Update any config files from `"writeBack": "..."` to `"action": { "writeBack": "..." }`.

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

**Local config with nudge and action defaults** (`.orcai/config.json`):

```json
{
  "defaultOrg": "my-github-org",
  "nudge": {
    "mode": "comment-and-reassign",
    "comment": "Hey {assignee}, any update on this one?"
  },
  "action": {
    "writeBack": "fork-and-pr"
  }
}
```

With both files present, the effective config merges them with local values winning on any key that appears in both. Within nested blocks (`nudge`, `action`), merging is field-level — a local `nudge.mode` overrides a global `nudge.mode` without discarding the global `nudge.comment`.

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
