# Plan: Per-repo issue template append/prepend

## Context

`issue.template` in a job YAML points to one Markdown file, loaded once and used verbatim as the issue body for every repo in the job (`JobConfig.IssueBody : string`, set in `YamlConfig.parse`, consumed via `config.IssueBody` throughout `RunCommand.fs`).

We want to let a job add repo-specific instructions on top of the shared template, without duplicating the whole template per repo.

## Design

- Convention-based, no new YAML fields.
- For a repo named `svc-a` (short name, as written under `repos:` in YAML — jobs are single-org, so no org prefix needed), OrcAI looks for two optional files **next to the base template file**:
  - `svc-a.prepend.md`
  - `svc-a.append.md`
- Composition, when either exists: `prepend content` + blank line + `base template` + blank line + `append content`. Either piece is skipped if its file doesn't exist. If neither exists, the body is just the base template (current behaviour, unchanged).
- Missing override files are not an error — this is an opt-in per repo, per direction.
- The job-level `TemplateHash` (used for the existing hash-based "template changed → refresh issue bodies" flow, see `plans/hash-based-issue-body-update.md`) must also change when an override file for any repo in the job is added, edited, or removed — otherwise editing `svc-a.append.md` alone would silently not trigger a body update.

## Files to modify

| File | Change |
|---|---|
| `src/OrcAI.Core/Domain.fs` | Replace `IssueBody : string` with `IssueBodyByRepo : Map<RepoName, string>` on `JobConfig` |
| `src/OrcAI.Core/YamlConfig.fs` | `parse`: build `IssueBodyByRepo` (base body per repo, no overrides — pure fn has no file access). `parseFile`: apply prepend/append overrides per repo after `parse` succeeds. `computeTemplateHash`: extend to also hash any existing override files. |
| `src/OrcAI.Core/RunCommand.fs` | Replace all `config.IssueBody` reads with `config.IssueBodyByRepo.[repo]` |
| `tests/OrcAI.Core.Tests/YamlConfigTests.fs` | Update existing `IssueBody` assertions; add new tests for override loading and hash sensitivity |

No changes needed to `InfoCommand.fs` beyond the `computeTemplateHash` signature update (call site already has `config` in scope).

---

## Step-by-step

### 1. `Domain.fs` — change `JobConfig`

```fsharp
type JobConfig =
    { Org              : OrgName
      ProjectTitle     : string
      Repos            : RepoName list
      IssueTitle       : string
      IssueBodyByRepo  : Map<RepoName, string>
      Labels           : string list
      ... // unchanged
```

### 2. `YamlConfig.fs` — `parse` builds the base map

No signature change to `parse` (it stays pure — no file I/O). In the `Ok` record literal, replace:

```fsharp
IssueBody = templateContent
```

with:

```fsharp
IssueBodyByRepo =
    repos |> List.map (fun r -> r, templateContent) |> Map.ofList
```

(`repos` is the already-computed `RepoName list` a few lines above in the same record literal — reuse that binding rather than re-mapping `root.repos`.)

### 3. `YamlConfig.fs` — new helper to load overrides

```fsharp
/// Given the base template's absolute path and a repo's short name (the name
/// as written under `repos:` in YAML, without the org prefix), reads optional
/// {repo}.prepend.md / {repo}.append.md from the template's directory and
/// composes the final issue body. Missing override files are not an error.
let private composeIssueBody (fs: IFileSystem) (templateDir: string) (shortRepo: string) (baseBody: string) : string =
    let readOptional (suffix: string) =
        let p = Path.Combine(templateDir, $"{shortRepo}.{suffix}.md")
        if fs.File.Exists(p) then Some (fs.File.ReadAllText(p).Trim()) else None
    [ readOptional "prepend"; Some baseBody; readOptional "append" ]
    |> List.choose id
    |> String.concat "\n\n"
```

### 4. `YamlConfig.fs` — `parseFile` applies overrides

After the existing `parse yaml templatePath templateContent |> Result.map resolveProviderRoot` call, add a second `Result.map` step. Short repo names come from `root.repos` (same list `parse` prefixes with org) — zip them with the already-prefixed `config.Repos` (order is preserved by `Seq.map` in `parse`, so `List.zip` is safe):

```fsharp
let applyOverrides (config: JobConfig) : JobConfig =
    let templateDir = Path.GetDirectoryName(templatePath)
    let shortNames  = root.repos |> Seq.toList
    let overridden =
        List.zip shortNames config.Repos
        |> List.map (fun (short, repoName) ->
            let baseBody = config.IssueBodyByRepo.[repoName]
            repoName, composeIssueBody fs templateDir short baseBody)
        |> Map.ofList
    { config with IssueBodyByRepo = overridden }

parse yaml templatePath templateContent
|> Result.map resolveProviderRoot
|> Result.map applyOverrides
```

### 5. `YamlConfig.fs` — `computeTemplateHash` includes overrides

Change signature to take the repo short names alongside the template path, and fold in override file bytes (sorted by short name so hash order is deterministic):

```fsharp
let computeTemplateHash (fs: IFileSystem) (templatePath: string) (shortRepoNames: string list) : string =
    let templateDir = Path.GetDirectoryName(templatePath)
    let overrideBytes =
        shortRepoNames
        |> List.sort
        |> List.collect (fun r ->
            [ Path.Combine(templateDir, $"{r}.prepend.md"); Path.Combine(templateDir, $"{r}.append.md") ])
        |> List.filter fs.File.Exists
        |> List.collect (fun p -> fs.File.ReadAllBytes(p) |> Array.toList)
        |> Array.ofList
    hashBytes (Array.append (fs.File.ReadAllBytes(templatePath)) overrideBytes)
```

Update the two call sites (`RunCommand.fs` ~line 1354, `InfoCommand.fs` ~line 122) to pass repo short names. Both already have `config`/`mergedConfig` in scope — derive short names by stripping the `{org}/` prefix from each `RepoName`:

```fsharp
let (OrgName orgStr) = config.Org
let shortNames = config.Repos |> List.map (fun (RepoName r) -> r.Substring(orgStr.Length + 1))
```

Consider hoisting this "short name from RepoName" logic into a small shared helper (e.g. `Domain.fs` or `YamlConfig.fs`) if it starts appearing in more than these two spots.

### 6. `RunCommand.fs` — swap `config.IssueBody` for the per-repo lookup

Every call site already has `repo : RepoName` in scope. Replace:

```fsharp
config.IssueBody
```

with:

```fsharp
config.IssueBodyByRepo.[repo]
```

at all ~7 sites: `CreateIssue` (x2, lines ~539/552), the `Cmd`/`CmdCheckout`/`CmdToGithub` template `vars` maps (`issue_text` / `issue_hash`, lines ~656/659, 708/711, 778/781, 861/864), and `UpdateIssue` in `refreshBodies` (~line 1238).

### 7. Tests (`YamlConfigTests.fs`)

- Update existing assertions: `cfg.IssueBody` → `cfg.IssueBodyByRepo.[RepoName "acme/svc-a"]` (and similarly wherever else `IssueBody` is asserted).
- New `parseFile` tests (using the in-memory `System.IO.Abstractions.TestingHelpers` filesystem already used elsewhere in this file):
  - override applies: `{repo}.append.md` present → body is `base + "\n\n" + append content`.
  - prepend applies: same, prepended.
  - both present: prepend + base + append, in order.
  - no override files: body equals the base template, unchanged (regression guard).
  - override only affects the matching repo — a second repo in the same job with no override file gets the unmodified base body.
- New `computeTemplateHash` test: hash changes when an override file is added/edited/removed, with the base template held constant.

---

## Verification

```bash
# Base template only — behaves exactly as today
orcai run example/add-agents-md.yml

# Add example/add-agents-md.svc-a.append.md, re-run
# → only svc-a's issue body changes; TemplateHash changes so the existing
#   hash-based update flow (plans/hash-based-issue-body-update.md) picks it up
orcai run example/add-agents-md.yml

dotnet test
```
