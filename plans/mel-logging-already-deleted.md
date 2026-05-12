# Plan: Use MEL logging for "already deleted" resource warnings

## Context

When `orcai cleanup` encounters a project, issue, or PR that no longer exists, it should treat
it as success (idempotent) and emit a warning rather than failing hard. The warning should use
`Microsoft.Extensions.Logging` (`ILogger`) instead of `eprintfn` so that the output level is
configurable — users can suppress or surface these messages by setting `ORCAI_LOG_LEVEL`.

Currently `DeleteIssue` silently returns `Ok ()` for not-found errors (no log); `DeleteProject`
and `ClosePr` have no guard at all.

---

## Verified error strings (from `gh` CLI)

| Function | Contains string |
|---|---|
| `DeleteProject` | `"Could not resolve to a ProjectV2"` |
| `DeleteIssue` | `"Could not resolve to an issue or pull request"` |
| `ClosePr` | `"Could not resolve to a PullRequest"` |

---

## Changes

### 1. `src/OrcAI.GitHub/OrcAI.GitHub.fsproj`

Add MEL abstractions (provides `ILogger`, no console provider needed here):

```xml
<PackageReference Include="Microsoft.Extensions.Logging.Abstractions" Version="10.0.0" />
```

### 2. `src/OrcAI.Tool/OrcAI.Tool.fsproj`

Add MEL + Console provider:

```xml
<PackageReference Include="Microsoft.Extensions.Logging" Version="10.0.0" />
<PackageReference Include="Microsoft.Extensions.Logging.Console" Version="10.0.0" />
```

### 3. `src/OrcAI.GitHub/GhClient.fs`

- Add `open Microsoft.Extensions.Logging` at the top
- Add `ILogger` parameter to `GhCliClient` constructor:

```fsharp
type GhCliClient(ghToken: string, writesPerMinute: int, rateLimitRetries: int, logger: ILogger) =
```

- `DeleteProject` — add not-found guard with warning:

```fsharp
| Error e ->
    if e.Contains("Could not resolve to a ProjectV2") then
        logger.LogWarning("Project #{ProjectNumber} in org '{Org}' not found — already deleted.", project.Number, orgStr)
        return Ok ()
    else
        return Error e
```

- `DeleteIssue` — replace silent `Ok ()` with a warning log:

```fsharp
if e.Contains("Could not resolve to an issue or pull request") then
    logger.LogWarning("Issue #{IssueNumber} in repo '{Repo}' not found — already deleted.", issueN, repoStr)
    return Ok ()
```

- `ClosePr` — add not-found guard with warning:

```fsharp
| Error e ->
    if e.Contains("Could not resolve to a PullRequest") then
        logger.LogWarning("PR #{PrNumber} in repo '{Repo}' not found — already closed/deleted.", prN, repoStr)
        return Ok ()
    else
        return Error e
```

### 4. `src/OrcAI.Tool/Program.fs`

In `withClient`, create a `LoggerFactory` and pass an `ILogger` to `GhCliClient`.
Read log level from the `ORCAI_LOG_LEVEL` env var (defaults to `Warning`):

```fsharp
open Microsoft.Extensions.Logging

let private resolveLogLevel () =
    match System.Environment.GetEnvironmentVariable("ORCAI_LOG_LEVEL") with
    | null | "" -> LogLevel.Warning
    | s ->
        match System.Enum.TryParse<LogLevel>(s, ignoreCase = true) with
        | true, level -> level
        | _           -> LogLevel.Warning

// Inside withClient, before constructing GhCliClient:
let logLevel    = resolveLogLevel ()
let logFactory  = LoggerFactory.Create(fun b -> b.AddConsole().SetMinimumLevel(logLevel) |> ignore)
let ghLogger    = logFactory.CreateLogger("OrcAI.GitHub.GhCliClient")
let client      = OrcAI.GitHub.GhClient.GhCliClient(ghToken, writesPerMinute, rateLimitRetries, ghLogger)
// (same for copilotClient)
```

---

## Critical files

| File | Change |
|---|---|
| `src/OrcAI.GitHub/OrcAI.GitHub.fsproj` | Add `Microsoft.Extensions.Logging.Abstractions` |
| `src/OrcAI.Tool/OrcAI.Tool.fsproj` | Add `Microsoft.Extensions.Logging` + `.Console` |
| `src/OrcAI.GitHub/GhClient.fs` | `ILogger` ctor param; guards + `LogWarning` in `DeleteProject`, `DeleteIssue`, `ClosePr` |
| `src/OrcAI.Tool/Program.fs` | Create `ILoggerFactory`, pass logger to `GhCliClient` |

No changes to `CleanupCommand.fs`, `Deps.fs`, or `Args.fs`.

---

## Configuration

Users can set `ORCAI_LOG_LEVEL` to any `Microsoft.Extensions.Logging.LogLevel` name
(`Trace`, `Debug`, `Information`, `Warning`, `Error`, `Critical`, `None`).
Default is `Warning` — warnings show by default, below-warning messages are suppressed.

---

## Verification

1. `dotnet build` — no errors
2. Run cleanup against a YAML whose lock file references a project/issue/PR that no longer exists
   → exits 0, warning lines appear on stdout (MEL console format)
3. `ORCAI_LOG_LEVEL=Error orcai cleanup ...` same scenario → exits 0, no warning output
4. Run cleanup against resources that do exist → unchanged behaviour, exits 0
5. Genuine API error (bad token, permission denied) → still fails with the original error message
