# Glob and Multi-File Run / Validate

**Status:** Draft

## Description

Change `orcai run` and `orcai validate` to accept a glob pattern instead of a single YAML file path. All matched configs are processed with file-level parallelism controlled by `--max-concurrency`. `--no-parallel` disables all parallelism. `--continue-on-error` allows remaining files to be processed when one fails. `--json` output becomes a filename-keyed dictionary for both single and multi-file invocations.

Pattern matching uses `Microsoft.Extensions.FileSystemGlobbing` — full `**` support, no custom parsing, wrappable for tests.

## Purpose

Users managing many repositories often maintain multiple YAML configs. Today they must script a loop. This lets them do it in one command: `orcai run "configs/*.yaml"`.

---

## Parallelism Model

| Flag | Effect |
|---|---|
| *(default)* | Files run up to `--max-concurrency` concurrently; repo checks within each file also parallel |
| `--max-concurrency N` | Cap on concurrent files (default: 4) |
| `--no-parallel` | All parallelism off — files sequential, repo checks sequential. Overrides `--max-concurrency` |

Pattern depth is controlled by the pattern itself — `*.yaml` matches top level only, `**/*.yaml` recurses fully. No `--max-depth` flag is needed.

---

## JSON Output Shape (`--json`)

Always a filename-keyed dictionary, for both single and multi-file invocations. **Breaking change** to the existing `run --json` flat shape.

```json
{
  "configs/upgrade.yaml": { "created": 3, "alreadyExisted": 1, "repos": [...] },
  "configs/security.yaml": { "error": "YAML 'job.title' is required." }
}
```

`info` and `cleanup` keep their existing flat `--json` shape until they also get multi-file support.

---

## Scope

### Tasks

1. **Add `Microsoft.Extensions.FileSystemGlobbing`** to `OrcAI.Core.fsproj`.

2. **Implement `FileGlob.fs`** (new, `OrcAI.Core`)
   - Primary: `expand (searchDir: string) (pattern: string) : Result<string list, string>`
     — creates a `Matcher`, calls `AddInclude pattern`, calls `GetResultsInFullPath searchDir`.
   - Testable overload: `expandWith (dir: DirectoryInfoBase) (pattern: string) : Result<string list, string>`
     — accepts a `DirectoryInfoBase` so tests can pass a fake directory tree without real disk I/O.
   - Returns `Error "No files matched pattern: <pattern>"` when result is empty.
   - A plain file path (no wildcards) passes through as a single-element list if the file exists, or `Error` if it does not.

3. **Update `RunCommand`** (`OrcAI.Core/RunCommand.fs`)
   - Add `MaxConcurrency: int`, `NoParallel: bool`, `ContinueOnError: bool` to `RunInput`.
   - Rename existing execute body to `executeSingle (deps) (input: RunInput) : Async<Result<RunResult, string>>`.
   - New `execute`: receives `paths: string list`; maps `executeSingle` over each path. Parallel (throttled by `MaxConcurrency`) unless `NoParallel`. Stops on first error unless `ContinueOnError`.
   - Return `Map<string, Result<RunResult, string>>`.

4. **Update `ValidateCommand`** (`OrcAI.Core/ValidateCommand.fs`)
   - Add `MaxConcurrency: int` and `ContinueOnError: bool` to `ValidateInput` (alongside existing `NoParallel`).
   - `execute` already accepts `paths: string list` (per validate plan). Add outer parallel loop matching `RunCommand` pattern.
   - Return `Map<string, ValidateResult>`.

5. **Update `RunArgs` and `ValidateArgs`** (`OrcAI.Tool/Args.fs`)
   - Add `Max_Concurrency of int`, `No_Parallel`, `Continue_On_Error` to both.
   - Update `Yaml_File` usage string to clarify glob patterns are accepted (type stays `string`).

6. **Update `Program.fs`** (`OrcAI.Tool/Program.fs`)
   - Before dispatching `run` or `validate`, call `FileGlob.expand cwd pattern` to resolve the path list.
   - Pass resolved `string list` and new flags into command input.
   - Human output: filename as section header, per-file results beneath.
   - `--json`: always serialise as filename-keyed dictionary (replace existing flat `run --json` serialiser).

7. **Add `FileGlob.fs` to `OrcAI.Core.fsproj`** before `RunCommand.fs`.

8. **Mark backlog item `[x]`** in `BACKLOG.md`.

---

## Dependencies / Prerequisites

- `validate-command` plan must be fully implemented first.
- `ValidateCommand.execute` must already accept `string list` (covered by that plan's internal design note).
- No other prerequisites. `Microsoft.Extensions.FileSystemGlobbing` is a new package reference.

---

## Impact on Existing Code

| File | Change |
|---|---|
| `OrcAI.Core/RunCommand.fs` | Extract `executeSingle`; add multi-file outer loop and new input fields |
| `OrcAI.Core/ValidateCommand.fs` | Add `MaxConcurrency`, `ContinueOnError`; wire outer loop |
| `OrcAI.Core/OrcAI.Core.fsproj` | Add `FileGlob.fs` and `FileSystemGlobbing` package reference |
| `OrcAI.Tool/Args.fs` | New flags on `RunArgs` and `ValidateArgs` |
| `OrcAI.Tool/Program.fs` | Glob expansion, updated dispatch, output formatting, JSON shape (**breaking** for `run --json`) |

No other commands are affected. Single plain-path invocations continue to work — they produce a single-entry dictionary in `--json` output.

---

## Acceptance Criteria

- `orcai run "configs/*.yaml"` processes all matched files; each gets its own lock file.
- `orcai validate "**/*.yaml"` validates all matched files and reports per-file results.
- A pattern matching zero files errors immediately with a clear message.
- `--max-concurrency 1` and `--no-parallel` both produce sequential file processing.
- `--no-parallel` also disables intra-file repo parallelism.
- Without `--continue-on-error`, the first file failure stops processing.
- With `--continue-on-error`, all files are attempted and per-file errors are reported.
- `--json` always emits a filename-keyed dictionary, including for a single file.
- A single plain file path (no glob characters) continues to work exactly as before, except `--json` shape.
- Updated `run --json` tests pass; all existing tests pass.

---

## Testing Strategy

**`OrcAI.Core.Tests/FileGlobTests.fs`**
- `expand returns single-entry list for plain file path`
- `expand returns all matching paths for a glob pattern`
- `expand returns Error when pattern matches zero files`
- `expandWith supports ** pattern using fake DirectoryInfoBase`

**Updated `RunCommandTests.fs`**
- `run processes all files in a resolved path list`
- `run stops on first failure without --continue-on-error`
- `run continues past failures with --continue-on-error`
- `run with NoParallel=true processes files sequentially` (fake records call order)
- `run --json result is always a filename-keyed dictionary`

**Manual / integration**
- `orcai run "example/*.yaml"` against a live org — green path.
- `orcai validate "**/*.yaml"` with one invalid config — other files still reported.
- Unquoted pattern on bash/zsh — confirm behaviour and document.

---

## Risks & Mitigations

| Risk | Mitigation |
|---|---|
| Shell expands glob before CLI sees it (bash/zsh) | Document: always quote the pattern; test on bash, zsh, and PowerShell |
| High `--max-concurrency` hits GitHub rate limits | Default 4; note rate limit risk in help text |
| Breaking change to `run --json` flat shape | Call out prominently in release changelog |

---

## Open Questions

- Should a plain unquoted path that happens to be shell-expanded by bash still work correctly? (i.e. the CLI receives multiple args instead of one.) Argu's `MainCommand` takes a single string — this will break. May need to document "always quote glob patterns" clearly, or accept `Yaml_File` as a list.
