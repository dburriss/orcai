# Plan: Dependent Jobs (`depends_on`)

## Context

Currently every `orcai` job is independent — `orcai run job.yml` processes all its repos unconditionally. As users chain multiple migration jobs (e.g. upgrade to .NET 10, then set up multi-version builds), they need a way to gate a downstream job on the completion of an upstream one, either per-repo (repo X in job-B only runs once job-A's PR is merged for repo X) or globally (job-B only runs once all of job-A's repos are done). Without this, users must manually check completion and sequence runs themselves.

The intended outcome is a `depends_on` YAML field, validated by `orcai validate` (including cycle detection), automatically sequenced by `orcai run`, and visualised by a new `orcai graph` command.

---

## YAML Schema

```yaml
depends_on:
  - job: ./dotnet-upgrade-to-10.yml   # relative path to upstream YAML
    condition: pr_merged               # pr_merged | issue_closed
    scope: per_repo                    # per_repo (default) | all_repos
    untracked_repos: include           # include (default) | skip
```

Multiple entries use AND logic. `scope: all_repos` gates the entire downstream run on every repo in the upstream lock meeting the condition. `scope: per_repo` filters the downstream repo list individually.

---

## Implementation Steps

### Step 1 — Domain types (`src/OrcAI.Core/Domain.fs`)

Add after `ClosedPrAction`:

```fsharp
type DependencyCondition    = | IssueClosed | PrMerged
type DependencyScope        = | PerRepo | AllRepos
type UntrackedReposBehavior = | Include | Skip

type DependsOnConfig =
    { Job            : string                   // relative path, resolved at parse time
      Condition      : DependencyCondition
      Scope          : DependencyScope
      UntrackedRepos : UntrackedReposBehavior }
```

Add `DependsOn : DependsOnConfig list` to `JobConfig`.

Add `BlockedBy : string option` to `RunResult` (the existing result record in `RunCommand.fs`) so a gated all-repos run can surface its reason without being an error.

---

### Step 2 — YAML parsing (`src/OrcAI.Core/YamlConfig.fs`)

Add DTO:

```fsharp
[<CLIMutable>]
type YamlDependsOn =
    { job            : string
      condition      : string
      scope          : string
      untrackedRepos : string }
```

Add `dependsOn : System.Collections.Generic.List<YamlDependsOn>` to `YamlRoot`.

In `parse`, after existing field mapping, parse the list:

- Unknown `condition` values → `Error` (fail-fast, like `onClosedIssue`)
- `scope` default: `"per_repo"` → `PerRepo`
- `untrackedRepos` default: `"include"` → `Include`
- `job` path is stored as-is (relative); callers resolve it against the YAML directory

Extend `YamlConfigTests.fs` with: valid `depends_on`, unknown condition, unknown scope, missing job path, multiple entries.

---

### Step 3 — New module: `src/OrcAI.Core/DependencyResolution.fs`

Add to `OrcAI.Core.fsproj` **after** `LockFile.fs` and **before** `RunCommand.fs`.

**Public API:**

```fsharp
/// Walk depends_on chains from yamlPath, return paths in topological order
/// (dependencies first). Returns Error on cycle or missing file.
val resolveOrder : fs:IFileSystem -> yamlPath:string -> Result<string list, string>

/// Given a single dependency config and the upstream job's lock file,
/// return which of candidateRepos are eligible to proceed.
/// Falls back to live API calls when the lock file lacks needed state.
val eligibleRepos
    : client:IGhClient
   -> upstreamConfig:JobConfig
   -> upstreamLock:LockFile option
   -> dep:DependsOnConfig
   -> candidateRepos:RepoName list
   -> Async<RepoName list>

/// Apply all depends_on entries to produce the filtered repo list for a job.
/// Returns Error if an all_repos gate is not met (with reason string).
val filterRepos
    : client:IGhClient
   -> fs:IFileSystem
   -> config:JobConfig
   -> yamlDir:string
   -> Async<Result<RepoName list, string>>
```

**Condition-checking logic for `eligibleRepos`:**

- `PrMerged`:
  1. Find repo's issue in `upstreamLock.Issues`
  2. Fast path: check `upstreamLock.PullRequests` for `pr.Repo = repo && pr.ClosesIssue = issueNumber && pr.State = "MERGED"`
  3. Fallback: call `client.FindPrsForIssue` and check any `MERGED` state
- `IssueClosed`:
  1. Find repo's issue in `upstreamLock.Issues`
  2. Call `client.GetIssueState` on that issue number; eligible if `"CLOSED"`
  3. If repo not in upstream lock at all → apply `UntrackedRepos` setting

**`resolveOrder` — cycle detection:**
DFS with a "visiting" set. If a node is encountered twice in the current path → `Error "Circular dependency detected: A → B → A"`.

Add `DependencyResolutionTests.fs` covering: no deps (identity), linear chain, diamond (deduplication), cycle detection, missing upstream file, per-repo filtering, all-repos gate (pass and block), untracked-repos both modes.

---

### Step 4 — ValidateCommand (`src/OrcAI.Core/ValidateCommand.fs`)

After the existing schema check in `executeSingle`, add:

1. **Upstream file existence** — for each entry in `config.DependsOn`, resolve the `job` path relative to the YAML directory. If the file does not exist → append to `ConfigErrors`.
2. **Cycle detection** — call `DependencyResolution.resolveOrder deps.FileSystem path`. If `Error msg` → append `msg` to `ConfigErrors`.

Both checks are static (no API calls). No new fields on `ValidateResult` — cycle and missing-file errors go into the existing `ConfigErrors` list.

Extend `ValidateCommandTests.fs`: valid deps pass, missing upstream file is an error, cycle is an error.

---

### Step 5 — RunCommand (`src/OrcAI.Core/RunCommand.fs`)

**`execute` (the multi-file entry point, line ~807):**

Before the existing parallel/sequential dispatch, expand the input paths into a topologically ordered, deduplicated list:

```fsharp
// For each user-supplied path, resolve its full dependency chain.
// Merge all chains, deduplicate by path, preserve topological order.
// A path is a "dependency" only if it was NOT in the original user-supplied list.
let allPaths = resolvePaths deps.FileSystem paths
// → Result<(path: string * isDependency: bool) list, string>
```

**Glob / multi-path deduplication rule:** if the user runs `orcai run *.yml` and the glob expands to `["job-a.yml", "job-b.yml"]` where job-b depends on job-a, the merged chain is `[job-a.yml; job-b.yml]` — job-a appears **once** as an explicit target (not a dependency), job-b runs after it. No double execution.

Run in the resolved order. Only paths introduced *solely* by chain resolution (not in the original user list) are prefixed (following existing verbose/non-verbose rules):
```
Running dependency: job-a.yml
```

**`executeSingle`:**

After `YamlConfig.parseFile` produces `config`, if `config.DependsOn` is non-empty:

```fsharp
let yamlDir = Path.GetDirectoryName(Path.GetFullPath(path))
let! filteredResult = DependencyResolution.filterRepos deps.GhClient deps.FileSystem config yamlDir
match filteredResult with
| Error reason ->
    // all_repos gate blocked; return Ok with BlockedBy set, no repos processed
    return Ok { emptyResult with BlockedBy = Some reason }
| Ok filteredRepos ->
    let config = { config with Repos = filteredRepos }
    // continue with existing logic unchanged
```

Reuse the existing `processRepo`, `runFull`, `refreshBodies` functions unchanged — only `config.Repos` is narrowed before they are called.

Extend `RunCommandTests.fs`: dep filter narrows repos, all-repos gate blocks run, untracked repos include/skip, chain runs job-a then job-b in order, blocked result has `BlockedBy` set.

---

### Step 6 — New command: `orcai graph`

**`src/OrcAI.Core/GraphCommand.fs`** (add to `.fsproj` after `DependencyResolution.fs`):

```fsharp
type GraphInput  = { YamlPath: string }
type GraphResult = { Lines: string list }  // ASCII tree lines

let execute (deps: OrcAIDeps) (input: GraphInput) : Result<GraphResult, string>
```

Walk `depends_on` chains using `DependencyResolution.resolveOrder`, build ASCII tree:

```
upgrade.yml
└── dotnet10.yml  (pr_merged · per_repo)
```

No GitHub API calls — file system only.

**`src/OrcAI.Tool/Args.fs`:** Add `type GraphArgs` with `[<MainCommand; Mandatory>] Yaml_File of string` plus `Verbose` and `Json`. Add `| [<SubCommand>] Graph of ParseResults<GraphArgs>` to `OrcAIArgs`.

**`src/OrcAI.Tool/Program.fs`:** Add `Graph` match arm following the existing pattern (`withClient` → `GraphCommand.execute` → print lines or JSON).

---

### Step 7 — Documentation and changelog

**`docs/cli-reference.md`:**
- Add `depends_on` block to the YAML configuration section (fields table: `job`, `condition`, `scope`, `untracked_repos` with types, defaults, and descriptions)
- Add full `## graph` command section following the existing format (usage, flags table, behavior list, output example, JSON output, examples)
- Add ToC entry for `graph`

**`README.md`:**
- Brief mention of `depends_on` and `orcai graph` in the features/commands overview

**`CHANGELOG.md`:**
- Add under `## [Unreleased]` → `### Added`:
  - `depends_on` YAML field — per-repo and all-repos dependency conditions (`pr_merged`, `issue_closed`), `untracked_repos` control
  - `orcai run` now resolves and executes dependency chains in topological order
  - `orcai graph <yaml>` — renders the job dependency DAG as ASCII
  - `orcai validate` now detects circular `depends_on` references and missing upstream files

---

## Key Files

| File | Change |
|---|---|
| `src/OrcAI.Core/Domain.fs` | Add `DependsOnConfig` and 3 supporting union types; extend `JobConfig` and `RunResult` |
| `src/OrcAI.Core/YamlConfig.fs` | Add `YamlDependsOn` DTO; parse `depends_on` in `parse` |
| `src/OrcAI.Core/DependencyResolution.fs` | **New** — topological sort, cycle detection, eligibility checks |
| `src/OrcAI.Core/GraphCommand.fs` | **New** — ASCII DAG renderer |
| `src/OrcAI.Core/ValidateCommand.fs` | Add upstream-file and cycle checks to `executeSingle` |
| `src/OrcAI.Core/RunCommand.fs` | Expand paths in `execute`; filter repos in `executeSingle` |
| `src/OrcAI.Tool/Args.fs` | Add `GraphArgs`; add `Graph` case to `OrcAIArgs` |
| `src/OrcAI.Tool/Program.fs` | Add `Graph` handler; log dependency runs |
| `src/OrcAI.Core/OrcAI.Core.fsproj` | Add new `.fs` files in dependency order |
| `tests/OrcAI.Core.Tests/DependencyResolutionTests.fs` | **New** |
| `tests/OrcAI.Core.Tests/YamlConfigTests.fs` | Extend |
| `tests/OrcAI.Core.Tests/ValidateCommandTests.fs` | Extend |
| `tests/OrcAI.Core.Tests/RunCommandTests.fs` | Extend |
| `docs/cli-reference.md` | Document `depends_on` schema and `graph` command |
| `README.md` | Mention new feature |
| `CHANGELOG.md` | `[Unreleased]` entries |

## Reused patterns and utilities

- **`FakeGhClient`** (`tests/OrcAI.Core.Tests/FakeGhClient.fs`) — override `findPrsForIssue` and `getIssueState` handlers for dependency condition tests
- **`Given.yamlFile`** / **`A.LockFile.defaults()`** (`TestData.fs`) — set up upstream + downstream YAML and lock fixtures on the mock FS
- **`DependencyResolution.resolveOrder`** — implemented once, reused by `ValidateCommand`, `RunCommand`, and `GraphCommand`
- **`LockFile.tryRead`** (`LockFile.fs`) — load upstream lock state in `DependencyResolution.eligibleRepos`
- **`client.FindPrsForIssue`** / **`client.GetIssueState`** (`IGhClient`) — existing API surface for live fallback

---

## Verification

```sh
# Build
dotnet build OrcAI.sln

# Unit tests
dotnet test OrcAI.sln

# Validate detects a cycle
orcai validate job-b.yml   # job-b → job-a → job-b
# → [error] Circular dependency detected: job-b.yml → job-a.yml → job-b.yml

# Validate catches missing upstream
orcai validate job-b.yml   # depends_on: ./missing.yml
# → [error] Dependency file not found: ./missing.yml

# Graph renders tree
orcai graph job-b.yml
# → job-b.yml
#    └── job-a.yml  (pr_merged · per_repo)

# Run chains jobs automatically
orcai run job-b.yml
# → Running dependency: job-a.yml
#    ... job-a output ...
# → Running: job-b.yml
#    ... job-b output (repos filtered by dep condition) ...
```
