# Plan: Generalise `assign:` into typed `action:` field

## Goal

Replace the implicit copilot-assignment behaviour and the `assign:` / `skipCopilot` knobs with an explicit, typed `action:` field in the job YAML. Backward compatibility is intentionally broken (pre-v1); old fields produce hard validation errors with migration messages.

---

## YAML schema

`issue:` is unchanged. `action:` is a new optional sibling. If absent the behaviour is identical to `type: assign-copilot` with all defaults.

```yaml
action:
  type: assign-copilot   # default when action: is omitted
  comment: "..."         # optional trigger comment

action:
  type: assign
  to: "@someuser"
  comment: "..."         # optional

action:
  type: comment
  comment: "..."         # required

action:
  type: comment-and-assign
  to: "@someuser"
  comment: "..."

action:
  type: cmd
  execute: "./scripts/setup.sh"   # script path; mutually exclusive with run:
  args: ["--flag", "value"]       # optional
  cwd: "./subdir"                 # optional

action:
  type: cmd
  run: "gh issue comment {{issue_number}} --body 'done'"   # inline command

action:
  type: noop   # do nothing after issue creation (replaces skipCopilot: true)
```

### Action types

| type | required params | optional params |
|---|---|---|
| `assign-copilot` | — | `comment` |
| `assign` | `to` | `comment` |
| `comment` | `comment` | — |
| `comment-and-assign` | `to`, `comment` | — |
| `cmd` | `execute` or `run` (mutually exclusive) | `args`, `cwd` |
| `noop` | — | — |

`cmd` params:
- `execute` — path to a script file (e.g. `./scripts/setup.sh`)
- `run` — inline shell command string (e.g. `gh issue comment {{issue_number}} --body "done"`)
- Exactly one of `execute` / `run` must be provided; both present is a validation error.

---

## Breaking changes (hard validation errors)

Both `parse` (`YamlConfig.parseFile`) and `validate` (`ValidateCommand`) must reject:

- **`assign:` present** → `"The 'assign:' field has been replaced by 'action:'. Migrate to: action: { type: assign-copilot, ... }"`
- **`job.skipCopilot: false`** → `"'job.skipCopilot' has been removed. To assign copilot, omit the 'action:' field or set 'action: { type: assign-copilot }'."`
- **`job.skipCopilot: true`** → `"'job.skipCopilot' has been removed. To skip assignment, use 'action: { type: noop }'."`

---

## Template variables for `cmd` (`{{var}}` syntax)

All vars use double-brace syntax. Implemented via a new `renderActionTemplate` helper (extending the existing `renderTemplate` pattern).

| variable | value |
|---|---|
| `{{project_number}}` | GitHub project number |
| `{{issue_number}}` | issue number in the repo |
| `{{issue_url}}` | full GitHub URL of the issue |
| `{{repo}}` | `owner/repo` |
| `{{org}}` | organisation name |
| `{{job_title}}` | value of `job.title` |
| `{{issue_text}}` | rendered issue body |
| `{{issue_hash}}` | SHA-256 of issue template content |
| `{{yaml_hash}}` | SHA-256 of the YAML file |
| `{{run_datetime}}` | ISO-8601 datetime of the run |

---

## Implementation steps

### 1. Domain (`Domain.fs`)

- Add discriminated union:
  ```fsharp
  type CmdSource =
      | Script  of path: string   // execute:
      | Inline  of command: string // run:

  type ActionConfig =
      | AssignCopilot    of comment: string option
      | Assign           of ``to``: string * comment: string option
      | Comment          of comment: string
      | CommentAndAssign of ``to``: string * comment: string
      | Cmd              of source: CmdSource * args: string list * cwd: string option
      | Noop
  ```
- Add `Action : ActionConfig` field to `JobConfig`, replacing `Assign : AssignConfig option`.
- Remove `AssignConfig` type (subsumed by `ActionConfig`).
- Remove `SkipCopilot : bool` from `JobConfig`.

### 2. YAML parsing (`YamlConfig.fs`)

- Add `YamlAction` DTO:
  ```fsharp
  [<CLIMutable>]
  type YamlAction =
      { ``type``  : string
        comment   : string
        ``to``    : string
        execute   : string   // script path
        run       : string   // inline command
        args      : System.Collections.Generic.List<string>
        cwd       : string }
  ```
- Add `action: YamlAction` to `YamlRoot`.
- In `parse`:
  - If `root.assign` is not null → hard error with migration message.
  - If `root.job.skipCopilot = true` → hard error: migrate to `noop`.
  - If `root.job.skipCopilot = false` (explicitly set) → hard error: remove the field.
  - Parse `root.action` into `ActionConfig`; default to `AssignCopilot None` when null.
  - Validate required params per type (e.g. `assign` requires `to`).
  - For `cmd`: both `execute` and `run` present → error; neither present → error.
- Remove `YamlAssign` DTO and all `assign` parsing logic.
- Remove `skipCopilot` from `YamlJob`.

### 3. Validate command (`ValidateCommand.fs`)

- Add the same two checks (`assign:` present, `skipCopilot: true`) before delegating to `YamlConfig.parse`, so `orcai validate` surfaces them with clear messages.

### 4. Run command (`RunCommand.fs`)

- Replace the `assignTo` / `assignVia` / `assignComment` resolution block with a match on `jobConfig.Action`.
- `AssignCopilot` — existing PAT/App logic, optional trigger comment.
- `Assign` — assign to `to`, optional comment, no PAT special-case.
- `Comment` — post comment only.
- `CommentAndAssign` — post comment then assign.
- `Cmd` — resolve `CmdSource` (script path or inline), shell-execute with `args` in `cwd`, with `{{var}}` substitution.
- `Noop` — skip the action step entirely; log that assignment was skipped.
- Remove all `SkipCopilot` / `skipCopilot` references.

### 5. Template rendering

- Add `renderActionTemplate : Map<string,string> -> string -> string` using `{{key}}` double-brace substitution (distinct from the existing `{key}` single-brace `renderTemplate`).
- Build the var map at run time from `ProjectInfo`, `IssueRef`, `JobConfig`, and run datetime.

### 6. Global config (`OrcAIConfig.fs`)

- Remove `SkipCopilot` field from `OrcAIConfig` and `OrcAIConfigDto`.
- Remove `Assign : AssignConfig option` from `OrcAIConfig` (no longer merged from global config — action is job-local).
- Update `merge` accordingly.

### 7. Tests

- Unit tests for `YamlConfig.parse`:
  - `assign:` present → error with migration message.
  - `skipCopilot: true` → error pointing to `noop`.
  - `skipCopilot: false` → error pointing to removing the field.
  - Missing `action:` → defaults to `AssignCopilot None`.
  - Each action type parses correctly, including `noop`.
  - `cmd` with both `execute` and `run` → error.
  - `cmd` with neither `execute` nor `run` → error.
  - Missing required param (e.g. `assign` without `to`) → error.
- Unit tests for `renderActionTemplate` with each `{{var}}`.
- Update existing `RunCommand` tests to use `Action` field instead of `Assign`.

### 8. Examples

- Update `example/*.yml` files: remove `copilot:` and `assign:` blocks, add `action:` blocks.

---

## Out of scope (future)

- `assign-opencode`, `assign-pi`, and other AI-agent action types.
- `cmd` action cloning/copying files into repos (complex; deferred).
- `notify` is unchanged — it remains a separate post-run command.
