# Run & Nudge Enhancements: Configurable Assignee, Trigger Mode, and Nudge Comments

## Context

Both `orcai run` and `orcai nudge` hardcode `"@copilot"` as the issue assignee and use assignment as the only trigger mechanism. This prevents teams from using alternative agents (e.g. [OpenCode](https://opencode.ai/docs/github/), which is triggered by a `/opencode` comment rather than assignment) or human reviewers. Three gaps need to be closed:

1. **Assignee is hardcoded** — `run` and `nudge` both hardcode `"@copilot"`.
2. **Assignment is the only trigger** — agents like OpenCode are triggered by a comment, not an assignment. `skipCopilot` is a blunt workaround.
3. **No comment support on nudge** — there is no way to post a traceable message on an issue when a nudge fires.

## Config design

Two new top-level blocks added to the job YAML schema (parsed by `YamlConfig.fs`), with the same fields available as global/local defaults in `OrcAIConfig` (JSON).

The ignored `copilot:` block already scaffolded by `GenerateCommand` is superseded by these.

### `assign` block (used by `run` and `nudge`)

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `to` | string | `"@copilot"` | Assignee handle. Optional when `via` is `"comment"`. |
| `via` | string | `"assign"` | How to trigger: `"assign"` \| `"comment"` \| `"comment-and-assign"` |
| `comment` | string | — | Comment body when `via` includes `"comment"`. Supports `{assignee}` placeholder. |

### `nudge` block (used by `nudge` only)

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `mode` | string | `"reassign"` | `"reassign"` \| `"comment-only"` \| `"comment-and-reassign"` |
| `comment` | string | — | Comment body posted on nudge. Supports `{assignee}` placeholder. |

`skipCopilot` is superseded by `assign.via: comment` and can be deprecated in a follow-up.

### Job YAML examples

Human assignee:
```yaml
assign:
  to: devon.burriss
  via: assign
  comment: "Hey, this issue is ready for you"

nudge:
  mode: comment-and-reassign
  comment: "Hey @devon.burriss, any update on this one?"
```

OpenCode:
```yaml
assign:
  to: opencode-agent[bot]
  via: comment
  comment: "/opencode please work on this issue"

nudge:
  mode: comment-only
  comment: "/opencode this issue seems stuck, please continue"
```

Copilot (backwards-compatible — no config needed, all defaults):
```yaml
assign:
  to: "@copilot"
  via: assign
```

## Scope

### 1. `YamlConfig.fs`

Add two new DTO types and wire them into the top-level YAML DTO:

```fsharp
[<CLIMutable>]
type AssignDto =
    { To      : string
      Via     : string
      Comment : string }

[<CLIMutable>]
type NudgeDto =
    { Mode    : string
      Comment : string }
```

Add to the top-level YAML DTO:
```fsharp
Assign : AssignDto   // nullable/default via YamlDotNet
Nudge  : NudgeDto
```

Expose on the parsed `YamlJobConfig` record:
```fsharp
Assign : AssignConfig option
Nudge  : NudgeConfig option
```

### 2. `OrcAIConfig.fs`

Add matching nested record types for global/local JSON config defaults (same fields, all `option`). Job YAML wins over `OrcAIConfig` when both specify a value — same precedence pattern as `skipCopilot`.

### 3. `GhClient.fs` / `FakeGhClient.fs`

Add `PostComment` to `IGhClient`:

```fsharp
PostComment : string -> int -> string -> Async<unit>
// repo        issueNumber  body
```

Implement in `GhCliClient` via `gh issue comment <n> --repo <r> --body <b>`.  
Add stub to `FakeGhClient.Handlers` and `defaultHandlers`.

### 4. `RunCommand.fs`

Replace hardcoded `"@copilot"` with resolved `assign.to` (default `"@copilot"`).  
Branch on `assign.via`:
- `"assign"` (default) — existing behaviour: call `AssignIssue`.
- `"comment"` — call `PostComment` with resolved body; skip `AssignIssue`.
- `"comment-and-assign"` — call both.

### 5. `NudgeCommand.fs`

Replace hardcoded `"@copilot"` with resolved `assign.to` (default `"@copilot"`).  
Branch on `nudge.mode`:
- `"reassign"` (default) — existing behaviour: `UnassignIssue` + `AssignIssue`.
- `"comment-only"` — `PostComment` only.
- `"comment-and-reassign"` — `PostComment` + `UnassignIssue` + `AssignIssue`.

Post `nudge.comment` (with `{assignee}` substituted) when a comment body is configured.

### 6. `GenerateCommand.fs`

Remove the `copilot:` block from the scaffolded YAML output. It has never been parsed and is superseded by the `assign:` block. No replacement scaffold is needed — `assign:` defaults are all backwards-compatible with the existing behaviour.

## Implementation order

1. `YamlConfig.fs` — add `AssignDto`, `NudgeDto`, wire into top-level DTO and parsed record.
2. `OrcAIConfig.fs` — add matching nested record types for global/local defaults.
3. `GhClient.fs` / `FakeGhClient.fs` — add `PostComment`.
4. `RunCommand.fs` — wire up `assign` config.
5. `NudgeCommand.fs` — wire up `assign.to`, `nudge.mode`, `nudge.comment`.
6. `GenerateCommand.fs` — remove `copilot:` block from generated YAML scaffold.

## Verification

1. **Unit tests** — add `NudgeCommandTests.fs` and extend `RunCommandTests.fs` using `FakeGhClient`. Cover each `via` and `nudge.mode` value; assert correct calls to `PostComment` / `AssignIssue` / `UnassignIssue`. Verify `{assignee}` substitution.
2. **Dry-run smoke test** — run `orcai nudge <yaml> --dry-run` with an `assign` block in the job YAML and confirm table output reflects the configured assignee.
3. **Backwards-compatibility check** — run with no config and confirm existing `@copilot` assign behaviour is unchanged.
4. **Live integration test** — test against a real stale issue for each mode and verify GitHub reflects the expected state.
