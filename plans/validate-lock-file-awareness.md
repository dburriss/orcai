# Plan: Lock-file-aware `validate` command

## Context

`orcai validate` currently checks every repo in the YAML config via live GitHub API calls, which is slow when a job has many repos. Since `orcai run` already validates repo accessibility when it writes the lock file, re-checking every repo on every `validate` run is redundant. The goal is to make validation fast by default: skip live checks for repos already in the lock file, only hitting the API for repos that are new (in YAML but not in the lock). On first run (no lock file yet) just validate the YAML schema — there is nothing to cross-check.

## Behaviour

| Scenario | Result |
|---|---|
| No lock file | YAML schema validation only (parse + required fields) |
| Lock file exists, no new repos | YAML schema validation only |
| Lock file exists, new repos in YAML | YAML schema + live check for new repos only |
| `--skip-lock` flag | YAML schema + live check for **all** repos (current behaviour) |

## Files to change

### 1. `src/OrcAI.Tool/Args.fs`

Add `Skip_Lock` case to `ValidateArgs` (mirroring `RunArgs` and `InfoArgs`):

```fsharp
type ValidateArgs =
    | ...
    | Skip_Lock
    ...
    member a.Usage =
        | Skip_Lock -> "Bypass the lock file and always check all repos live."
```

### 2. `src/OrcAI.Core/ValidateCommand.fs`

Add `SkipLock: bool` to `ValidateInput`:

```fsharp
type ValidateInput =
    { ...
      SkipLock        : bool }
```

Rewrite `executeSingle` to use `LockFile.tryRead` for partial repo checking:

```
parse YAML → config.Repos
if SkipLock:
    reposToCheck = config.Repos          // current behaviour
else:
    lock = LockFile.tryRead fs yamlPath
    match lock with
    | None      -> reposToCheck = []     // first run: YAML-only
    | Some lock ->
        let lockRepoSet = Set.ofList lock.Repos
        reposToCheck = config.Repos |> List.filter (fun r -> not (lockRepoSet.Contains r))
run live checks on reposToCheck only
```

Key reuse: `LockFile.tryRead` (`src/OrcAI.Core/LockFile.fs:295`) and `LockFile.lockFilePath` (same file, line 286). The `IFileSystem` is already available on `deps`.

### 3. `src/OrcAI.Tool/Program.fs`

In the `Validate` branch, read the new flag and pass it through:

```fsharp
let skipLock = args.Contains(ValidateArgs.Skip_Lock)
...
let input : OrcAI.Core.ValidateCommand.ValidateInput =
    { ...
      SkipLock = skipLock }
```

## Verification

1. **Unit tests** — add cases in `tests/OrcAI.Core.Tests/` for:
   - No lock file → only schema errors surfaced, no repo checks made
   - Lock file present with same repos as YAML → no live calls made
   - Lock file present with subset of YAML repos → only delta repos are checked live
   - `--skip-lock` → all repos checked live

2. **Manual smoke test**:
   - Run `orcai validate <yaml>` on a job that already has a `.lock.json` — should return fast without network calls (verify with `--verbose`)
   - Add a new repo to the YAML, re-run — only that repo should be checked live
   - Run `orcai validate --skip-lock <yaml>` — all repos checked, same as before
   - Delete the lock file, run again — YAML-only, no API calls
