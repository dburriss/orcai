# OrcAI Configuration Reference

OrcAI loads settings from two optional JSON config files, applied in layers:

1. **Global** — `~/.config/orcai/config.json`
2. **Local** — `.orcai/config.json` in the current working directory

Local values take precedence over global values. Any key can be omitted; CLI flags always override config file values.

---

## Config keys

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `skipCopilot` | bool | `false` | Skip assigning `@copilot` to new issues. Equivalent to `--skip-copilot`. |
| `defaultLabels` | string[] | `[]` | Labels applied to every issue in addition to any labels in the YAML job config. |
| `autoCreateLabels` | bool | `false` | Create missing labels in each repo before applying them. Equivalent to `--auto-create-labels`. |
| `maxConcurrency` | int | `4` | Maximum number of config files processed concurrently. Equivalent to `--max-concurrency`. |
| `continueOnError` | bool | `false` | Continue processing remaining files after a failure. Equivalent to `--continue-on-error`. |
| `defaultOrg` | string | — | Default GitHub org used by `orcai generate` when `--org` is not supplied. |
| `writesPerMinute` | int | `60` | Token-bucket capacity for GitHub write calls per minute. Reduce if you hit secondary rate limits. |
| `rateLimitRetries` | int | `3` | Maximum number of automatic retries when a GitHub rate-limit error is encountered. |

---

## Examples

**Global config** (`~/.config/orcai/config.json`) — sensible defaults for all projects:

```json
{
  "skipCopilot": false,
  "defaultLabels": ["orcai"],
  "maxConcurrency": 4,
  "writesPerMinute": 60,
  "rateLimitRetries": 3
}
```

**Local config** (`.orcai/config.json`) — project-specific overrides:

```json
{
  "defaultOrg": "my-github-org",
  "autoCreateLabels": true,
  "maxConcurrency": 2
}
```

With both files present, the effective config merges them with local values winning on any key that appears in both.

---

## Precedence order (highest to lowest)

1. CLI flags (e.g. `--max-concurrency 2`)
2. Local config (`.orcai/config.json`)
3. Global config (`~/.config/orcai/config.json`)
4. Built-in defaults
