# Plan: Implement `FetchReposState` bulk GraphQL prefetch for `RunCommand`

## Context

`RunCommand.processRepo` currently makes 2–4 API calls **per repo** before any writes:
- `IsArchived` — one `gh repo view` REST call per repo (always)
- `FindIssue` — one `gh issue list --state open` REST call per repo (always)
- `FindClosedIssue` — one `gh issue list --state closed` REST call per repo (only when FindIssue returns None)
- `ListLabels` — one `gh label list` REST call per repo (only when `--auto-create-labels`)

These run in parallel across repos but are still N×2-3 total round-trips. A single GraphQL query can fetch `isArchived`, the open issue, and the closed issue for all repos simultaneously using aliased root fields — the same pattern already used by `ReposExist`. A spike confirmed this works correctly against a real repo.

Labels are excluded from the bulk query: `ListLabels` uses `--limit 1000` and including labels per repo would reduce safe chunk size from ~50 to ~30 repos, risking silent truncation for label-heavy repos.

---

## Files to Change

| File | Change |
|---|---|
| `src/OrcAI.Core/Domain.fs` | Add `RepoState` type |
| `src/OrcAI.Core/GhClient.fs` | Add `FetchReposState` to `IGhClient` interface |
| `src/OrcAI.GitHub/GhClient.fs` | Implement `FetchReposState` on `GhCliClient` |
| `src/OrcAI.Core/RunCommand.fs` | Call `FetchReposState` in `runFull`; update `processRepo` to use prefetched state |
| `tests/OrcAI.Core.Tests/FakeGhClient.fs` | Add `FetchReposState` handler + test helpers |
| `tests/OrcAI.Core.Tests/RunCommandTests.fs` | Update ~15 tests to override `FetchReposState` instead of individual handlers |

---

## Step-by-step

### 1. `Domain.fs` — Add `RepoState`

After `IssueRef`:

```fsharp
type RepoState =
    { IsArchived  : bool
      OpenIssue   : IssueRef option
      ClosedIssue : IssueRef option }
```

### 2. `GhClient.fs` (interface) — Add abstract member

In the Repos section, after `IsArchived`:

```fsharp
abstract FetchReposState : repos:RepoName list -> title:string -> Async<Map<RepoName, Result<RepoState, string>>>
```

### 3. `GhClient.fs` (production) — Implement `FetchReposState`

After the `IsArchived` implementation (~line 687). Pattern mirrors `ReposExist` (chunking, tmp file, `runGhApiGraphQL`, error/null handling).

**Chunk size:** 50 repos per query (~50 × 23 nodes = ~1150 complexity, well within GitHub's GraphQL limit).

**Query shape per repo** (verified working via spike):
```
r{i}_info: repository(owner:"...",name:"..."){isArchived}
r{i}_open: search(query:"repo:.../... is:issue is:open <title> in:title",type:ISSUE,first:1)
           {nodes{...on Issue{number url assignees(first:10){nodes{login}}}}}
r{i}_closed: search(query:"repo:.../... is:issue is:closed <title> in:title",type:ISSUE,first:1)
             {nodes{...on Issue{number url assignees(first:10){nodes{login}}}}}
```

**Parsing:**
- `r{i}_info` null/missing → `Error "not found or inaccessible"` for that repo
- `r{i}_info` present → `Ok { IsArchived; OpenIssue = firstNode openAlias; ClosedIssue = firstNode closedAlias }`
- Use existing `strProp` / `intProp` helpers; assignees parsed same as `FindIssueImpl`
- Title must have embedded double-quotes escaped (`title.Replace("\"", "\\\"")`)

### 4. `RunCommand.fs`

**`processRepo` signature** — add parameter before `repo`:
```fsharp
(prefetchedState : RepoState option)
```

**Inside `processRepo`**, replace the three individual calls with prefetch lookups:

```fsharp
// IsArchived
let! archivedResult =
    match prefetchedState with
    | Some s -> async { return Ok s.IsArchived }
    | None   -> client.IsArchived repo

// FindIssue (inside the issueResult computation)
match! (match prefetchedState with
        | Some s -> async { return Ok s.OpenIssue }
        | None   -> client.FindIssue repo config.IssueTitle) with ...

// FindClosedIssue (inside the Ok None branch of the above)
match! (match prefetchedState with
        | Some s -> async { return Ok s.ClosedIssue }
        | None   -> client.FindClosedIssue repo config.IssueTitle) with ...
```

The existing branching logic (None → create/reopen/skip/fail) stays intact.

**`runFull`** — prefetch before parallel `processRepo` calls:

```fsharp
let prefetchedStates =
    deps.GhClient.FetchReposState config.Repos config.IssueTitle
    |> Async.RunSynchronously

let repoOutcomes =
    config.Repos
    |> List.map (fun repo ->
        let priorFailures  = Map.tryFind repo priorFailuresByRepo |> Option.defaultValue []
        let prefetchedState =
            Map.tryFind repo prefetchedStates
            |> Option.bind Result.toOption  // Error → None → falls back to individual calls
        processRepo deps processParams priorFailures prefetchedState repo)
    |> Async.Parallel
    |> Async.RunSynchronously
```

**`recreateStaleIssues`** — pass `None` (state may have changed since `runFull` ran):
```fsharp
processRepo deps params' [] None repo
```

### 5. `FakeGhClient.fs`

Add to `Handlers` record:
```fsharp
FetchReposState : RepoName list -> string -> Async<Map<RepoName, Result<RepoState, string>>>
```

`defaults` and `neverCalledHandlers` — both return not-archived, no issues (mirrors old `IsArchived` carve-out):
```fsharp
FetchReposState = fun repos _ ->
    async { return repos |> List.map (fun r -> r, Ok { IsArchived = false; OpenIssue = None; ClosedIssue = None }) |> Map.ofList }
```

`from`:
```fsharp
member _.FetchReposState repos title = h.FetchReposState repos title
```

**Add helpers** for concise test overrides:
```fsharp
let repoStateDefault  = { IsArchived = false; OpenIssue = None; ClosedIssue = None }
let repoStateArchived = { IsArchived = true;  OpenIssue = None; ClosedIssue = None }
let repoStateWithOpen   (repo: RepoName) num = { repoStateDefault with OpenIssue   = Some (issueFor repo num) }
let repoStateWithClosed (repo: RepoName) num = { repoStateDefault with ClosedIssue = Some (issueFor repo num) }

let fetchReposStateReturning (f: RepoName -> RepoState) : RepoName list -> string -> Async<Map<RepoName, Result<RepoState, string>>> =
    fun repos _ -> async { return repos |> List.map (fun r -> r, Ok (f r)) |> Map.ofList }
```

### 6. `RunCommandTests.fs` — migrate ~15 tests

Tests that override `IsArchived`, `FindIssue`, or `FindClosedIssue` flowing through `runFull` must now override `FetchReposState` instead (the individual handlers are still in the interface for other commands, but `processRepo` only calls them via the `None` fallback path used by `recreateStaleIssues`).

| Old override | New override |
|---|---|
| `IsArchived = fun _ -> async { return Ok true }` | `FetchReposState = fetchReposStateReturning (fun _ -> repoStateArchived)` |
| `IsArchived = fun _ -> async { return Error "..." }` | `FetchReposState = fun repos _ -> async { return repos \|> List.map (fun r -> r, Error "...") \|> Map.ofList }` |
| `FindIssue = fun r _ -> async { return Ok (Some (issueFor r 42)) }` | `FetchReposState = fetchReposStateReturning (fun r -> repoStateWithOpen r 42)` |
| `FindClosedIssue = fun r _ -> async { return Ok (Some (issueFor r 7)) }` | `FetchReposState = fetchReposStateReturning (fun r -> repoStateWithClosed r 7)` |

**Special case — stateful `FindIssue` test (line 656, stale-issue recreation):** The second `processRepo` call goes through `recreateStaleIssues` → `processRepo(None)`, so the individual `FindIssue` handler is still exercised on the fallback path. Only the first call (in `runFull`) needs `FetchReposState` set up.

**Special case — dry-run read-only tracking test (line 847):** Currently tracks calls to `FindIssue`/`FindClosedIssue`/`IsArchived`. Update to track `FetchReposState` instead.

---

## Verification

```bash
dotnet build src/OrcAI.Core/OrcAI.Core.fsproj
dotnet build src/OrcAI.GitHub/OrcAI.GitHub.fsproj
dotnet test tests/OrcAI.Core.Tests/OrcAI.Core.Tests.fsproj

# Smoke test against real YAML to confirm bulk query works end-to-end
orcai run <path-to-yaml> --verbose
```
