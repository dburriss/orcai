# Copy files into cmd/cmd-checkout/cmd-to-pr actions

## Context

Today `cmd`, `cmd-checkout`, and `cmd-to-pr` actions can only run a command against files already in the repo. There's no way to stage input files (e.g. a helper script and a prompt `.md` file to drive `opencode`) from where `orcai` itself is invoked into the target working directory / checkout before the command runs.

This adds a Docker-`COPY`-style `copy:` list to the `action:` block. Because `cmd-to-pr` commits with `git add -A`, copied scratch files (scripts, prompts) would otherwise leak into the PR diff — so each copy entry defaults to being deleted again after the command runs, unless explicitly marked to keep.

Design decisions already settled with the user:
- Available on all three action types: `cmd`, `cmd-checkout`, `cmd-to-pr`.
- `from`/`to` are **static paths, not templated** (no `{{var}}` rendering) — v1 keeps this simple.
- `from` supports globs, reusing the existing `FileGlob.expand` (`src/OrcAI.Core/FileGlob.fs:63`). A pattern matching **zero files is a hard error** that aborts the step (consistent with `expand`'s existing behavior).
- Single match → `to` is the exact destination file path. Multiple matches → `to` is treated as a directory; each file is copied preserving its path relative to the glob's static (non-wildcard) prefix directory.
- `keep: false` (default) → the copied destination path(s) are deleted after the command finishes, uniformly across all three action types (even though it's a no-op for `cmd-checkout`, whose whole worktree is discarded anyway). For `cmd-to-pr` this deletion happens **before** `commitAll`, so non-kept files never enter the commit/PR.
- `from` is resolved against orcai's own invocation directory (`Environment.CurrentDirectory`) — nothing in the codebase does a process-wide `chdir`, so this is stable for the whole run.

## Changes

### 1. `src/OrcAI.Core/Domain.fs`
- Add:
  ```fsharp
  type CopyEntry =
      { From : string
        To   : string
        Keep : bool }
  ```
- Extend `ActionConfig`:
  ```fsharp
  | Cmd         of exec: CmdExec * cwd: string option * copy: CopyEntry list
  | CmdCheckout of exec: CmdExec * cwd: string option * copy: CopyEntry list
  ```
- Add `Copy : CopyEntry list` to `CmdToPrConfig`.
- No change needed in `extractAssignee` — it already matches these cases with wildcards (`Cmd _ | CmdCheckout _ | CmdToPr _`).

### 2. `src/OrcAI.Core/FileGlob.fs`
Add copy orchestration next to the existing `expand`:
- `private staticPrefixDir (pattern: string) : string` — returns the leading path segments of a pattern before the first glob character (e.g. `./scripts/*.sh` → `./scripts`), used to compute relative paths for multi-match copies.
- `copyEntry (sourceRoot: string) (destRoot: string) (entry: CopyEntry) : Result<string list, string>` — calls `expand sourceRoot entry.From`; on a single match, copies to `Path.Combine(destRoot, entry.To)` as an exact file (creating parent dirs); on multiple matches, treats `entry.To` as a directory and copies each file preserving its path relative to `staticPrefixDir`. Propagates `expand`'s existing zero-match `Error`.
- `copyAll (sourceRoot: string) (destRoot: string) (entries: CopyEntry list) : Result<(string * bool) list, string>` — folds `copyEntry` over the list, short-circuits on the first `Error`, and threads each entry's `Keep` flag alongside its resolved destination path(s).
- `cleanupCopies (written: (string * bool) list) : unit` — deletes every path where `keep = false`, swallowing I/O errors (best-effort — e.g. the worktree may already be gone).

### 3. `src/OrcAI.Core/YamlConfig.fs`
- Add DTO (mirrors the existing `YamlDependsOn` list-of-records pattern, `YamlConfig.fs:69-73` / `:211-232`):
  ```fsharp
  [<CLIMutable>]
  type YamlCopyEntry =
      { from: string
        ``to``: string
        keep: System.Nullable<bool> }
  ```
- Add `copy: System.Collections.Generic.List<YamlCopyEntry>` to `YamlAction`.
- Add `parseCopyEntry` mapper: fails if `from`/`to` blank; `Keep` defaults to `false`.
- Compute `copyList` once (`isNull` guard → `[]`, else `Seq.map parseCopyEntry |> List.ofSeq`) and thread it into the `Cmd(...)`/`CmdCheckout(...)` constructors and the `CmdToPr { ...; Copy = copyList }` record literal (`YamlConfig.fs:173-197`).

### 4. `src/OrcAI.Core/RunCommand.fs`
- Add `open OrcAI.Core.FileGlob`.
- **`Cmd(exec, cwd, copy)`** branch (`:475`): after `workingDir` is computed, call `FileGlob.copyAll Environment.CurrentDirectory (Path.GetFullPath workingDir) copy`. On `Error e`, `eprintfn` a warning and return without starting the process (this action already has no lock-file failure tracking, so this matches its existing behavior). On `Ok written`, run the process as today, then call `FileGlob.cleanupCopies written` after it exits.
- **`CmdCheckout(exec, cwd, copy)`** branch (`:512`): same copy step inserted right after `workingDir` is computed, before starting the process. On failure, reuse the existing `CmdCheckoutFailed` category (`record CmdCheckoutFailed (Error e)`, matching how clone/worktree failures are already recorded there) and return. On success, run the process as today, then `cleanupCopies written` before the final `CheckoutManager.cleanup`.
- **`CmdToPr(cfg)`** branch (`:573`): same copy step using `cfg.Copy`, after `workingDir` is computed. On failure, reuse `CmdToPrCheckoutFailed`. On success, run the process, then call `cleanupCopies written` **before** `let renderedMsg = render commitMsg` / `CheckoutManager.commitAll worktreePath renderedMsg` (`:647-648`) — this ordering is the critical piece that keeps non-`keep` files out of the commit.

### 5. Tests
- `tests/OrcAI.Core.Tests/YamlConfigTests.fs`: update existing constructor assertions to add the third `[]` arg (lines 371, 378, 404, 411, 488, 495). Add new cases: parsing a `copy:` list on `cmd`/`cmd-checkout`/`cmd-to-pr`, default `keep: false`, explicit `keep: true`, and an error when an entry is missing `from` or `to`.
- `tests/OrcAI.Core.Tests/FileGlobTests.fs`: reuse the existing `withTempDir` helper (extend it or add a two-directory variant for source/dest) to cover `copyEntry`/`copyAll`/`cleanupCopies`: single-file copy to an exact path, glob matching multiple files copying into a directory with relative structure preserved, zero-match hard error, and `cleanupCopies` removing `keep: false` entries while leaving `keep: true` ones untouched.
- No changes needed to `RunCommandTests.fs`/`CheckoutManagerTests.fs` — this codebase doesn't exercise the `Cmd`/`CmdCheckout`/`CmdToPr` branches with real git/process integration tests today (they're process/git-dependent); coverage stays at the `FileGlob` unit level plus YAML parsing, consistent with existing conventions.

## Verification
- `dotnet build` — confirms all arity/record-literal changes compile clean across `Domain.fs`, `YamlConfig.fs`, `RunCommand.fs`.
- `dotnet test` — run the full suite, including the new `FileGlobTests` and `YamlConfigTests` cases.
- Update `docs/cli-reference.md` (`action:` block table around line 723-812) to document the new `copy` field and its `keep` default, with an example YAML snippet showing a `cmd-to-pr` action that copies a script + prompt file with `keep: false`.
