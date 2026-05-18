# Plan: improve nudge error handling UX

## Context

A run of marge's Nudge workflow (run 26051921598) emitted ugly multi-line warnings for every issue it tried to nudge. The marge workflow only shells out to `dotnet orcai nudge` — the actual error formatting and outcome reporting live in this repo (`orcai`).

Three UX problems compound in the log:

1. **Noisy stderr wrapping.** Each failed reassign produces a SimpleExec-formatted blob:
   ```
   Warning: failed to reassign @copilot: Failed to run 'gh issue edit 94 --repo ...':
   One or more errors occurred. (The command exited with code 1.

   Standard output (stdout):


   Standard error (stderr):

   failed to update https://.../issues/94: GraphQL: Assigning agents is not supported
   with GitHub App installation tokens. Use a user token ... (replaceActorsForAssignable)
   failed to update 1 issue
   )
   ```
   The actionable line is buried under SimpleExec's `AggregateException.Message`, with an empty stdout section and an unmatched-looking trailing `)`.

2. **No actionable hint** for the very specific (and common-in-CI) case where the failure is "Assigning agents is not supported with GitHub App installation tokens". The user has to read the buried GraphQL message to know they need a PAT.

3. **Misleading status — and a destructive side-effect.** When primary auth is a GitHub App and no PAT is stored, the nudge in `reassign` / `comment-and-reassign` mode runs `UnassignIssue` (succeeds — Apps can unassign) and then `AssignIssue` (fails — Apps can't assign agents). The issue is left **unassigned**, but `NudgeCommand` unconditionally returns `Outcome = NudgeSent`, so the summary table prints "nudged" and `printfn "Done. %d nudged, ..."` lies about the count. For a 200-repo run, this leaves @copilot detached from every issue with zero indication in the report.

The user has confirmed scope: clean the noise, add the hint, **and** fix status accuracy. A pre-flight short-circuit is also included to avoid the destructive unassign-without-reassign when we can predict it will fail.

This is **not** about adding the PAT to the workflow — that's a marge-side config change the user is aware of. This plan is solely about making the orcai CLI degrade gracefully and report honestly.

## Files to change (all in this repo)

| File | What changes |
|---|---|
| `src/OrcAI.GitHub/GhClient.fs:22-37` | Replace `runGh`'s catch to handle `SimpleExec.ExitCodeReadException` first; surface only the trimmed stderr in the error string. |
| `src/OrcAI.GitHub/GhClient.fs` (new helper near `isRateLimit` at line 43) | Add `isAppTokenAssignError` classifier. |
| `src/OrcAI.Core/NudgeCommand.fs:26-32` | Extend `NudgeOutcome` with `NudgeFailed of reason: string`; thread the result through. |
| `src/OrcAI.Core/NudgeCommand.fs:43-47` | Pre-flight: when fallback `assignClient = client`, `assignTo = "@copilot"`, and `nudgeMode` includes reassign, short-circuit with a single error before iterating over repos. |
| `src/OrcAI.Core/NudgeCommand.fs:96-105` | Capture assign/unassign results; map to `NudgeFailed`. Drop the per-issue `eprintfn` walls — the summary table shows failure counts and emits one consolidated hint line at the end. |
| `src/OrcAI.Tool/Program.fs:498-528` | Add a `failed` counter; render the new outcome as `[red]failed[/]` in the table; update the closing `printfn`; when any row failed with the App-token reason, print one consolidated hint referencing `ORCAI_PAT` / `orcai auth pat --token`. |

## Approach details

### 1. Strip SimpleExec wrapping (`runGh`)

`Command.ReadAsync` throws `ExitCodeReadException` (a subtype of `ExitCodeException`) which exposes `.StandardError` and `.StandardOutput` directly — we don't need to parse them out of `.Message`. Catch the subtype first and use the stderr.

### 2. App-token classifier

Add near `isRateLimit`:

```fsharp
let internal isAppTokenAssignError (msg: string) =
    msg.Contains("Assigning agents is not supported with GitHub App installation tokens",
                 StringComparison.OrdinalIgnoreCase)
```

### 3. Pre-flight short-circuit in `NudgeCommand.execute`

After resolving `assignClient`, `assignTo`, `nudgeMode`, refuse early when `IsPrimaryAuthApp && CopilotClient.IsNone && assignTo = "@copilot" && nudgeMode includes reassign`.

### 4. Honest outcomes

Capture `AssignIssue` / `UnassignIssue` errors into `Outcome = NudgeFailed reason`.

### 5. Table + summary

`Program.fs`:
- Count `NudgeFailed _` rows.
- Render `[red]failed[/]`.
- Update `printfn` summary.
- If any failed row matches the App-token error string, print once:
  > `Hint: @copilot assignment requires a PAT. Set ORCAI_PAT or run 'orcai auth pat --token <PAT>'.`

## Verification

1. **Unit / module level** — F# tests in `tests/OrcAI.Core.Tests/NudgeCommandTests.fs`:
   - Pre-flight short-circuit: `IsPrimaryAuthApp = true`, `CopilotClient = None`, `assignTo = "@copilot"`, `nudgeMode = "reassign"` → `execute` returns `Error` *before* any `IGhClient` call (use existing fake; assert counters stay 0).
   - When `AssignIssue` fails, the result row is `NudgeFailed`, not `NudgeSent`.
2. **Smoke test against the real CLI** locally:
   - `dotnet run --project src/OrcAI.Tool -- nudge <yaml>` with App auth + no PAT → exits non-zero with the single short-circuit message.
   - With PAT configured → normal flow; no formatting noise on a forced failure.
3. **Replay the marge scenario** — trigger marge's Nudge workflow with the fixed binary; expect one clear refusal (or honest table with failure counts and a single PAT hint) instead of 200 noisy walls of text.
