module OrcAI.GitHub.Tests.RateLimitTests

open System
open Xunit
open OrcAI.GitHub.GhClient

// ---------------------------------------------------------------------------
// Unit tests for the token-bucket and retry helpers in GhClient.
//
// The helpers under test are `internal`; visibility is granted via
// InternalsVisibleTo in src/OrcAI.GitHub/AssemblyInfo.fs.
//
// `ApiBucket` accepts an injected time source so tests advance "now"
// without sleeping. `withRetryDelays` takes the initial backoff delays as
// parameters so tests don't actually wait the production 60s / 2s.
// ---------------------------------------------------------------------------

/// Frozen-clock helper. Returns (getNow, advance) where `advance secs`
/// moves the mock time forward by `secs` seconds.
let private mockNow (start: DateTime) =
    let mutable current = start
    let getNow () = current
    let advance (secs: float) = current <- current.AddSeconds(secs)
    getNow, advance

// --- Bucket -----------------------------------------------------------------

[<Fact>]
let ``ApiBucket starts at 80% of capacity`` () =
    let getNow, _ = mockNow DateTime.UtcNow
    let bucket = ApiBucket(60, getNow)
    // 60 * 4 / 5 = 48 initial tokens.
    for i in 1..48 do
        Assert.Equal(0, bucket.Acquire())
    // Next acquire must wait.
    Assert.True(bucket.Acquire() > 0)

[<Fact>]
let ``ApiBucket refills on time`` () =
    let getNow, advance = mockNow DateTime.UtcNow
    let bucket = ApiBucket(60, getNow)
    // Exhaust the warm-start 48 tokens.
    for _ in 1..48 do
        Assert.Equal(0, bucket.Acquire())
    Assert.True(bucket.Acquire() > 0)
    // 60/min => 1 token/sec. Advance 1s, expect exactly one fresh acquire.
    advance 1.0
    Assert.Equal(0, bucket.Acquire())
    Assert.True(bucket.Acquire() > 0)

[<Fact>]
let ``ApiBucket caps tokens at perMinuteCap`` () =
    let getNow, advance = mockNow DateTime.UtcNow
    let bucket = ApiBucket(60, getNow)
    // Idle 5 minutes => naive refill would be 300 tokens; cap must clamp to 60.
    advance 300.0
    let mutable acquired = 0
    let mutable hitWait = false
    for _ in 1..70 do
        if bucket.Acquire() = 0 then acquired <- acquired + 1
        else hitWait <- true
    Assert.Equal(60, acquired)
    Assert.True(hitWait)

// --- Retry ------------------------------------------------------------------

let private rateLimitMsg = "You have exceeded a secondary rate limit"
let private transientMsg = "connection reset by peer"

[<Fact>]
let ``withRetryDelays on rate-limit returns Ok after N attempts`` () =
    let mutable attempts = 0
    let run () = async {
        attempts <- attempts + 1
        if attempts < 3 then return Error rateLimitMsg
        else return Ok "done"
    }
    let result = withRetryDelays 3 10 5 run |> Async.RunSynchronously
    Assert.Equal(Ok "done", result)
    Assert.Equal(3, attempts)

[<Fact>]
let ``withRetryDelays on transient uses short backoff`` () =
    let mutable attempts = 0
    let run () = async {
        attempts <- attempts + 1
        if attempts < 2 then return Error transientMsg
        else return Ok "done"
    }
    // Set rate-limit initial to 60s so a misroute to the rate-limit branch
    // would blow past our 3s budget. Transient initial is tiny; the floor
    // is 500ms inside withRetryDelays.
    let sw = System.Diagnostics.Stopwatch.StartNew()
    let result = withRetryDelays 3 60_000 5 run |> Async.RunSynchronously
    sw.Stop()
    Assert.Equal(Ok "done", result)
    Assert.Equal(2, attempts)
    Assert.True(sw.Elapsed.TotalSeconds < 3.0,
                $"Expected transient backoff (<3s), got {sw.Elapsed.TotalSeconds}s")

[<Fact>]
let ``withRetryDelays gives up after maxAttempts`` () =
    let mutable attempts = 0
    let run () = async {
        attempts <- attempts + 1
        return Error rateLimitMsg
    }
    let result = withRetryDelays 2 10 5 run |> Async.RunSynchronously
    Assert.Equal(Error rateLimitMsg, result)
    Assert.Equal(2, attempts)

[<Fact>]
let ``withRetryDelays does not retry non-retriable errors`` () =
    let mutable attempts = 0
    let nonRetriable = "Could not resolve to a PullRequest"
    let run () = async {
        attempts <- attempts + 1
        return Error nonRetriable
    }
    let result = withRetryDelays 3 10 5 run |> Async.RunSynchronously
    Assert.Equal(Error nonRetriable, result)
    Assert.Equal(1, attempts)
