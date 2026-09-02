# Handle non-GraphQL-shaped error envelopes (gateway timeout) in bulk repo-state fetch

## Context

Follow-up to `plans/fix-github-rate-limit-and-crash.md` (shipped in `eb3f87f`, released as v0.10.2). That fix addressed two failure modes: non-JSON/HTML response bodies, and proper GraphQL error envelopes (`{"data":..,"errors":[..]}`) with a path-less rate-limit-flavored message. Both are now handled via `tryParseJson` + `globalRateLimitError`.

Retesting `orcai run apigateway_specializedwafprofile.yml --dryrun` (152 repos) against the released v0.10.2 still reproduces the original bug: repos get misreported as `"not found or inaccessible"` in large, clean chunk-boundary-aligned blocks (confirmed against the job's declared repo order — chunks of 50 alternate FAIL/OK/FAIL/OK exactly on the `List.chunkBySize 50` boundary in `FetchReposState`, `GhClient.fs:869`).

**Root cause — a third response shape, distinct from the two already handled.** Reproduced directly with `gh api graphql` using the same query shape `FetchReposState` builds (50 repos × 3 aliased fields, 2 of them `search`) against this job's real repos:

- One attempt returned `HTTP 504` with stderr `"We couldn't respond to your request in time..."` and a response body of `{"message": "We couldn't respond to your request in time. Sorry about that. Please try resubmitting your request and contact us if the problem persists."}`.
- This body is **valid JSON** — so `tryParseJson` succeeds and `isMalformedResponse`/`isRateLimit` are never consulted at all.
- It is **not GraphQL-shaped** — no `data` key, no `errors` key. It's GitHub's generic gateway-timeout REST-style error envelope, not a GraphQL response.
- `globalRateLimitError` (`GhClient.fs:245-253`) only inspects `doc.TryGetProperty("errors")`; finding none, it returns `None`.
- So `runGhApiGraphQLParsed` returns `Ok doc` — the timeout is treated as a **successful** response.
- Back in `FetchReposState`, `doc.TryGetProperty("data")` also finds nothing (`data = None`), so every repo in the chunk falls into the `None -> ... Option.defaultValue "not found or inaccessible"` branch (`GhClient.fs:938-951`) with `errorsByAlias` empty (no `errors` array to build it from) — every repo in the chunk is misreported as not found, exactly matching the observed bug.

This is a gateway-level failure (GitHub's edge/nginx layer timing out on an expensive query before it ever reaches GraphQL execution), so it makes sense it doesn't come back GraphQL-shaped — but the current code assumes any successfully-parsed JSON is a GraphQL response with meaningful `data`/`errors`.

## Fix

In `runGhApiGraphQLParsed` (`GhClient.fs:259-...`), after `tryParseJson` succeeds, before (or alongside) calling `globalRateLimitError`, detect a non-GraphQL-shaped body and treat it as a retryable transient error rather than `Ok doc`:

```fsharp
let private isGraphQLShaped (doc: JsonElement) : bool =
    let has name = match doc.TryGetProperty(name) with true, _ -> true | _ -> false
    has "data" || has "errors"

// in runGhApiGraphQLParsed, after tryParseJson succeeds:
match tryParseJson json with
| Error e -> return Error e
| Ok doc ->
    if not (isGraphQLShaped doc) then
        // Gateway-level failure before GraphQL execution (e.g. HTTP 502/504 timeout
        // wrapper) — has neither `data` nor `errors`, so it isn't a real GraphQL
        // response at all. Surface the message if present so it retries/backs off
        // like any other transient failure, instead of being treated as Ok with no data.
        let msg =
            match doc.TryGetProperty("message") with
            | true, m -> m.GetString()
            | _ -> "non-GraphQL response from GitHub (likely a gateway timeout)"
        return Error msg
    else
        match globalRateLimitError doc with
        | Some msg -> return Error msg
        | None -> return Ok doc
```

Route this through the existing retry machinery correctly:
- Extend `isTransient` (`GhClient.fs:102-107`) to match this message shape — either match the literal GitHub timeout wording (`"couldn't respond to your request in time"`) or, more robustly, have `runGhApiGraphQLParsed` tag this specific case with a message that's already covered by `isRateLimit`/`isTransient` (e.g. reuse `isMalformedResponse`'s bucket, since in effect this is the same class of "GitHub's edge layer rejected an oversized/expensive request" as the HTML block-page case — arguably `isMalformedResponse` should be renamed/broadened rather than adding a third parallel category). Given the two already-handled cases (HTML body, path-less GraphQL rate-limit error) both route through the rate-limit backoff+chunk-retry path, this third case (valid JSON, non-GraphQL-shaped) should too, for consistency and so `bucket.Pause` also fires here — a gateway timeout on an expensive query is a strong signal to also back off other concurrent/queued expensive queries, not just retry this one chunk immediately.
- Confirm the retried chunk failing 3x still doesn't fall back to the wrong classification: with this fix, `FetchReposState`'s chunk-level `Error e ->` branch now receives `e` = the real timeout message (or the mapped rate-limit-flavored message) instead of nothing — and `RunCommand.fs`'s `isRepoNotFoundError` (`RunCommand.fs:184-186`, exact-match on `"not found or inaccessible"` or contains `"Could not resolve to a Repository"`) correctly does **not** match this message, so it should already fall through to the transient/per-repo-retry path rather than being misreported as "not found" — verify this end-to-end once the fix is in, since it depends on the exact final error string threaded through.

## Considerations specific to why this keeps happening at this job's scale

152 repos × 3 aliased fields (2 of them `search`, one of GitHub's most expensive GraphQL root fields) in chunks of 50 = up to 150 aliased fields, 100 of them `search`, per single GraphQL call. This is a heavy enough query that GitHub's edge/gateway times it out under real-world load (reproduced live, not simulated). Beyond fixing the misclassification, consider whether the batch itself should be smaller specifically for the `search`-bearing fields — e.g. chunk `isArchived` lookups at a larger size (cheap, like `ReposExist`'s existing 100) and the two `search` lookups at a much smaller size (10-20 repos → 20-40 `search` calls per query) so the query stays under whatever complexity threshold triggers the gateway timeout, rather than only fixing error handling after the fact. Error-handling correctness (this plan) and reducing how often it's needed (chunk sizing) are complementary, not alternatives.

## Verification

- Add a unit test for `runGhApiGraphQLParsed`'s new non-GraphQL-shape check: a JSON body with neither `data` nor `errors` (e.g. `{"message": "..."}`) should not be treated as `Ok`.
- `dotnet test` across `tests/OrcAI.GitHub.Tests` and `tests/OrcAI.Core.Tests`.
- Manual: re-run `orcai run apigateway_specializedwafprofile.yml --verbose --continue-on-error --on-closed-issue skip --dryrun` (and `--no-parallel`) against the real 152-repo job. Confirm no repo is misreported as not found — spot-check against `gh repo view <repo> --json name,isArchived,viewerPermission` as ground truth, the technique used to confirm the original bug (7/7 spot-checked repos there were real, non-archived, admin-accessible).
- Reproduce the underlying gateway-timeout condition directly if needed: `gh api graphql --input <file>` with a query shaped like `FetchReposState`'s (aliased `repository{isArchived}` + two `search(...)` per repo, 50 repos) — this reliably surfaces the `HTTP 504` / `{"message": "..."}` response under real load, no mocking required for a manual check (though the unit test above should use a canned fixture, not live GitHub).
