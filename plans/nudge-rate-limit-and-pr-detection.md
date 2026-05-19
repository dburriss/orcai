# Fix nudge: rate limiting and PR detection

## Context

GitHub Actions run [marge#26060942867](https://github.com/coolblue-development/marge/actions/runs/26060942867/job/76620696934) processed **146 nudges in ~32 seconds** and reported `0 already have PRs, 0 skipped, 0 failed`. Two real problems in orcai:

1. **Rate limiting is broken.** Three compounding bugs let the configured `writesPerMinute = 60` be ignored:
   - `WriteBucket.Acquire` (`src/OrcAI.GitHub/GhClient.fs:71-83`) returns a wait-ms when out of tokens but does **not** decrement, so concurrent callers all see the same "wait then go" signal.
   - `runGhWrite` (`src/OrcAI.GitHub/GhClient.fs:86-91`) sleeps once and runs `gh` regardless — never re-acquires a token.
   - `NudgeCommand.execute` (`src/OrcAI.Core/NudgeCommand.fs:96-145`) fires every issue via `Async.Parallel` with no concurrency cap, so all 146 issues' read+write calls start in lockstep and the "throttle" is at most a single shared sleep.

2. **"Skip if PR exists" is too narrow.** `FindPrsForIssueImpl` (`src/OrcAI.GitHub/GhClient.fs:248-286`) only finds PRs via GraphQL `closingPullRequests`, which requires a closing keyword (`fixes #N`/`closes #N`). Copilot-authored PRs often lack that keyword, so the org's existing Copilot PRs are invisible — that's why all 146 issues were nudged.

Intended outcome after this change: nudge actually paces calls to the configured `writesPerMinute`, never bursts beyond a small concurrency cap, and skips issues that have any referencing open/closed PR (Copilot's included).

## Changes

### 1. Fix the bucket so it genuinely throttles

File: `src/OrcAI.GitHub/GhClient.fs`

- Change `runGhWrite` (line 86-91) to **loop** until `bucket.Acquire()` actually grants a token (returns 0). The bucket's refill math inside `lock gate` is already correct; the only bug is the caller's failure to re-check after sleeping.
  ```fsharp
  let private runGhWrite (bucket: WriteBucket) (retries: int) (token: string) (args: string) : Async<Result<string, string>> =
      withRetry retries (fun () -> async {
          let rec waitForToken () = async {
              let waitMs = bucket.Acquire()
              if waitMs > 0 then
                  do! Async.Sleep waitMs
                  return! waitForToken ()
          }
          do! waitForToken ()
          return! runGh token args
      })
  ```
- Keep `WriteBucket` as-is — the math is sound; the bug was external. (Optional: rename internal symbol to `ApiBucket` to reflect that reads will use it too.)

### 2. Route the broadened PR-detection call through the same bucket

File: `src/OrcAI.GitHub/GhClient.fs`

- `FindPrsForIssueImpl` (line 248-286) currently uses `runGh`. After this change it also gets the heavier timeline query (below) and will be called once per issue. Switch its call site to `runGhWrite bucket retries ghToken ...` so it shares the same per-minute quota and retry-on-rate-limit behaviour as writes.
- This is what the user explicitly requested: the new API call uses the bucket we're fixing.

### 3. Broaden PR detection via `timelineItems(CROSS_REFERENCED_EVENT)`

File: `src/OrcAI.GitHub/GhClient.fs`, replace the GraphQL query in `FindPrsForIssueImpl` (line 255).

Query both closing PRs *and* cross-referenced PRs in one round-trip, dedupe by PR number:

```graphql
query($owner:String!,$repo:String!,$issue:Int!) {
  repository(owner:$owner, name:$repo) {
    issue(number:$issue) {
      closingPullRequests(first:25) { nodes { number url state } }
      timelineItems(itemTypes:[CROSS_REFERENCED_EVENT], first:50) {
        nodes { ... on CrossReferencedEvent {
          source { ... on PullRequest { number url state } }
        } }
      }
    }
  }
}
```

Parse both node lists, build `PullRequestRef`s, dedupe by `PrNumber`. Keep the existing return shape (`PullRequestRef list`) so `NudgeCommand` doesn't change. Treat any returned PR (open or closed) the same as today — `PrFoundLive` skips the nudge.

### 4. Bound parallelism in `NudgeCommand`

Files:
- `src/OrcAI.Core/NudgeCommand.fs` — replace `Async.Parallel` (line 145) with a `SemaphoreSlim`-gated map, mirroring the pattern already used in `src/OrcAI.Core/RunCommand.fs:612-625`. Add `MaxConcurrency: int` to `NudgeInput` (line 20-25).
- `src/OrcAI.Tool/Args.fs` — add `Max_Concurrency of n: int` to `NudgeArgs` (line 137-148), matching `RunArgs`/`ValidateArgs` (line 17, line 176). Usage string: same wording as line 30.
- `src/OrcAI.Tool/Program.fs` — wire `args.TryGetResult(NudgeArgs.Max_Concurrency) |> Option.defaultValue 4` into the `NudgeInput`. Find the nudge command dispatch (search for `NudgeInput` constructor) and add the field.

Default `4` matches `run` and `validate`. With 4-way concurrency and a working 60/min bucket, 146 nudges × 2 writes + 146 reads = 438 calls → roughly 7 minutes — slow enough that the bucket actually paces, fast enough to be useful.

## Critical files

- `src/OrcAI.GitHub/GhClient.fs` — bucket fix, route FindPrs through bucket, broaden GraphQL query
- `src/OrcAI.Core/NudgeCommand.fs` — bounded parallelism, accept `MaxConcurrency`
- `src/OrcAI.Tool/Args.fs` — `Max_Concurrency` flag on `NudgeArgs`
- `src/OrcAI.Tool/Program.fs` — wire the flag through

No changes needed in `OrcAIConfig.fs` — `writesPerMinute` is already configurable and now actually enforced.

## Verification

1. **Unit / build:** `dotnet build` then `dotnet test` from repo root.
2. **Local dry-run against a job YAML with several issues:**
   ```
   dotnet run --project src/OrcAI.Tool -- nudge <yaml> --dry-run --verbose --max-concurrency 4
   ```
   Confirm: PR detection log lines show repos with Copilot PRs as `PR found on GitHub, no nudge needed` (previously they were nudged).
3. **Throttle smoke test:** add a temporary `writesPerMinute: 12` to `~/.config/orcai/config.json` and run a real nudge over ~30 issues. Wall-clock should be ~30/12 × 60 ≈ 2.5 min, not seconds. Remove the override after.
4. **CI verification (next marge run):** the action logs should show a runtime measured in minutes (not 32s) and a non-zero `already have PRs` count for the org's open Copilot PRs.
