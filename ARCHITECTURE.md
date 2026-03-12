# Architecture

## Overview

**OrcAI** (`orcai`) is a CLI tool for automating bulk GitHub repository upgrades. It reads a YAML job config and orchestrates GitHub Projects, issues, and Copilot assignments across multiple repositories. It does **not** mutate repository code — it only manages GitHub planning metadata.

## Tech Stack

| Concern | Choice |
|---|---|
| Language | F# / .NET 10 |
| CLI parsing | [Argu](https://github.com/fsprojects/Argu) |
| GitHub API | `gh` CLI subprocess via [SimpleExec](https://github.com/adamralph/simple-exec) |
| YAML parsing | [YamlDotNet](https://github.com/aaubry/YamlDotNet) |
| Console output | [Spectre.Console](https://spectreconsole.net/) |
| Testing | xUnit v3 |
| Task runner | `mise` |

## Projects

```
OrcAI.Tool       Entry point — argument parsing, output, dispatch
  ├── OrcAI.Core     Domain types, interfaces (IGhClient, IAuthContext),
  │                  command modules, YAML/lock file I/O
  ├── OrcAI.GitHub   IGhClient implementation (shells out to gh CLI)
  └── OrcAI.Auth     IAuthContext implementations (PAT, GitHub App)
```

`OrcAI.Core` has no upstream project dependencies and is the dependency root.

## Key Patterns

**Dependency injection via record** — `OrcAIDeps { GhClient; AuthContext; FileSystem }` is passed to every command function, making them independently testable without a container.

**Interface over subprocess** — All GitHub API calls go through `IGhClient`. Production shells out to `gh`; tests substitute a fake. Auth is delivered by setting `GH_TOKEN` in the subprocess environment.

**Lock file for idempotency** — After a successful `run`, a `<basename>.lock.json` is written alongside the YAML. Subsequent runs short-circuit all network calls if the YAML hash is unchanged.

**File system abstraction** — `IFileSystem` (Testably.Abstractions) is injected so file I/O is testable without real disk access.

## Auth Resolution (runtime priority)

1. `ORCAI_PAT` env var or stored PAT profile
2. `ORCAI_APP_*` env vars or stored GitHub App profile
3. `GH_TOKEN` env var
4. Ambient `gh auth token`

## CLI Commands

| Command | Description |
|---|---|
| `run <yaml>` | Create Project, issues, assign Copilot |
| `cleanup <yaml>` | Close PRs/issues, delete project, remove lock |
| `info <yaml>` | Show job state (lock file or live) |
| `generate` | Scaffold a YAML job config |
| `auth pat/app/create-app/switch` | Manage auth profiles |

All commands support `--json` for machine-readable output.

## Distribution

- **Single-file binary** — self-contained, trimmed (`linux-x64`, `win-x64`, `osx-x64`)
- **NuGet dotnet tool** — `dotnet tool install OrcAI.Tool`
