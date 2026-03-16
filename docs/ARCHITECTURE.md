# OrcAI CLI — Architecture

## Phases

### Phase 1 — MVP (Nushell scripts)

The initial implementation consists of two standalone Nushell scripts:

- `orca.nu` — creates the GitHub Project, issues, and Copilot assignments from a YAML config file.
- `cleanup.nu` — tears down the project, issues, and related PRs.

These scripts shell out to the `gh` CLI for all GitHub API interactions and rely on `gh auth login` (local) or a `GH_TOKEN` environment variable (CI) for authentication.

---

### Phase 2 — Full CLI (F#)

The full implementation replaces the Nushell scripts with a compiled F# CLI. The Nushell scripts serve as a reference for the expected behaviour of each command.

---

## Tech Stack

| Concern | Choice |
|---|---|
| Language | F# |
| Distribution | Native AOT binary (preferred; .NET self-contained single-file as fallback) |
| CLI argument parsing | [Argu](https://github.com/fsprojects/Argu) |
| GitHub API | `gh` CLI (invoked via [simple-exec](https://github.com/adamralph/simple-exec)) |
| GitHub App auth | F# CLI generates the JWT and exchanges it for an installation token, then sets `GH_TOKEN` for `gh` subprocess calls |
| Testing | xUnit v3 |
| YAML parsing | YamlDotNet (or similar) |
| JSON serialisation | System.Text.Json |

---

## GitHub API Strategy

All GitHub API calls are delegated to the `gh` CLI as a subprocess, invoked via the [simple-exec](https://github.com/adamralph/simple-exec) library. The F# CLI does not call the GitHub REST or GraphQL APIs directly.

This keeps the implementation simple and benefits from `gh`'s existing support for projects, issues, and PRs without reimplementing API clients.

### Authentication flow

```
Local (PAT)
  User runs: orcai auth --pat <token>
  OrcAI stores token securely.
  On command execution: sets GH_TOKEN=<token> in gh subprocess environment.

CI (GitHub App)
  User configures: app ID + private key path (or key content via env var).
  On command execution:
    1. F# CLI generates a JWT from the app ID and private key.
    2. Exchanges JWT for an installation token via GitHub API.
    3. Sets GH_TOKEN=<installation_token> in gh subprocess environment.
  gh CLI then operates as normal using the injected token.
```

`gh` CLI does not natively support GitHub App authentication. The installation token exchange must be handled by the F# CLI before any `gh` subprocess call.

---

## Project Structure (proposed)

```
cli/
  src/
    OrcAI.Tool/          -- Entry point, Argu argument definitions, command dispatch
    OrcAI.Core/          -- Domain types, command logic, lock file, YAML parsing
    OrcAI.GitHub/        -- gh CLI wrapper using simple-exec
    OrcAI.Auth/          -- PAT storage, GitHub App JWT generation, token exchange
  tests/
    OrcAI.Core.Tests/    -- xUnit v3 unit tests for domain logic
    OrcAI.GitHub.Tests/  -- xUnit v3 integration tests for gh subprocess wrapper
  docs/
  example/
  orca.nu              -- Phase 1 reference script
  cleanup.nu           -- Phase 1 reference script
```

---

## Lock File

The lock file is a JSON file written alongside the YAML config file (e.g. `job.lock.json` for `job.yml`). It is managed by `OrcAI.Core` and contains:

```jsonc
{
  "lockedAt": "2026-03-02T10:00:00Z",
  "yamlHash": "<sha256 of original yaml content>",
  "project": { "org": "...", "number": 42, "title": "..." },
  "repos": ["repo1", "repo2"],
  "issues": [
    { "repo": "repo1", "number": 7, "url": "...", "assignees": ["copilot"] }
  ],
  "pullRequests": [
    { "repo": "repo1", "number": 3, "url": "...", "closesIssue": 7 }
  ]
}
```

---

## Command Dispatch (Argu)

```
orcai run    <yaml_file> [--verbose]
orcai cleanup <yaml_file> [--dryrun]
orcai info   <yaml_file> [--no-lock] [--save-lock]
orcai auth   pat --token <token>
orcai auth   app --app-id <id> --key <path>
```

Each top-level subcommand maps to a module in `OrcAI.Core` with a pure function that takes typed input and returns a result type. The `OrcAI.Tool` entry point handles argument parsing, wires in the `gh` subprocess wrapper and auth context, and prints output.

---

## Testing Strategy

- **Unit tests** (`OrcAI.Core.Tests`): test domain logic (YAML parsing, lock file read/write, idempotency checks, hash computation) without any I/O or subprocess calls. Dependencies are injected as interfaces or function parameters.
- **Integration tests** (`OrcAI.GitHub.Tests`): test the `gh` wrapper (via simple-exec) against a real or stubbed `gh` binary. These are opt-in and require a configured `GH_TOKEN`.

---

## CI/CD (GitHub Actions)

Two workflows live under `.github/workflows/`:

### `build.yml` — Build & Test

- Triggers on every push and pull request to any branch.
- Runs on `ubuntu-latest` using .NET 10.
- Steps: clear NuGet cache → restore (locked-mode) → build (Release) → test.

### `publish.yml` — Release

- Triggers on `v*` tag pushes (e.g. `v1.0.0`) and `workflow_dispatch`.
- Runs on a matrix of `ubuntu-latest`, `windows-latest`, and `macos-latest`.
- Builds a self-contained, single-file binary named `orcai` (or `orcai.exe` on Windows) for each platform using `dotnet publish`.
- Uploads the binaries as assets to a GitHub Release created for the tag.
- Requires no secrets beyond the default `GITHUB_TOKEN`.
