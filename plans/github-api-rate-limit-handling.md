# Plan: GitHub API Rate Limit Handling

## Context

When `orcai` runs against many repos in parallel, it hits GitHub's secondary rate limits:
- **80 content-generating requests/minute** (POST/PATCH/PUT/DELETE) — issue creation and Copilot assignment both count
- **500 content-generating requests/hour**

Currently there is **zero rate limit handling or retry logic**. The `runGh` function returns `Error "gh exited with code N: ..."` on failure with no retry. Repos run via `Async.Parallel` with only a `SemaphoreSlim` throttle at file level (default 4), but write operations within a run have no per-call throttle.

The `gh` CLI doesn't expose response headers, so proactive header-based throttling is not feasible. The solution must detect failures reactively and throttle writes proactively.

## Approach

Two complementary changes in `src/OrcAI.GitHub/GhClient.fs`:

### 1. Write Rate Limiter (Token Bucket)

Add a module-level token bucket that enforces the secondary rate limits without adding artificial delay when under the cap. GitHub's secondary limits are 80 writes/minute and 500/hour; target 60/min to leave comfortable headroom (25% safety margin), shared across all parallel workers.

The bucket refills continuously. A write only blocks if the bucket is empty — small runs (< 60 writes) go through at full speed with zero added latency.

```fsharp
// Module-level in GhCliClient
type WriteBucket() =
    let perMinuteCap = 60
    let mutable tokens = perMinuteCap
    let mutable lastRefill = DateTime.UtcNow
    let lock = obj()

    member _.Acquire() =
        lock lock (fun () ->
            let now = DateTime.UtcNow
            let elapsed = (now - lastRefill).TotalSeconds
            // Refill at 60 tokens/60s = 1 token/second
            let refilled = int (elapsed * float perMinuteCap / 60.0)
            if refilled > 0 then
                tokens <- min perMinuteCap (tokens + refilled)
                lastRefill <- now
            if tokens > 0 then
                tokens <- tokens - 1
                0  // no wait needed
            else
                // Wait for 1 token to refill (ms)
                int (60_000.0 / float perMinuteCap) + 1)

let private writeBucket = WriteBucket()

let private runGhWrite args token = async {
    let waitMs = writeBucket.Acquire()
    if waitMs > 0 then do! Async.Sleep waitMs
    return! runGh args token
}
```

### 2. Retry with Exponential Backoff for Rate Limit Errors

`runGh` catches `ExitCodeException` — SimpleExec's `Message` property contains combined stdout+stderr, so the GitHub error text is present even though `_stderr` is currently discarded. All rate-limit responses exit with code **1** (no dedicated exit code exists).

Confirmed `gh` CLI error substrings for rate limits:

| Scenario | Substring in error string |
|---|---|
| Primary REST limit | `"API rate limit exceeded"` |
| Secondary REST limit | `"secondary rate limit"` |
| Abuse detection / GraphQL | `"abuse detection mechanism"` |
| Submission speed | `"was submitted too quickly"` |

```fsharp
let private isRateLimit (msg: string) =
    msg.Contains("API rate limit exceeded", StringComparison.OrdinalIgnoreCase)
    || msg.Contains("secondary rate limit", StringComparison.OrdinalIgnoreCase)
    || msg.Contains("abuse detection mechanism", StringComparison.OrdinalIgnoreCase)
    || msg.Contains("was submitted too quickly", StringComparison.OrdinalIgnoreCase)

let private withRetry maxAttempts (run: unit -> Async<Result<'a, string>>) = async {
    let rec loop attempt delay = async {
        let! result = run()
        match result with
        | Error msg when isRateLimit msg && attempt < maxAttempts ->
            do! Async.Sleep delay
            return! loop (attempt + 1) (min (delay * 2) 300_000)
        | other -> return other
    }
    return! loop 1 60_000  // start at 60s, cap at 5min
}
```

Use `withRetry 3` around calls in `GhCliClient` methods.

## Critical Files

- `src/OrcAI.GitHub/GhClient.fs` — primary change: add `WriteBucket`, `runGhWrite`, `withRetry`, and `isRateLimit`; update write-path methods to use `runGhWrite` wrapped in `withRetry`
- `src/OrcAI.Core/GhClient.fs` — interface only, no changes needed
- `src/OrcAI.Core/RunCommand.fs` — no changes needed (rate limiting is an infrastructure concern, belongs in GhClient)

## Write Operations to Throttle

In `GhCliClient`, change these methods to use `runGhWrite` instead of `runGh`:
- `CreateIssue` (issue create)
- `AssignIssue` (issue edit --add-assignee)
- `ReopenIssue` (issue reopen)
- `DeleteIssue` (issue delete)
- `CreateLabel` (label create)
- `CreateProject` (project create)
- `DeleteProject` (project delete)
- `AddToProject` (project item-add)
- `ClosePr` (pr close)

Read operations (`FindIssue`, `FindProject`, `ListLabels`, `ListPrs`, etc.) continue using the plain `runGh`.

## Verification

1. Run `dotnet build` to confirm compilation
2. Run `dotnet test` for unit tests
3. Manually run `orcai run` against a config with 5+ repos and confirm:
   - Issues are created without rate limit errors
   - Small runs (< 60 writes) complete without any added delay
   - Large runs slow down gracefully rather than failing
   - If a rate limit error occurs, the tool retries rather than failing immediately
