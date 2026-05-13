# Plan: Notify Command — CLI Template and Data Payload

## Context

The `orcai notify` command currently reads its comment template exclusively from the YAML config (`notify.comment`) or global config. There is no way to supply a template or extra template variables directly on the command line without editing a YAML file. This makes ad-hoc one-off notifications awkward. The goal is to add two new capabilities:

1. `--template <string>` — supply an inline template string on the CLI that overrides the YAML `notify.comment`.
2. `--data key=value` (repeatable) and `--json-data <json>` — supply extra template variables that are merged into the existing built-in vars (`{assignee}`, `{job.owner}`, `{repo.codeowners}`).

---

## Changes

### 1. `src/OrcAI.Tool/Args.fs` — Add flags to `NotifyArgs`

```fsharp
type NotifyArgs =
    | ...existing...
    | Template  of template: string
    | Data      of kv: string          // repeatable: key=value
    | Json_Data of json: string
```

Usage strings:
- `Template _` → `"Inline comment template, overrides notify.comment in YAML. Supports {key} placeholders."`
- `Data _` → `"Extra template variable as key=value (repeatable). E.g. --data sprint=42."`
- `Json_Data _` → `"Extra template variables as a JSON object string. E.g. --json-data '{\"sprint\":\"42\"}'. Merged with --data; --data takes precedence on key conflicts."`

### 2. `src/OrcAI.Core/NotifyCommand.fs` — Extend `NotifyInput`

```fsharp
type NotifyInput =
    { YamlPath : string
      DryRun   : bool
      Verbose  : bool
      Target   : string
      State    : string
      Template : string option          // new — CLI override for notify.comment
      ExtraVars: Map<string, string>    // new — merged template variables
    }
```

In `execute`, update the template resolution so CLI `--template` has highest priority:

```fsharp
let effectiveTemplate =
    input.Template
    |> Option.orElse notifyComment   // YAML/global config fallback
```

Pass `input.ExtraVars` through to the comment posting step (see step 3).

### 3. `src/OrcAI.Core/Comments.fs` — Accept extra vars in `postTemplatedComment`

Add an `extraVars: Map<string, string>` parameter. User-supplied vars override built-in vars on key conflicts — if a caller explicitly passes `--data assignee=foo`, that is intentional:

```fsharp
let postTemplatedComment
        (client    : IGhClient)
        (repo      : RepoName)
        (issue     : IssueNumber)
        (assignTo  : string)
        (jobOwner  : string option)
        (template  : string)
        (verbose   : bool)
        (label     : string)
        (extraVars : Map<string, string>)   // new
        : Async<unit> =
    async {
        ...
        let builtIn  = buildCommentVars assignTo jobOwner repoOwners
        let vars     = Map.fold (fun acc k v -> Map.add k v acc) builtIn extraVars  // extraVars wins
        let body     = renderTemplate vars template
        ...
    }
```

All three callers (`NotifyCommand`, `NudgeCommand`, `RunCommand`) must pass `Map.empty` as `extraVars` to maintain existing behaviour.

### 4. `src/OrcAI.Tool/Program.fs` — Parse and populate new fields

In the `Notify args` branch:

```fsharp
let template = args.TryGetResult(NotifyArgs.Template)

// Parse --data key=value pairs
let dataKvs =
    args.GetResults(NotifyArgs.Data)
    |> List.choose (fun s ->
        match s.IndexOf('=') with
        | -1  -> None
        | idx -> Some (s.[..idx-1], s.[idx+1..]))
    |> Map.ofList

// Parse --json-data JSON string
let jsonKvs =
    args.TryGetResult(NotifyArgs.Json_Data)
    |> Option.map (fun json ->
        System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(json)
        |> Seq.map (fun kv -> kv.Key, kv.Value)
        |> Map.ofSeq)
    |> Option.defaultValue Map.empty

// Merge: --data wins over --json-data on key conflicts
let extraVars = Map.fold (fun acc k v -> Map.add k v acc) jsonKvs dataKvs

let input : OrcAI.Core.NotifyCommand.NotifyInput =
    { ...existing fields...
      Template  = template
      ExtraVars = extraVars }
```

---

## Files to modify

| File | Change |
|---|---|
| `src/OrcAI.Tool/Args.fs` | Add `Template`, `Data`, `Json_Data` to `NotifyArgs` |
| `src/OrcAI.Core/NotifyCommand.fs` | Add `Template`/`ExtraVars` to `NotifyInput`; update template resolution in `execute` |
| `src/OrcAI.Core/Comments.fs` | Add `extraVars` param to `postTemplatedComment`; merge with built-in vars |
| `src/OrcAI.Tool/Program.fs` | Parse new flags; populate new `NotifyInput` fields |
| `src/OrcAI.Core/NudgeCommand.fs` | Pass `Map.empty` as `extraVars` to updated `postTemplatedComment` |
| `src/OrcAI.Core/RunCommand.fs` | Pass `Map.empty` as `extraVars` to updated `postTemplatedComment` |
| `tests/OrcAI.Core.Tests/NotifyCommandTests.fs` | Add new tests (see below) |

---

## New tests in `NotifyCommandTests.fs`

1. **`notify uses CLI template over YAML template`** — Set `input.Template = Some "CLI {assignee}"` with YAML `notify.comment = "YAML"`. Assert posted body starts with `"CLI"`.
2. **`notify extra vars are substituted in template`** — Set `input.ExtraVars = Map ["sprint", "42"]` and `input.Template = Some "Sprint {sprint}"`. Assert posted body = `"Sprint 42"`.
3. **`notify extra vars override built-in vars on conflict`** — Set `input.ExtraVars = Map ["assignee", "custom-handle"]` and template `"{assignee}"`. Assert posted body = `"custom-handle"` (user-supplied value wins).

---

## Verification

```bash
# Build
dotnet build src/OrcAI.Tool/OrcAI.Tool.fsproj

# Tests
dotnet test tests/OrcAI.Core.Tests/OrcAI.Core.Tests.fsproj

# Smoke test — inline template with --data
orcai notify job.yml --dry-run --template "Hey {assignee}, sprint {sprint} is starting!" --data sprint=42

# Smoke test — --json-data variant
orcai notify job.yml --dry-run --template "Hey {assignee}!" --json-data '{"sprint":"42"}'
```
