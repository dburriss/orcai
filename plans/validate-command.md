# Validate Command

**Status:** Draft

## Description

Add an `orcai validate <yaml>` command that checks a YAML job config for correctness and verifies that all referenced repositories exist and are accessible with the current credentials — before any GitHub resources are created. This gives users a fast, safe pre-flight check.

## Purpose

The `run` command can fail mid-execution if a config is malformed or a repo is unreachable. `validate` surfaces all problems upfront, non-destructively, so users can fix them before committing to a real run.

---

## Scope

### Checks performed (in order)

1. **File exists** — the YAML path resolves to a readable file on disk.
2. **Schema valid** — YAML parses correctly and all required fields are present (`job.title`, `job.org`, `repos`, `issue.template`). The referenced template file must also exist on disk. Reuses `YamlConfig.parseFile`.
3. **Repos accessible** — for each repo in the config, confirm it exists and is accessible via the current credentials. Checks run in parallel by default; pass `--no-parallel` to disable.

All errors are collected across all checks; validation does not fail fast.

### Tasks

1. **Add `RepoExists` to `IGhClient`** (`OrcAI.Core/GhClient.fs`)
   New member: `abstract RepoExists : repo:RepoName -> Async<Result<unit, string>>`

2. **Implement `RepoExists`** (`OrcAI.GitHub/GhClient.fs`)
   Shell out to `gh repo view <org/repo> --json name`. Return `Ok ()` on success, `Error <gh message>` on non-zero exit.

3. **Implement `ValidateCommand`** (`OrcAI.Core/ValidateCommand.fs`)
   - Input: `{ YamlPath: string; NoParallel: bool }`
   - Step 1: check file exists via `IFileSystem`. If not, return immediately with that error.
   - Step 2: parse via `YamlConfig.parseFile`. If error, return immediately (no point checking repos).
   - Step 3: call `RepoExists` for each repo, in parallel unless `NoParallel = true`. Collect all errors.
   - Return `ValidateResult { ConfigErrors: string list; RepoErrors: (RepoName * string) list; IsValid: bool }`.
   - **Internal design note:** the core `executeSingle (deps) (path: string)` function takes a single resolved path. The public `execute` entry point accepts `paths: string list` and maps over it — today the CLI passes a single-element list, but this signature requires no change when the glob plan adds multi-file support.

4. **Wire CLI** (`OrcAI.Tool/Args.fs`, `OrcAI.Tool/Program.fs`)
   - Add `ValidateArgs`: `Yaml_File` (main command, mandatory), `No_Parallel` flag, `Json` flag.
   - Add `Validate` case to top-level `OrcAIArgs` DU.
   - Dispatch to `ValidateCommand.execute deps input`.
   - Human output: print each error with `[red]`; print `[green]Validation passed.[/]` on success.
   - `--json` output: `{ "valid": bool, "configErrors": [...], "repoErrors": [{"repo": "...", "error": "..."}] }`.
   - Exit code 1 if `IsValid = false`.

5. **Add `ValidateCommand.fs` to project file** (`OrcAI.Core/OrcAI.Core.fsproj`) before `RunCommand.fs`.

6. **Mark backlog item `[x]`** in `BACKLOG.md`.

---

## Dependencies / Prerequisites

- No new packages required. All infrastructure already exists.
- `RepoExists` on `IGhClient` must be added before `ValidateCommand` can be completed.

---

## Impact on Existing Code

| File | Change |
|---|---|
| `OrcAI.Core/GhClient.fs` | Add `RepoExists` abstract member |
| `OrcAI.GitHub/GhClient.fs` | Implement `RepoExists` via `gh repo view` |
| `OrcAI.Core/OrcAI.Core.fsproj` | Include `ValidateCommand.fs` |
| `OrcAI.Tool/Args.fs` | Add `ValidateArgs` and `Validate` case |
| `OrcAI.Tool/Program.fs` | Add dispatch branch and JSON printer |

No existing command logic is modified. `YamlConfig.parseFile` is reused as-is.

---

## Acceptance Criteria

- `orcai validate job.yaml` exits 0 and prints `Validation passed.` when the file exists, YAML is valid, and all repos are accessible.
- Exits 1 and lists all errors when any check fails.
- File-not-found and schema errors are reported without making any GitHub API calls.
- `--json` emits `{ "valid": bool, "configErrors": [...], "repoErrors": [...] }`.
- `--no-parallel` runs repo checks sequentially.
- Command is read-only and idempotent (no GitHub resources created or modified).
- All new code covered by unit tests; existing tests continue to pass.

---

## Testing Strategy

### Unit tests (`OrcAI.Core.Tests/ValidateCommandTests.fs`)

- `validate returns IsValid=true when file exists, config parses, and all repos exist`
- `validate returns error immediately when file does not exist` (no GhClient calls)
- `validate returns config error when YAML is malformed` (no GhClient calls)
- `validate returns config error when template file is missing` (no GhClient calls)
- `validate returns repo error for one inaccessible repo, reports others as ok`
- `validate collects errors for all inaccessible repos, not just the first`

### Unit tests for `RepoExists` (`OrcAI.GitHub.Tests` or inline fake)

- `RepoExists returns Ok when gh repo view succeeds`
- `RepoExists returns Error message when gh repo view returns non-zero`

### Manual / integration

- Run against `example/` YAML with a live org — expect green path.
- Introduce a typo in a repo name — expect red path listing the bad repo.
- Run with `--no-parallel` — same results, sequential execution.

---

## Risks & Mitigations

| Risk | Mitigation |
|---|---|
| `gh repo view` doesn't distinguish 404 from 403 | Surface the raw `gh` error message verbatim |
| Parallel async exceptions swallowed | Wrap each `RepoExists` call in `try/catch`, consistent with `InfoCommand` |
| Large repo lists hit GitHub rate limits | Document in help text; `--no-parallel` as an escape hatch |

---

## Open Questions

- Should `validate` also check that required labels exist per repo? This adds API calls per repo. Suggest deferring to a follow-up backlog item.
