module OrcAI.GitHub.Tests.RateLimitTests

open System
open System.Text.Json
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

// --- ApiBucket.Pause ----------------------------------------------------

[<Fact>]
let ``ApiBucket Acquire waits out an active pause even with tokens available`` () =
    let getNow, advance = mockNow DateTime.UtcNow
    let bucket = ApiBucket(60, getNow)
    // Plenty of tokens (80% warm start), but a rate-limit hit pauses everyone.
    bucket.Pause(5_000)
    let wait = bucket.Acquire()
    Assert.True(wait > 0, "expected Acquire to report a wait while paused")
    // Right as the pause lifts, the bucket resumes empty (zeroed by Pause) rather
    // than silently refilling for the whole paused duration — so a caller must
    // still wait out one token's worth of the normal per-minute pace.
    advance 5.1
    Assert.True(bucket.Acquire() > 0, "expected the bucket to resume empty, not fully refilled, right after a pause")
    // Once enough real time has passed for the per-minute pace to mint a token
    // (60/min => 1/sec), a call goes through again.
    advance 1.1
    Assert.Equal(0, bucket.Acquire())

[<Fact>]
let ``ApiBucket does not silently refill for the duration of a pause`` () =
    let getNow, advance = mockNow DateTime.UtcNow
    let bucket = ApiBucket(60, getNow)
    // A 60s pause at 60/min cap would — if lastRefill were left frozen — look
    // like a full minute's worth of unthrottled traffic once elapsed-time refill
    // math ran again, handing back a full bucket right when we most need the
    // throttle to still be in effect.
    bucket.Pause(60_000)
    advance 60.1
    let mutable freeAcquires = 0
    for _ in 1..10 do
        if bucket.Acquire() = 0 then freeAcquires <- freeAcquires + 1
    Assert.True(freeAcquires <= 1, $"expected at most one immediately-available token right after the pause, got {freeAcquires}")

[<Fact>]
let ``ApiBucket Pause only ever extends the pause window forward`` () =
    let getNow, advance = mockNow DateTime.UtcNow
    let bucket = ApiBucket(60, getNow)
    bucket.Pause(10_000)
    advance 1.0
    // A shorter pause request shouldn't shorten the existing window.
    bucket.Pause(1_000)
    let wait = bucket.Acquire()
    // ~9s should remain from the original 10s pause, not ~1s from the second call.
    Assert.True(wait > 5_000, $"expected the longer pause to still be in effect, wait={wait}ms")

// --- tryParseJson ---------------------------------------------------------

[<Fact>]
let ``tryParseJson round-trips valid JSON`` () =
    match tryParseJson """{"a":1}""" with
    | Ok el -> Assert.Equal(1, el.GetProperty("a").GetInt32())
    | Error e -> Assert.Fail($"expected Ok, got Error {e}")

[<Fact>]
let ``tryParseJson classifies an HTML body as rate-limit-flavored instead of throwing`` () =
    let html = "<html><body>You have been rate limited</body></html>"
    match tryParseJson html with
    | Ok _ -> Assert.Fail("expected Error for a non-JSON body")
    | Error e ->
        Assert.True(isMalformedResponse e, $"expected message to be classified malformed: {e}")
        Assert.True(isRateLimit e, "malformed responses must be routed into the rate-limit retry path")

// --- globalRateLimitError ---------------------------------------------------

[<Fact>]
let ``globalRateLimitError finds a path-less secondary-rate-limit GraphQL error`` () =
    let json = """{"data":{"r0":null},"errors":[{"message":"You have exceeded a secondary rate limit"}]}"""
    let doc = JsonDocument.Parse(json).RootElement
    match globalRateLimitError doc with
    | Some msg -> Assert.Contains("secondary rate limit", msg)
    | None -> Assert.Fail("expected a global rate-limit error to be detected")

[<Fact>]
let ``globalRateLimitError ignores ordinary path-scoped per-repo errors`` () =
    let json = """{"data":{"r0":null},"errors":[{"message":"Could not resolve to a Repository","path":["r0"]}]}"""
    let doc = JsonDocument.Parse(json).RootElement
    Assert.Equal(None, globalRateLimitError doc)

// --- retry on malformed / non-JSON responses -------------------------------

[<Fact>]
let ``withRetryDelays retries a malformed-response error on the rate-limit schedule`` () =
    let mutable attempts = 0
    let malformedMsg = "non-JSON response from GitHub (likely rate limited/blocked): <html>..."
    let run () = async {
        attempts <- attempts + 1
        if attempts < 3 then return Error malformedMsg
        else return Ok "done"
    }
    let result = withRetryDelays 3 10 5 run |> Async.RunSynchronously
    Assert.Equal(Ok "done", result)
    Assert.Equal(3, attempts)

[<Fact>]
let ``withRetryDelaysNotify pauses the shared bucket on each rate-limit retry`` () =
    let getNow, _ = mockNow DateTime.UtcNow
    let bucket = ApiBucket(60, getNow)
    let mutable attempts = 0
    let run () = async {
        attempts <- attempts + 1
        if attempts < 2 then return Error rateLimitMsg
        else return Ok "done"
    }
    let result = withRetryDelaysNotify 3 10 5 bucket.Pause run |> Async.RunSynchronously
    Assert.Equal(Ok "done", result)
    // Acquire() should now report a wait, proving Pause was invoked by the retry loop.
    Assert.True(bucket.Acquire() > 0)
