# Mixed-Auth Copilot Assignment

**Status:** Implemented

## Description

When the primary GitHub auth is a GitHub App, the App cannot assign @copilot to issues (GitHub Apps lack that permission). This plan adds support for a secondary PAT credential used exclusively for the `AssignIssue` call. If no PAT is resolvable and primary auth is App-based, OrcAI warns per-repo and skips assignment gracefully (exit 0).

## Purpose

Users authenticating with a GitHub App for API calls (projects, issues) would silently fail or error when trying to assign @copilot. This change allows those users to provide a secondary PAT (via `ORCAI_PAT`, an `auth.json` `pat` profile, `GH_TOKEN`, or `gh auth token`) that is used only for copilot assignment — preserving App auth for all other calls.

---

## Scope

### Behaviour

1. **PAT-primary auth** — `CopilotClient` is `None`; `IsPrimaryAuthApp = false`. Copilot assignment uses the primary `GhClient` as before. No change in behaviour.
2. **App-primary auth + PAT resolved** — `CopilotClient = Some <patClient>`; `IsPrimaryAuthApp = true`. Copilot assignment uses `CopilotClient`.
3. **App-primary auth + no PAT** — `CopilotClient = None`; `IsPrimaryAuthApp = true`. A per-repo warning is emitted to stderr and copilot assignment is skipped. Exit code remains 0.

No new flags, CLI args, or storage formats are introduced. The existing PAT resolution chain is reused:
`ORCAI_PAT` env var → `auth.json` `pat` profile → `GH_TOKEN` env var → `gh auth token`.

### Tasks

1. **Add `CopilotClient` to `OrcAIDeps`** (`OrcAI.Core/Deps.fs`)
   New field: `CopilotClient : IGhClient option` with doc comment explaining its purpose.

2. **Add `IsPrimaryAuthApp` to `RunInput`** (`OrcAI.Core/RunCommand.fs`)
   Boolean field threaded into `processRepo`; used to determine warning vs. skip vs. use-PAT behaviour for copilot assignment.

3. **Update `processRepo`** (`OrcAI.Core/RunCommand.fs`)
   Replace `client: IGhClient` parameter with `deps: OrcAIDeps`. In the copilot assignment branch, match on `(deps.CopilotClient, isPrimaryAuthApp)`:
   - `None, true` → warn to stderr, return issue unchanged.
   - `Some c, _` → use `c` for `AssignIssue`.
   - `None, false` → use primary `deps.GhClient` as before.

4. **Detect App auth and resolve secondary PAT** (`OrcAI.Tool/Program.fs`)
   - Change `withClient` signature from `(OrcAIDeps -> int)` to `(OrcAIDeps -> bool -> int)` to pass `isPrimaryAuthApp` downstream.
   - Detect App-primary auth via `:? AppAuthContext` type test.
   - When App-primary, attempt to resolve a secondary PAT via `PatAuth.loadToken ()` and construct a PAT-based `GhClient`. Set `CopilotClient` accordingly.
   - Pass `IsPrimaryAuthApp` into `RunInput` in the `Run` dispatch branch.

5. **Update all `withClient` call sites** (`OrcAI.Tool/Program.fs`)
   Five call sites: `Run`, `Cleanup`, `Info`, `Generate`, `Validate`. Only `Run` consumes `isPrimaryAuthApp`; the rest use `_`.

6. **Update test helpers** (`tests/OrcAI.Core.Tests/`)
   - `RunCommandTests.fs` `makeDeps`: add `CopilotClient = None`.
   - `RunCommandTests.fs` `defaultInput`: add `IsPrimaryAuthApp = false`.
   - `ValidateCommandTests.fs` `makeDeps`: add `CopilotClient = None`.

7. **Add new unit tests** (`tests/OrcAI.Core.Tests/RunCommandTests.fs`)
   Add a `TrackingGhClient` type that records which client label was used for `AssignIssue`, then add:
   - `processRepo uses CopilotClient when Some for AssignIssue`
   - `processRepo uses primary client for AssignIssue when CopilotClient is None and IsPrimaryAuthApp=false`
   - `processRepo skips assignment and does not call AssignIssue when CopilotClient=None and IsPrimaryAuthApp=true`
   - `processRepo skips assignment entirely when skipCopilot=true regardless of CopilotClient`

8. **Mark backlog item `[x]`** in `BACKLOG.md` (line 24).

---

## Dependencies / Prerequisites

- No new packages required.
- `PatAuth.loadToken ()` and `AppAuthContext` already exist in `OrcAI.Auth`.

---

## Impact on Existing Code

| File | Change |
|---|---|
| `OrcAI.Core/Deps.fs` | Add `CopilotClient : IGhClient option` field |
| `OrcAI.Core/RunCommand.fs` | Add `IsPrimaryAuthApp` to `RunInput`; refactor `processRepo` to accept `deps`; add mixed-auth copilot logic |
| `OrcAI.Tool/Program.fs` | Change `withClient` signature; add App detection + secondary PAT resolution; pass `IsPrimaryAuthApp` to `RunInput` |
| `tests/OrcAI.Core.Tests/RunCommandTests.fs` | Add `CopilotClient`, `IsPrimaryAuthApp`, `TrackingGhClient`, 4 new tests |
| `tests/OrcAI.Core.Tests/ValidateCommandTests.fs` | Add `CopilotClient = None` to `makeDeps` |

No existing command logic or storage formats are modified.

---

## Acceptance Criteria

- When primary auth is PAT-based: copilot assignment uses the primary client as before.
- When primary auth is App-based and `ORCAI_PAT` (or equivalent) is set: copilot assignment uses a separate PAT-authenticated client; all other calls continue to use the App client.
- When primary auth is App-based and no PAT is resolvable: a per-repo warning is emitted to stderr and copilot assignment is skipped. Exit code is 0.
- `--skip-copilot` continues to suppress assignment regardless of auth type.
- All new code covered by unit tests; all existing tests continue to pass.
- No new CLI flags, storage formats, or breaking changes.

---

## Testing Strategy

### Unit tests (`OrcAI.Core.Tests/RunCommandTests.fs`)

- `processRepo uses CopilotClient when Some for AssignIssue` — verifies the secondary PAT client is called, not the primary.
- `processRepo uses primary client for AssignIssue when CopilotClient is None and IsPrimaryAuthApp=false` — PAT-primary path unchanged.
- `processRepo skips assignment and does not call AssignIssue when CopilotClient=None and IsPrimaryAuthApp=true` — warn-and-skip path.
- `processRepo skips assignment entirely when skipCopilot=true regardless of CopilotClient` — existing skip behaviour is unaffected.

### Manual / integration

- Authenticate with a GitHub App; set `ORCAI_PAT` to a valid PAT; run `orcai run` — expect copilot assigned.
- Authenticate with a GitHub App; unset `ORCAI_PAT`; run `orcai run` — expect per-repo warning and exit 0.
- Authenticate with a PAT only; run `orcai run` — expect copilot assigned as before.

---

## Risks & Mitigations

| Risk | Mitigation |
|---|---|
| PAT has insufficient scopes for copilot assignment | Surface the raw `AssignIssue` error to stderr as a warning (existing error handling) |
| Secondary client construction fails at startup | `PatAuth.loadToken ()` returns `None` on failure; `CopilotClient` is set to `None` and the warn-and-skip path applies |
| Accidentally using App token for copilot assignment | Type test on `AppAuthContext` is explicit; the fallback `Option.defaultValue client` only activates when `IsPrimaryAuthApp = false` |
