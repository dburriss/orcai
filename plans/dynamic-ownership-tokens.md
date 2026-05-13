# Dynamic Ownership Tokens in Comment Templates

## Context

Comment templates in `assign.comment` and `nudge.comment` currently only support `{assignee}`, substituted by a plain `String.Replace`. Two new dynamic tokens are needed to notify the right people when a nudge fires or an issue is assigned:

- `{job.owner}` — who owns the orcai job (the person/team responsible for running it). Resolved locally: YAML `job.owner` field wins; falls back to the catch-all `*` owner from CODEOWNERS in the current repo on disk. No API call.
- `{repo.codeowners}` — who owns the target repository. Resolved remotely: parses CODEOWNERS from the target GitHub repo (tried at `CODEOWNERS`, `.github/CODEOWNERS`, `docs/CODEOWNERS` in order). One API call per repo, only when a comment is being posted.

Tokens left unreplaced if their source is absent (no CODEOWNERS, no `job.owner` set, CODEOWNERS has no `*` rule).

The existing `String.Replace("{assignee}", assignTo)` calls are replaced by a shared `renderTemplate` helper that applies all substitutions in one pass.

## Files to modify

### 1. `src/OrcAI.Core/Domain.fs`

Add `JobOwner : string option` to `JobConfig` after the `Nudge` field (line 65).

Add `renderTemplate` at the bottom of the file:
```fsharp
let renderTemplate (vars: Map<string, string>) (tmpl: string) =
    vars |> Map.fold (fun s k v -> s.Replace("{" + k + "}", v)) tmpl
```

### 2. NEW `src/OrcAI.Core/Codeowners.fs`

New module with two functions:

```fsharp
module OrcAI.Core.Codeowners

open System.IO.Abstractions

/// Return the owners on the catch-all `*` line, or None if absent.
let parseCatchAll (content: string) : string option =
    content.Split('\n')
    |> Array.tryPick (fun line ->
        let t = line.Trim()
        if t.StartsWith("#") || t = "" then None
        elif t.StartsWith("* ") || t.StartsWith("*\t") then
            let owners = t.Substring(1).Trim()
            if owners = "" then None else Some owners
        else None)

/// Try to read a CODEOWNERS file from the local filesystem.
/// Checks CODEOWNERS, .github/CODEOWNERS, docs/CODEOWNERS in order.
let tryReadLocal (fs: IFileSystem) (dir: string) : string option =
    [ "CODEOWNERS"; ".github/CODEOWNERS"; "docs/CODEOWNERS" ]
    |> List.tryPick (fun rel ->
        let path = fs.Path.Combine(dir, rel)
        if fs.File.Exists(path) then Some (fs.File.ReadAllText(path))
        else None)
    |> Option.bind parseCatchAll
```

### 3. `src/OrcAI.Core/OrcAI.Core.fsproj`

Add `<Compile Include="Codeowners.fs" />` after `Domain.fs` and before `GhClient.fs` (line 11).

### 4. `src/OrcAI.Core/GhClient.fs`

Add one new member to `IGhClient` in the Repos section (after `RepoExists`, line 42):
```fsharp
/// Fetch raw CODEOWNERS content from the repo. Returns None if not found.
FetchCodeowners : repo:RepoName -> Async<string option>
```

### 5. `src/OrcAI.GitHub/GhClient.fs`

Implement `FetchCodeowners` in `GhCliClient`. Try the three canonical CODEOWNERS paths via `gh api`:

```fsharp
FetchCodeowners = fun repo ->
    async {
        let (RepoName r) = repo
        let paths = [ "CODEOWNERS"; ".github/CODEOWNERS"; "docs/CODEOWNERS" ]
        let rec tryPaths = function
            | [] -> return None
            | p :: rest ->
                let! result = runGh token $"api repos/{r}/contents/{p} --jq .content"
                match result with
                | Error _ -> return! tryPaths rest
                | Ok b64  ->
                    let bytes = System.Convert.FromBase64String(b64.Replace("\n",""))
                    return Some (System.Text.Encoding.UTF8.GetString(bytes))
        return! tryPaths paths
    }
```

### 6. `tests/OrcAI.Core.Tests/FakeGhClient.fs`

Add `FetchCodeowners : RepoName -> Async<string option>` to the `Handlers` record (after `RepoExists`).

Add to `defaults`: `FetchCodeowners = fun _ -> async { return None }`.

Add to `neverCalledHandlers`: `FetchCodeowners = fun _ -> failwith "GhClient should not be called"`.

Add delegation in the `from` wrapper's object expression.

### 7. `src/OrcAI.Core/YamlConfig.fs`

Add `owner: string` to `YamlJob` DTO (line 32).

In `parse`, map to `JobOwner` on `JobConfig`:
```fsharp
JobOwner = nullStr root.job.owner
```

### 8. `src/OrcAI.Core/RunCommand.fs`

Resolve `jobOwner` once before `processRepo` (after the existing `pickAssign`/`pickNudge` block, ~line 274):

```fsharp
let jobOwner =
    jobConfig.JobOwner
    |> Option.orElseWith (fun () ->
        let dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(input.YamlPath[0]))
        Codeowners.tryReadLocal deps.FileSystem dir)
```

Inside `processRepo`, where the assign comment body is rendered (~line 196), replace:
```fsharp
let body = tmpl.Replace("{assignee}", assignTo)
```
with:
```fsharp
let! repoOwners = client.FetchCodeowners repo |> Async.map (Option.bind Codeowners.parseCatchAll)
let vars =
    [ "assignee",       assignTo
      yield! jobOwner   |> Option.map (fun v -> "job.owner",       v) |> Option.toList
      yield! repoOwners |> Option.map (fun v -> "repo.codeowners", v) |> Option.toList ]
    |> Map.ofList
let body = renderTemplate vars tmpl
```

### 9. `src/OrcAI.Core/NudgeCommand.fs`

Same pattern as RunCommand.fs. Resolve `jobOwner` once before the per-issue loop (after ~line 58). Inside the async block where the nudge comment body is rendered (~line 87), replace the `String.Replace` call with `renderTemplate vars tmpl` using the same vars map, fetching `repoOwners` via `client.FetchCodeowners issue.Repo`.

## Verification

1. **Build** — `dotnet build` from repo root; zero errors.
2. **Unit tests** — add `CodeownersTests.fs` covering `parseCatchAll`: `*` rule present, multiple owners, no `*` rule, comment lines, empty file.
3. **`{job.owner}` from YAML** — set `job.owner: "@platform-team"` in a test YAML, run nudge dry-run, confirm `{job.owner}` resolves in the logged comment body.
4. **`{job.owner}` from local CODEOWNERS** — create a `CODEOWNERS` file with `* @local-owner`, omit `job.owner` from YAML, confirm token resolves.
5. **`{repo.codeowners}` via API** — stub `FetchCodeowners` in `FakeGhClient` to return a CODEOWNERS string, assert the rendered comment contains the expected handle.
6. **Absent CODEOWNERS** — with no `job.owner`, no local CODEOWNERS, and `FetchCodeowners` returning `None`, confirm tokens are left unreplaced (not empty strings).
7. **Backwards compatibility** — templates with only `{assignee}` continue to work unchanged.
