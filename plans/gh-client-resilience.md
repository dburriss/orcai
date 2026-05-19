# GH client resilience hardening

## Context

The previous fix (`plans/nudge-rate-limit-and-pr-detection.md`) landed correctly — paced writes, broadened PR detection, bounded nudge concurrency. A follow-up review flagged four resilience gaps in `src/OrcAI.GitHub/GhClient.fs`:

1. **Bucket starts full** — the first ~`writesPerMinute` calls escape pacing entirely before the bucket runs dry.
2. **No jitter on retry** — concurrent callers all sleep exactly 60s after a 429 and stampede the API simultaneously.
3. **Reads bypass the bucket** — `FindIssue`, `ListRepos`, `IsArchived`, `FetchCodeowners`, `GetIssueState`, `GetPrState`, `ListLabels`, and the `issue view` inside `ReopenIssue` use bare `runGh` with no throttle and no retry. `run` / `cleanup` / `info` can still trip the secondary rate limit.
4. **No regression test for the bucket loop** — the original bug was a missing re-acquire; we have nothing guarding it.
5. **Smoke-testing the throttle requires hand-editing `~/.config/orcai/config.json`** — easy to forget the cleanup, after which every subsequent run silently throttles at the test value.

Deferred (accepted as-is): interrupt safety — re-running `nudge` picks up issues left unassigned by a killed run.

Intended outcome: every `gh` call is paced and retried (rate-limit *and* transient), the bucket warms up at 80% capacity, the bucket + retry logic have unit tests, and the throttle can be overridden for a single invocation without touching config files.

## Changes

### 1. Bucket: rename, 80% warm start, share with reads

File: `src/OrcAI.GitHub/GhClient.fs:66-83`

- Rename `WriteBucket` → `ApiBucket`. Same lock, same refill math.
- Start tokens at 80% of capacity instead of 100%:
  ```fsharp
  let mutable tokens = perMinuteCap * 4 / 5
  ```
  Rationale: a full start lets the first ~60 calls bypass the throttle entirely. 80% gives a small warm-up burst without effectively disabling pacing for the first minute.
- Rename `runGhWrite` → `runGhApi`. Signature unchanged.

### 2. Route reads through the bucket

File: `src/OrcAI.GitHub/GhClient.fs`

Replace every direct `runGh ghToken ...` call inside `GhCliClient` (lines 137, 167, 196, 229, 340, 509, 523, 531, 549, 567, 578) with `runGhApi bucket retries ghToken ...`. The bare `runGh` helper stays — `runGhApi` calls it internally.

`FetchCodeowners` (line 541-561) tries up to 3 paths per repo; each now counts as a token. Acceptable; CODEOWNERS lookup is once per repo.

### 3. Jitter on backoff

File: `src/OrcAI.GitHub/GhClient.fs:55-64`

Add ±25% jitter inside `withRetry`:

```fsharp
let private jitter (ms: int) =
    let delta = max 1 (ms / 4)
    ms + System.Random.Shared.Next(-delta, delta + 1)
```

Apply to the `Async.Sleep` call; floor at 500ms so a degenerate negative can't become a no-op.

### 4. Separate retry path for transient errors

File: `src/OrcAI.GitHub/GhClient.fs`

Add an `isTransient` predicate next to `isRateLimit` (line 49):

```fsharp
let private isTransient (msg: string) =
    let m = msg.ToLowerInvariant()
    [ "i/o timeout"; "connection refused"; "connection reset"
      "no such host"; "tls handshake"; "remote end closed"
      "502 bad gateway"; "503 service unavailable"; "504 gateway timeout" ]
    |> List.exists m.Contains
```

Extend `withRetry` to handle both paths with independent backoff schedules sharing one attempt budget:

- Rate-limit branch: start 60s, double, cap 5min (unchanged).
- Transient branch: start 2s, double, cap 30s.

```fsharp
let private withRetry maxAttempts (run: unit -> Async<Result<'a, string>>) =
    let rec loop attempt (rlDelay: int) (txDelay: int) = async {
        let! result = run()
        match result with
        | Error msg when isRateLimit msg && attempt < maxAttempts ->
            do! Async.Sleep (max 500 (jitter rlDelay))
            return! loop (attempt + 1) (min (rlDelay * 2) 300_000) txDelay
        | Error msg when isTransient msg && attempt < maxAttempts ->
            do! Async.Sleep (max 500 (jitter txDelay))
            return! loop (attempt + 1) rlDelay (min (txDelay * 2) 30_000)
        | other -> return other
    }
    loop 1 60_000 2_000
```

Total attempts are still bounded by `maxAttempts` so the existing `rateLimitRetries` config knob keeps its meaning.

### 5. Unit tests

File: `tests/OrcAI.GitHub.Tests/` — project already exists (currently only holds an integration smoke test for `gh --version`).

The helpers being tested are `private`. Mark them `internal` in `GhClient.fs` and add at the top of the test file:

```fsharp
[<assembly: System.Runtime.CompilerServices.InternalsVisibleTo("OrcAI.GitHub.Tests")>]
do ()
```

(Or use a small `internal module RateLimit` to expose just what tests need.)

Add `tests/OrcAI.GitHub.Tests/RateLimitTests.fs` (and add it to `OrcAI.GitHub.Tests.fsproj` `<Compile Include="..." />`) with:

- **`ApiBucket starts at 80%`** — `ApiBucket(60).Acquire()` returns 0 for the first 48 calls; the 49th returns a positive wait.
- **`ApiBucket refills on time`** — exhaust the bucket, sleep `60_000 / perMinuteCap`, next `Acquire()` returns 0.
- **`ApiBucket caps tokens at perMinuteCap`** — after a long idle, the bucket does not over-accumulate beyond `perMinuteCap`.
- **`withRetry on rate-limit returns Ok after N attempts`** — mock `run` to return rate-limit error twice then Ok; assert Ok and attempt count.
- **`withRetry on transient uses short backoff`** — mock with `"connection reset"`; assert the loop retries with the transient schedule (parameterise initial delays in tests via an `internal` test-only overload so the test doesn't actually sleep 60s).
- **`withRetry gives up after maxAttempts`** — propagates the final Error unchanged.
- **`withRetry does not retry non-retriable errors`** — e.g. `"Could not resolve to a PullRequest"` returns immediately.

To keep tests fast, factor the initial delays out as parameters with the production defaults as an `internal` overload:

```fsharp
let internal withRetryDelays maxAttempts initialRl initialTx run = ...
let private withRetry maxAttempts run = withRetryDelays maxAttempts 60_000 2_000 run
```

Tests call `withRetryDelays 3 10 5` to keep total test time under a second.

### 6. Env-var override for throttle smoke testing

File: `src/OrcAI.Tool/Program.fs:96-97`

The throttle values are read once when the client is constructed:

```fsharp
let writesPerMinute  = cfg.WritesPerMinute  |> Option.defaultValue 60
let rateLimitRetries = cfg.RateLimitRetries |> Option.defaultValue 3
```

Layer two env vars on top of the config so a developer can override per-invocation without editing files:

```fsharp
let envInt (name: string) =
    match System.Environment.GetEnvironmentVariable(name) with
    | null | "" -> None
    | s ->
        match System.Int32.TryParse(s) with
        | true, n when n > 0 -> Some n
        | _ -> None

let writesPerMinute =
    envInt "ORCAI_WRITES_PER_MINUTE"
    |> Option.orElse cfg.WritesPerMinute
    |> Option.defaultValue 60
let rateLimitRetries =
    envInt "ORCAI_RATE_LIMIT_RETRIES"
    |> Option.orElse cfg.RateLimitRetries
    |> Option.defaultValue 3
```

Precedence: env > config > default. Smoke test becomes `ORCAI_WRITES_PER_MINUTE=12 orcai nudge …` — single invocation, no cleanup, no risk of a stale config silently degrading later runs.

Document the env vars in `README.md` (or wherever `WritesPerMinute` is currently documented — `grep -rn writesPerMinute docs README.md` from repo root to find the right spot; if undocumented, skip).

## Critical files

- `src/OrcAI.GitHub/GhClient.fs` — rename + 80% warm start (lines 66-98), bucket all reads, add jitter and transient retry (lines 49-64)
- `src/OrcAI.Tool/Program.fs` — env-var overrides for `writesPerMinute` and `rateLimitRetries` (lines 96-97)
- `tests/OrcAI.GitHub.Tests/RateLimitTests.fs` — new file with bucket + retry tests
- `tests/OrcAI.GitHub.Tests/OrcAI.GitHub.Tests.fsproj` — add the new `.fs` to `<ItemGroup>`

No public API changes; `GhCliClient` constructor and `IGhClient` are unchanged.

## Verification

1. `dotnet build` and `dotnet test` from repo root — new `RateLimitTests` pass; existing nudge tests still pass.
2. **Local throttle smoke test:** `ORCAI_WRITES_PER_MINUTE=12 orcai run <yaml>` over ~30 repos. Reads now count against the budget, so wall-clock will be longer than before; verify no rate-limit retries fire in `--verbose` logs. Re-run without the env var and confirm normal throughput returns — no config file to clean up.
3. **Jitter sanity:** in a manual run that triggers a 429 (or a hand-crafted unit test), the retry timestamps from concurrent issue handlers should not be aligned to the same second.
4. **CI verification (next marge run):** action log shows runtime in minutes, non-zero `already have PRs` count, and no secondary-rate-limit errors or `gh` connection-reset errors propagating to the report.
