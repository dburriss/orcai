# Fix GitHub rate-limit misdetection and crash at scale

## Context

Migrating `marge` (a job-config repo driving orcai against 150+ target repos per job) from orcai 0.8.1 → 0.10.1, a 152-repo dry run reproduced two failures that only show up at this scale:

1. **Default (parallel) mode**: ~100/152 real, accessible repos get logged as `Repository not found or inaccessible — skipping`, with the lock file showing `FindIssue ... attempt 1/3` and no further attempts.
2. **`--no-parallel` mode**: the whole run aborts immediately with `Error: '<' is an invalid start of a value` — a JSON deserializer choking on an HTML body, the signature of GitHub returning a rate-limit/abuse-detection page instead of JSON.

Root cause (confirmed by reading `src/OrcAI.GitHub/GhClient.fs`): orcai talks to GitHub entirely by shelling out to the `gh` CLI (`SimpleExec`) — there is no `HttpClient`, so raw HTTP status codes / `Retry-After` / `X-RateLimit-*` headers are never visible; the only rate-limit signal available is text in stdout/stderr or the parsed JSON body.

- **Failure mode 1** — `ReposExist` / `FetchReposState` (`GhClient.fs:675-739`, `754-860`) batch up to 50-100 repos into one GraphQL query with aliased fields (`r0: repository(...)`). Per-repo errors are matched to aliases via each GraphQL error's `path` array (`errorsByAlias`, e.g. line 706-721). GitHub reports secondary-rate-limit/abuse-detection errors as **global** GraphQL errors with **no `path`** — so `alias |> Option.map` silently drops them (`errorsByAlias` ends up empty for that chunk). Since `gh` CLI still exits 0 with a structurally valid (if partial) JSON envelope, the call returns `Ok json`, not `Error` — and the existing retry loop (`withRetryDelays`, `GhClient.fs:110-127`) only inspects `Error` results for `isRateLimit`/`isTransient`, so it never fires. Every repo in the chunk then falls through to the hardcoded default `"not found or inaccessible"` (lines 733, 845), which `isRepoNotFoundError`/`classifyCause` correctly-but-wrongly classifies as `NotFound`. This also explains "attempt 1/3, no 2/3 ever": from `GhCliClient`'s perspective the call *succeeded*, so no in-process retry happens at all; "1/3" is just the cross-run `LockFile` attempt counter.
- **Failure mode 2** — all 13 call sites in `GhClient.fs` do unguarded `JsonDocument.Parse(json).RootElement` on the `Ok` payload (lines 249, 279, 308, 341, 382, 453, 481, 611, 652, 667, 700, 747, 786, 791, 873). If `gh` exits 0 but the body isn't JSON (an HTML abuse-detection page slipping through), `JsonException` propagates **uncaught** through the `Async` workflow and kills the whole run (this is what crashed `--no-parallel`; under the default parallel mode via `Async.Parallel` it would do the same to the whole batch, so it's likely only avoided by chance so far).

Decisions confirmed with the user:
- No literal `Retry-After`/`X-RateLimit-Remaining` header reading — not accessible through `gh` CLI subprocess output. Keep text-pattern detection (`isRateLimit`) and the existing fixed exponential backoff (60s → capped 300s).
- Non-JSON/HTML response bodies get their own distinct failure signal ("malformed") but are **routed into the same rate-limit retry/backoff path**, since in practice they're GitHub's abuse-detection block page.

## Fix

### 1. Safe, classified JSON parsing (kills the crash)

Add a shared helper in `GhClient.fs` near the existing JSON helpers (`strProp`/`intProp`, ~line 187):

```fsharp
let internal isMalformedResponse (msg: string) = ... // e.g. contains "non-JSON response"

let private tryParseJson (json: string) : Result<JsonElement, string> =
    try Ok (JsonDocument.Parse(json).RootElement)
    with :? JsonException ->
        let snippet = json.Substring(0, min 120 json.Length)
        Error $"non-JSON response from GitHub (likely rate limited/blocked): {snippet}"
```

Replace all 13 unguarded `JsonDocument.Parse(json).RootElement` call sites with `tryParseJson json` and thread the `Result` through existing `Result`/`Option`-returning members (most already return `Result<_, string>`, so this is a mechanical swap; a couple returning `Option` map `Error` to `None`, matching current behavior for other error paths there).

Extend `isRateLimit` (`GhClient.fs:89-93`) to also match `isMalformedResponse`, so malformed/HTML bodies retry on the same schedule as rate limits. Extend `classifyCause` in `LockFile.fs:135-147` with the same substring so the lock file still records cause `RateLimit` for these (add the match **before** the `NotFound` branch, same way `UserError` is ordered before `NotFound` at line 141-143, so "not found"-ish text inside a malformed snippet doesn't get misrouted).

### 2. Detect and retry on global (path-less) GraphQL rate-limit errors

In `ReposExist` and `FetchReposState`, GraphQL errors lacking a `path` are today silently dropped when building `errorsByAlias`. Add a helper:

```fsharp
let private globalRateLimitError (doc: JsonElement) : string option =
    match doc.TryGetProperty("errors") with
    | true, arr ->
        arr.EnumerateArray()
        |> Seq.tryPick (fun err ->
            let hasPath = match err.TryGetProperty("path") with true, _ -> true | _ -> false
            let msg = match err.TryGetProperty("message") with true, m -> m.GetString() | _ -> ""
            if not hasPath && isRateLimit msg then Some msg else None)
    | _ -> None
```

Introduce `runGhApiGraphQLParsed` (alongside the existing `runGhApiGraphQL`, `GhClient.fs:171-181`) that moves JSON parsing *inside* the retried closure so a detected global rate-limit or malformed body becomes an `Error` the existing `withRetry` loop actually retries on:

```fsharp
let private runGhApiGraphQLParsed (bucket: ApiBucket) (retries: int) (token: string) (args: string) : Async<Result<JsonElement, string>> =
    withRetry retries (fun () -> async {
        do! waitForToken bucket   // existing pattern, factored out of runGhApi/runGhApiGraphQL
        match! runGhGraphQL token args with
        | Error e -> return Error e
        | Ok json ->
            match tryParseJson json with
            | Error e -> return Error e
            | Ok doc ->
                match globalRateLimitError doc with
                | Some msg -> return Error msg
                | None -> return Ok doc
    })
```

`ReposExist` (`GhClient.fs:696-736`) and `FetchReposState` (`GhClient.fs:787-857`) switch to this function and drop their own `JsonDocument.Parse(json)` call, using the returned `doc` directly — the rest of the per-alias logic (lines 700-734, 791-855) is unchanged. This makes attempts 2/3 actually happen: once the global error is surfaced as `Error`, the existing `withRetryDelays` exponential backoff (already correct) retries up to `retries` (configured via `ORCAI_RATE_LIMIT_RETRIES`, default 3).

### 3. Pause concurrent callers during backoff (desired fix #2)

`ApiBucket` (`GhClient.fs:131-153`) is already a single instance shared across all concurrent repo tasks within one `GhCliClient` (constructed once, captured in the interface members' closures — confirmed at `GhClient.fs:229-230`). Extend it with a shared cooldown so one caller's detected rate limit pauses everyone, not just itself:

```fsharp
member _.Pause(ms: int) =
    lock gate (fun () -> pausedUntil <- max pausedUntil (getNow().AddMilliseconds(float ms)))
```

`Acquire()` checks `pausedUntil` first and returns the remaining wait if still in the future, regardless of token count.

Wire this into `withRetryDelays` (`GhClient.fs:110-127`) via a new optional `onRetry: int -> unit` callback (default no-op) invoked with the delay right before each `Async.Sleep` in the rate-limit branch; `runGhApi`, `runGhApiGraphQL`, and the new `runGhApiGraphQLParsed` pass `bucket.Pause`. Other concurrent repo checks calling `bucket.Acquire()` during the pause window now wait instead of continuing to hit the API.

### 4. Tests

Follow the existing pattern in `tests/OrcAI.GitHub.Tests/RateLimitTests.fs` (pure `internal` helpers exercised directly via `InternalsVisibleTo`, injectable clock, no process/HTTP mocking — there's no seam for that today and adding one is out of scope):

- `ApiBucket.Pause` — a paused bucket makes `Acquire()` return a positive wait even with tokens available; a concurrent caller mid-backoff is held until `pausedUntil`.
- `tryParseJson` — valid JSON round-trips; an HTML body (`"<html>..."`) returns `Error` whose message satisfies `isMalformedResponse`/`isRateLimit`.
- `globalRateLimitError` — a GraphQL payload with a path-less `"You have exceeded a secondary rate limit"` error returns `Some`; a payload with only path-scoped errors (existing per-repo case) returns `None`.
- `withRetryDelays` with a malformed-response error — reuse the existing rate-limit test shape (`RateLimitTests.fs:70-80`) with an HTML-flavored message, asserting it retries on the same schedule as `isRateLimit`.
- `classifyCause` (`tests/OrcAI.Core.Tests`, follow existing `InlineData` pattern) — add cases for the malformed/non-JSON message mapping to `RateLimit`.

Per CLAUDE.md: each of these is effectively a regression test for a real bug, so write it to fail against current code first, then confirm it passes after the fix.

### 5. CHANGELOG.md

Add an entry under `## [Unreleased]` → `### Fixed` in `CHANGELOG.md` (existing section at line 3-7) describing: repos are no longer misreported as "not found" when GitHub returns a secondary-rate-limit error during bulk repo/issue lookups; malformed/HTML API responses no longer crash a run; rate-limit backoff now pauses concurrent in-flight repo checks instead of letting them keep hitting the API.

## Verification

- `dotnet test` — run `tests/OrcAI.GitHub.Tests` and `tests/OrcAI.Core.Tests` (`classifyCause`, `LockFileTests`) to confirm new/updated tests fail before the fix and pass after.
- `dotnet build` across the solution to confirm no type errors from the `Result<JsonElement,_>` threading changes.
- Manual: if feasible, re-run `orcai run apigateway_specializedwafprofile.yml --verbose --continue-on-error --on-closed-issue skip --dryrun` (both with and without `--no-parallel`) against the real 152-repo job to confirm no repo is misreported and no crash occurs. If not reproducible on demand (rate limiting is somewhat load/timing dependent), rely on the unit tests above as the primary verification, per the acceptance criteria's "or simulate 100+ rapid calls" fallback.
