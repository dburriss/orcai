# Global and Local Config File

**Status:** Draft

## Description

Add a layered config system that stores default values for CLI flags. A **global** config (`~/.config/orcai/config.json`) applies to all runs on a machine. A **local** config (`.orcai/config.json` in the working directory) applies to a specific project and overrides the global config. CLI flags override both.

## Purpose

Users who always want `--skip-copilot` or a specific `--max-concurrency` must repeat those flags on every command. A config file lets them set these defaults once — per machine or per project — and omit them from the command line.

---

## Scope

### Config settings supported

| Key | Type | Applicable commands |
|---|---|---|
| `skipCopilot` | `bool` | `run`, `generate` |
| `defaultLabels` | `string[]` | `run` (merged with YAML labels) |
| `autoCreateLabels` | `bool` | `run` |
| `maxConcurrency` | `int` | `run`, `validate` |
| `continueOnError` | `bool` | `run`, `validate` |
| `defaultOrg` | `string` | `generate` |

### Resolution order (highest wins)

1. Explicit CLI flag
2. Local config — `.orcai/config.json` relative to `$CWD`
3. Global config — `~/.config/orcai/config.json`
4. Built-in defaults (existing behaviour)

### Tasks

1. **Define `OrcAIConfig` type** (`OrcAI.Core/OrcAIConfig.fs` — new file)
   - `OrcAIConfig` record with all keys as `option` types so absence is distinguishable from `false`/`0`.
   - Pure functions: `merge (global: OrcAIConfig) (local: OrcAIConfig) : OrcAIConfig` — local wins per field when `Some`.
   - Pure value: `empty : OrcAIConfig` — all `None`.

2. **Implement config I/O** (same `OrcAIConfig.fs`)
   - `globalConfigPath (home: string) : string` → `~/.config/orcai/config.json`
   - `localConfigPath (cwd: string) : string` → `.orcai/config.json`
   - `readFile (fs: IFileSystem) (path: string) : Result<OrcAIConfig, string>` — `Ok empty` if file absent; error on malformed JSON.
   - `resolve (fs: IFileSystem) (home: string) (cwd: string) : OrcAIConfig` — reads both, merges, returns result.
   - Uses `System.Text.Json` (already available transitively via `OrcAI.Auth`).

3. **Add `Config` to `OrcAIDeps`** (`OrcAI.Core/Deps.fs`)
   - Add `Config: OrcAIConfig` field.

4. **Populate config in `main`** (`OrcAI.Tool/Program.fs`)
   - Call `OrcAIConfig.resolve fs home cwd` early; place result in `deps`.

5. **Apply defaults in command dispatch** (`OrcAI.Tool/Program.fs`)
   - `run`: fall back to `deps.Config` for `SkipCopilot`, `AutoCreateLabels`, `MaxConcurrency`, `ContinueOnError`; union-merge `DefaultLabels` with YAML labels.
   - `validate`: fall back for `MaxConcurrency`, `ContinueOnError`.
   - `generate`: fall back for `DefaultOrg`, `SkipCopilot`.
   - Fallback logic stays in `Program.fs` dispatch; command modules remain unchanged.

6. **Register `OrcAIConfig.fs`** in `OrcAI.Core/OrcAI.Core.fsproj` after `Deps.fs`.

7. **Mark backlog item `[x]`** in `BACKLOG.md`.

---

## Dependencies / Prerequisites

- No new NuGet packages. `System.Text.Json` is already available via `OrcAI.Auth`.
- `IFileSystem` abstraction already in `OrcAIDeps` for testable file reads.

---

## Impact on Existing Code

| File | Change |
|---|---|
| `OrcAI.Core/OrcAIConfig.fs` | New file — type, merge logic, file I/O |
| `OrcAI.Core/OrcAI.Core.fsproj` | Add `OrcAIConfig.fs` after `Deps.fs` |
| `OrcAI.Core/Deps.fs` | Add `Config: OrcAIConfig` field to `OrcAIDeps` |
| `OrcAI.Tool/Program.fs` | Resolve config in `main`; apply fallbacks in dispatch |

No command modules modified. No changes to `Args.fs`.

---

## Acceptance Criteria

- When neither config file exists, all commands behave exactly as before (no regression).
- A global config setting is applied when no local config or CLI flag overrides it.
- A local `.orcai/config.json` overrides the global on the same key.
- An explicit CLI flag always overrides both config files.
- `defaultLabels` in config is union-merged with YAML labels (not replaced).
- Malformed JSON prints a clear error and exits non-zero.
- A missing config file is silently ignored.

---

## Testing Strategy

### Unit tests (`OrcAI.Core.Tests/OrcAIConfigTests.fs`)

- `merge: local Some value wins over global Some value`
- `merge: global Some value used when local is None`
- `merge: both None yields None`
- `resolve: returns empty config when neither file exists`
- `resolve: reads and applies global config when only global exists`
- `resolve: local overrides global on overlapping keys`
- `readFile: returns Ok empty for a missing file`
- `readFile: returns Error for malformed JSON`
- `readFile: parses all supported fields correctly`

### Integration / manual

- Set `{ "skipCopilot": true }` in `~/.config/orcai/config.json`, run `orcai run job.yaml` without `--skip-copilot` — copilot should not be assigned.
- Add `.orcai/config.json` with `{ "skipCopilot": false }` — local overrides global, copilot assigned.
- Pass `--skip-copilot` explicitly — overrides both.

---

## Risks & Mitigations

| Risk | Mitigation |
|---|---|
| Adding `Config` to `OrcAIDeps` breaks all existing test construction sites | Set `Config = OrcAIConfig.empty` in all existing test helpers; add a convenience constructor |
| `defaultLabels` merge semantics unclear | Document as union-merge; open question below if replace is preferred |
| Silent ignore of absent file could mask permission errors | Only suppress `FileNotFoundException`; surface all other I/O exceptions |

---

## Open Questions

- Should `defaultLabels` **union-merge** with YAML labels or **replace** them? Current plan: union. Confirm before implementation.
- Should a future `generate` subcommand scaffold an empty config file? Deferred, not in scope here.
