# Plan: `--on-closed-pr` flag for `orcai nudge`

## Context

`orcai nudge` re-triggers the assignee on issues with no linked PR. It checks for existing PRs via GitHub's `closingPullRequests` GraphQL field, which only returns **open** PRs. If a PR was closed without merging (e.g. Copilot gave up or a branch was tidied up), nudge sees no PR, fires the trigger, and Copilot opens a duplicate. The `state` field is already fetched in the GraphQL query but discarded before it reaches the nudge logic.

This change surfaces PR state through the type system and adds a `--on-closed-pr` flag mirroring `--on-closed-issue` on `run`. Default is `skip` — closed PRs are treated as "handled; don't nudge".

---

## Changes

### 1. `src/OrcAI.Core/Domain.fs`

**Add a `State` field to `PullRequestRef`** (GitHub values: `"OPEN"`, `"CLOSED"`, `"MERGED"`):
```fsharp
type PullRequestRef =
    { Repo        : RepoName
      Number      : PrNumber
      Url         : string
      ClosesIssue : IssueNumber
      State       : string }        // new — default "OPEN" for backward compat
```

**Add `ClosedPrAction` DU** (simpler than `ClosedIssueAction` — no Reopen/Create since nudge doesn't manage PRs):
```fsharp
type ClosedPrAction = Nudge | Skip | Fail
```

---

### 2. `src/OrcAI.Core/LockFile.fs`

Add `state: string` to `PullRequestRefDto` with a backward-compat default:
```fsharp
[<JsonPropertyName("state")>]
state: string   // null in old lock files → default to "OPEN" when mapping to domain
```

Update the `PullRequestRefDto → PullRequestRef` mapping:
```fsharp
State = if isNull dto.state || dto.state = "" then "OPEN" else dto.state
```

Update the reverse mapping (`PullRequestRef → PullRequestRefDto`) to write the field.

---

### 3. `src/OrcAI.GitHub/GhClient.fs` — `toRef` (~line 394)

Extract `state` from the `JsonElement` (both `closingPullRequests` and `CrossReferencedEvent` nodes already include it in the query):
```fsharp
let toRef (el: JsonElement) =
    match intProp el "number", strProp el "url" with
    | Some n, Some url ->
        let state = strProp el "state" |> Option.defaultValue "OPEN"
        Some { Repo = repo; Number = PrNumber n; Url = url; ClosesIssue = issue; State = state }
    | _ -> None
```

---

### 4. `src/OrcAI.Core/NudgeCommand.fs`

**Extend `NudgeInput`:**
```fsharp
type NudgeInput =
    { ...
      OnClosedPr : ClosedPrAction }   // new — default Skip applied in Program.fs
```

**Add `NudgeOutcome` case:**
```fsharp
type NudgeOutcome =
    | Skipped
    | PrFoundLive
    | SkippedClosedPr   // new
    | NudgeSent
    | DryRunWouldNudge
    | NudgeFailed of reason: string
```

**Update `hasPrInLock`** — only open PRs in the lock count as "PR exists" (so a stale closed PR in the lock won't permanently suppress nudge):
```fsharp
let hasPrInLock =
    lock.PullRequests
    |> List.exists (fun pr -> pr.Repo = issue.Repo && pr.ClosesIssue = issue.Number && pr.State = "OPEN")
```

**Update live-PR decision logic** — split by state; `MERGED` is treated as done (same as open — never nudge):
```fsharp
let! prs = client.FindPrsForIssue issue.Repo issue.Number
let openOrMergedPrs = prs |> List.filter (fun pr -> pr.State = "OPEN" || pr.State = "MERGED")
let closedPrs       = prs |> List.filter (fun pr -> pr.State = "CLOSED")

if not (List.isEmpty openOrMergedPrs) then
    return { ...; Outcome = PrFoundLive; LivePrs = openOrMergedPrs }
elif not (List.isEmpty closedPrs) then
    match input.OnClosedPr with
    | Skip ->
        return { ...; Outcome = SkippedClosedPr; LivePrs = closedPrs }
    | Fail ->
        return { ...; Outcome = NudgeFailed "closed PR exists (use --on-closed-pr nudge to re-trigger)"; LivePrs = [] }
    | Nudge ->
        () // fall through to existing nudge logic
// ... existing nudge logic unchanged
```

**`SaveLock`** — only persist open/merged PRs (not closed, to avoid `hasPrInLock` suppressing future nudges):
```fsharp
let newPrs =
    results
    |> List.collect (fun r -> r.LivePrs |> List.filter (fun pr -> pr.State <> "CLOSED"))
```

---

### 5. `src/OrcAI.Tool/Args.fs`

Add to `NudgeArgs` DU (after `Max_Concurrency`):
```fsharp
| On_Closed_Pr of action: string
```
Usage string: `"What to do when a closed (unmerged) PR already exists: skip (default), nudge, or fail."`

---

### 6. `src/OrcAI.Tool/Program.fs`

In the `Nudge args` handler (~line 555), parse and wire:
```fsharp
let onClosedPr =
    match args.TryGetResult(NudgeArgs.On_Closed_Pr) with
    | None | Some "skip"  -> OrcAI.Core.Domain.ClosedPrAction.Skip
    | Some "nudge"        -> OrcAI.Core.Domain.ClosedPrAction.Nudge
    | Some "fail"         -> OrcAI.Core.Domain.ClosedPrAction.Fail
    | Some other          ->
        eprintfn "Unknown --on-closed-pr value '%s'. Valid values: skip, nudge, fail." other
        OrcAI.Core.Domain.ClosedPrAction.Skip
```

Add `OnClosedPr = onClosedPr` to the `NudgeInput` record construction.

**Add summary count and table rendering** for `SkippedClosedPr` (~lines 582–604):
- Counter: `let closedPr = results |> List.filter (fun r -> r.Outcome = NudgeCommand.SkippedClosedPr) |> List.length`
- Table cell: `| NudgeCommand.SkippedClosedPr -> "[grey]skipped (closed PR)[/]"`
- Include in the summary line alongside existing counts

---

### 7. `docs/cli-reference.md`

Update the nudge flags table and behavior section to document `--on-closed-pr`.

---

## Tests

**`tests/OrcAI.Core.Tests/TestData.fs`** — add helper in the `NudgeInput` module:
```fsharp
let withOnClosedPr a (i: NudgeCommand.NudgeInput) = { i with OnClosedPr = a }
```

**`tests/OrcAI.Core.Tests/NudgeCommandTests.fs`** — add a `ClosedPrAction tests` section following the pattern of `RunCommandTests.fs` lines 239–387:

| Test | Setup | Assertion |
|---|---|---|
| `skip (default)` | `FindPrsForIssue` returns `[{State="CLOSED"}]` | outcome = `SkippedClosedPr`; no unassign/reassign called |
| `nudge` | same, `OnClosedPr = Nudge` | outcome = `NudgeSent`; unassign+reassign called |
| `fail` | same, `OnClosedPr = Fail` | outcome = `NudgeFailed _` |
| `merged PR → never nudge` | `FindPrsForIssue` returns `[{State="MERGED"}]` | outcome = `PrFoundLive` regardless of `OnClosedPr` |
| `open PR → never nudge` | `FindPrsForIssue` returns `[{State="OPEN"}]` | outcome = `PrFoundLive` (unchanged) |

---

## Verification

1. `dotnet test` — all existing + new tests pass
2. `dotnet build` — no warnings
3. Smoke test with `--dryrun`:
   ```sh
   orcai nudge jobs/test.yml --on-closed-pr skip --dryrun --verbose
   orcai nudge jobs/test.yml --on-closed-pr nudge --dryrun --verbose
   ```
4. Confirm old lock files without a `state` key on PRs still load (backward compat check).
