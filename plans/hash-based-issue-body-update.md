# Plan: Hash-based auto-update of issue bodies on `orcai run`

## Context

Once `orcai run` creates GitHub issues, editing the `.md` template has no effect on existing issues — `run` sees the lock file, hash matches, and returns early with zero API calls. The user wants to edit the MD and re-run `orcai run` to have it automatically update existing issue bodies.

The lock file already stores a `YamlHash`, but it currently only hashes the YAML bytes — not the MD template. Editing the MD alone produces no hash change and triggers no update.

## Approach: two separate hashes — YAML and template

Store a `YamlHash` and a `TemplateHash` separately in the lock file. This lets us be surgical about which network calls to make:

- **Only MD changed** → update issue bodies only (`UpdateIssue` per repo in lock) — no project, label, or assignment calls
- **Only YAML changed** → `runFull` as today — handles structural changes (new repos, labels, title, etc.); body unchanged so no `UpdateIssue`
- **Both changed** → `runFull` to handle structure + `UpdateIssue` pass for bodies
- **Neither changed** → fast path, zero API calls
- **No lock file** → `runFull` fresh, creates everything

---

## Edge cases

| State | Behaviour |
|---|---|
| No lock file | `runFull` creates everything fresh — no change |
| Both hashes match | Fast path, `AlreadyExisted` — no change |
| Only YAML hash changed | `runFull` (structural changes); no body update needed |
| Only template hash changed | `updateBodies` only — `UpdateIssue` per repo in lock; repos not in lock are ignored |
| Both hashes changed | `runFull` + `updateBodies` pass |
| Repo in lock but issue missing from lock | Not updated (lock is the source of truth for which issues exist) |
| Repo not yet in lock (new repo added to YAML) | Handled by `runFull` — creates issue normally |

---

## Files to modify

| File | Change |
|---|---|
| `src/OrcAI.Core/Domain.fs` | Add `TemplateHash` field to `LockFile` |
| `src/OrcAI.Core/LockFile.fs` | Add `templateHash` to DTO; read/write it |
| `src/OrcAI.Core/YamlConfig.fs` | Add `computeTemplateHash` (hashes MD bytes only) |
| `src/OrcAI.Core/GhClient.fs` | Add `UpdateIssue` to `IGhClient` |
| `src/OrcAI.GitHub/GhClient.fs` | Implement `UpdateIssue` on `GhCliClient` |
| `src/OrcAI.Core/RunCommand.fs` | New dispatch logic + `updateBodies` helper; add `Updated` outcome |

---

## Step-by-step

### 1. Add `TemplateHash` to `LockFile` (`Domain.fs`)

```fsharp
type LockFile =
    { LockedAt     : DateTimeOffset
      YamlHash     : string
      TemplateHash : string   // SHA-256 of the MD template bytes
      Project      : ProjectInfo
      Repos        : RepoName list
      Issues       : IssueRef list
      PullRequests : PullRequestRef list }
```

### 2. Update lock file DTO (`LockFile.fs`)

Add `templateHash: string` to the DTO record, and update `toDto` / `ofDto` to map it. Old lock files without the field will deserialise with an empty string (treated as "hash missing = assume changed").

### 3. Add `computeTemplateHash` (`YamlConfig.fs`)

`computeHash` stays as-is (YAML only). Add a parallel function:

```fsharp
let computeTemplateHash (fs: IFileSystem) (templatePath: string) : string =
    hashBytes (fs.File.ReadAllBytes(templatePath))
```

The template path is already resolved during `parseFile` — pass it to the call site in `RunCommand.fs` alongside `yamlPath`.

### 4. Add `UpdateIssue` to `IGhClient` (`GhClient.fs`)

```fsharp
abstract UpdateIssue : repo:RepoName -> issue:IssueNumber -> title:string -> body:string -> Async<Result<unit, string>>
```

### 5. Implement `UpdateIssue` in `GhCliClient` (`OrcAI.GitHub/GhClient.fs`)

Same temp-file pattern as `CreateIssue`:

```fsharp
member _.UpdateIssue repo issue title body =
    async {
        let (RepoName repoStr)   = repo
        let (IssueNumber issueN) = issue
        let tmpFile = System.IO.Path.GetTempFileName()
        try
            System.IO.File.WriteAllText(tmpFile, body)
            match! runGhWrite bucket retries ghToken
                       $"issue edit {issueN} --repo {repoStr} --title \"{title}\" --body-file \"{tmpFile}\"" with
            | Ok _    -> return Ok ()
            | Error e -> return Error e
        finally
            System.IO.File.Delete(tmpFile)
    }
```

### 6. New dispatch logic in `RunCommand.fs`

Add `Updated` to the per-repo outcome type. Add an `updateBodies` helper that iterates `lock.Issues` and calls `UpdateIssue` for each.

Replace the current dispatch in `executeSingle` (~lines 323-343):

```fsharp
let yamlHash     = YamlConfig.computeHash         deps.FileSystem input.YamlPath
let templateHash = YamlConfig.computeTemplateHash deps.FileSystem templatePath

match LockFile.tryRead deps.FileSystem input.YamlPath with
| None ->
    runFull deps input mergedConfig yamlHash templateHash

| Some lock when lock.YamlHash = yamlHash && lock.TemplateHash = templateHash ->
    // fast path — nothing changed
    let results = lock.Issues |> List.map (fun i -> { Issue = i; Outcome = AlreadyExisted })
    Ok { Lock = lock; Results = results; Source = FromLockFile }

| Some lock when lock.YamlHash = yamlHash ->
    // only MD changed — update bodies only, no structural calls
    updateBodies deps mergedConfig yamlHash templateHash lock

| Some lock when lock.TemplateHash = templateHash ->
    // only YAML changed — structural runFull, body unchanged
    runFull deps input mergedConfig yamlHash templateHash

| Some lock ->
    // both changed — structural runFull then update bodies
    runFull deps input mergedConfig yamlHash templateHash
```

`updateBodies` calls `UpdateIssue` in parallel (respecting `maxConcurrency`), collects `Updated`/`UpdateFailed` outcomes, renders the existing Spectre table, and writes a new lock file with the updated `TemplateHash`.

---

## Verification

```bash
# First run — creates issues and lock file
orcai run example/add-agents-md.yml

# Edit the MD template
vim example/add-agents-md.md

# Re-run — template hash differs, should update all existing issue bodies
orcai run example/add-agents-md.yml
# Output should show "Updated" for each repo instead of "AlreadyExisted"

# Re-run immediately — both hashes match new lock, fast-path again
orcai run example/add-agents-md.yml
# Output should show "AlreadyExisted"

# Confirm on GitHub that issue bodies changed
gh issue view <number> --repo <org/repo>
```
