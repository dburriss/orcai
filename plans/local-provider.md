# Plan: Local file-backed provider (`provider: local`)

> **Prerequisites, in order:**
> 1. `plans/split-provider-interface.md` — splits `IGhClient` into a
>    mandatory `IIssueTracker` plus optional `IPullRequestLinker`/
>    `IRepoInspector`. `LocalClient` below only implements `IIssueTracker`.
> 2. `plans/generalize-tracker-ids.md` — makes issue/project identity an
>    opaque `IssueId`/`ProjectId` string instead of a GitHub-shaped int. This
>    plan's identity scheme (below) depends on that: a sequential int counter
>    is exactly the design this plan originally had, and it doesn't hold up
>    for a file store that's meant to be shared/synced across machines (see
>    "Identity scheme").

## Goal

Add a second `IIssueTracker` implementation that persists project/issue
state to a YAML project file + Markdown issue files on disk instead of
calling the GitHub API. Scoped deliberately small: it keeps OrcAI's existing
"one job = one issue, fanned out across N repo-equivalents" semantics
unchanged, and just swaps the backend that tracks state.

Not in scope: a "one project, many distinct issues" model, or a Jira
provider. Those are bigger, separate changes (see Out of scope).

---

## Design

### YAML schema addition

New optional top-level `provider:` block. Omitting it preserves all current
behaviour (`type: github`) — fully additive, no breaking change.

```yaml
provider:
  type: local              # "github" (default) | "local"
  root: "./.orcai-local"   # optional; default "<yaml-dir>/.orcai-local"
```

`root` is resolved relative to the YAML file's directory, same convention as
`issue.template`.

### Storage layout

Under `provider.root`:

```
.orcai-local/
  projects/
    <org>/
      <slug>.yaml                   # ProjectInfo, filename = deterministic slug
  repos/
    <org>/<repo>/
      issues/
        <ulid>.md                   # one issue file per issue, frontmatter + body
      labels.yaml                   # list of created label names
```

`<org>` and `<repo>` come straight from `RepoName`/`OrgName` (`owner/repo`
strings) — no domain type changes needed there; for a local job these are
just namespacing strings, not real GitHub identifiers.

### Identity scheme (why not sequential numbers)

Local files are the one provider where "add an issue" can plausibly happen
from two places without coordination — two clones of the same
`.orcai-local` directory (e.g. checked into git and pulled by two
contributors), or two `orcai run` invocations against the same root. A
sequential counter (`max(existing numbers) + 1`) is unsafe there: two
concurrent creators compute the same next number, write two different issues
to the same filename, and a git merge either silently picks one or conflicts
on a file that's semantically two different issues. This is a well-known
failure mode for any git-synced sequential-ID scheme.

Now that `IssueId`/`ProjectId` are opaque strings (`generalize-tracker-ids.md`),
neither identity needs to be a small increasing integer:

- **Issue id**: a ULID generated at creation time (time-sortable, 128 bits of
  randomness beyond the timestamp — collision probability is negligible
  without coordination). Filename is `issues/<ulid>.md`. No shared counter,
  no read-then-increment race.
- **Project id**: a deterministic slug of `org + title`
  (`slugify("dburriss/Add AGENTS.md")` → `dburriss-add-agents-md`). Two
  concurrent "find or create project X" calls converge on the same filename
  by construction — idempotent by design, not by locking. `FindProject`
  checks the file exists before `CreateProject` writes it; the residual race
  (both processes miss the existence check and both write) just overwrites
  with equivalent content, which is harmless because the content is
  derived from the same inputs.

No numeric "issue number" is exposed anywhere. Templates and CLI output that
today show `#42` for GitHub show the raw id for local jobs (e.g. a ULID) —
less pretty, but correct under concurrent/distributed use, which matters more
for a file store than a cosmetic counter. `{{issue_number}}` in `cmd`/
`cmd-to-github` action templates keeps its name (job YAML compatibility) but
carries whatever `IssueId` string the active provider produced.

**Project file** (`projects/<org>/<slug>.yaml`):

```yaml
id: "dburriss-add-agents-md"
title: "Add AGENTS.md"
org: "dburriss"
issues: ["wye#01JB3X...", "fennel#01JB3Y..."]   # repo#issue-id references
createdAt: 2026-08-02T10:00:00Z
```

`url` in the returned `ProjectInfo` is a synthetic `local://<org>/<slug>`
string.

**Issue file** (`repos/<org>/<repo>/issues/<ulid>.md`):

```markdown
---
id: "01JB3X9K2QZ8N7F5T6R1M4P0WC"
title: "Add AGENTS.md"
state: open
labels: [documentation, automated]
assignees: [copilot]
createdAt: 2026-08-02T10:00:00Z
updatedAt: 2026-08-02T10:00:00Z
---
<issue body markdown>

### Comment 2026-08-02T10:05:00Z
<comment body>
```

`url` in the returned `IssueRef` is `local://<org>/<repo>/issues/<id>`.
Comments are appended as `### Comment <timestamp>` sections — simple,
diffable, no separate comment store.

### `IIssueTracker` method → local behaviour

| Method | Local behaviour |
|---|---|
| `FindProject` / `CreateProject` | Read/write `projects/<org>/<slug>.yaml`, slug derived from title (see Identity scheme) |
| `DeleteProject` | Delete the project file |
| `ListLabels` / `CreateLabel` | Read/append `repos/<org>/<repo>/labels.yaml` |
| `FindIssue` / `FindClosedIssue` | Scan `issues/*.md` frontmatter for matching title + state |
| `CreateIssue` | Generate a ULID, write new issue file |
| `UpdateIssue` | Rewrite title/body, bump `updatedAt` |
| `DeleteIssue` | Delete the issue file |
| `ReopenIssue` | Flip `state: closed` → `open` |
| `AddIssueToProject` | Append `repo#id` to the project's `issues` list (idempotent) |
| `AssignIssue` / `UnassignIssue` | Add/remove from `assignees` frontmatter list |
| `PostComment` | Append a `### Comment` section |
| `GetIssueState` | Read `state` from frontmatter |

That's the full interface — 16 methods, all with genuine local meaning.
There's no PR-linking or repo-inspection table anymore: `LocalClient` doesn't
implement `IPullRequestLinker`/`IRepoInspector` at all, so `ProviderClients`
for a local job is `{ Tracker = LocalClient(...); Prs = None; Repos = None }`.
Command modules already handle `None` for those (see
`split-provider-interface.md`) as "no PRs" / "no bulk repo state available",
which is exactly correct for local jobs rather than something being faked.

### Wiring a provider per job

`OrcAIDeps.ResolveProvider : JobConfig -> Result<ProviderClients, string>`
(from `split-provider-interface.md`) gets its second real branch:

```fsharp
// Program.fs
let resolveProvider (config: JobConfig) : Result<ProviderClients, string> =
    match config.Provider with
    | Domain.GitHub -> ghProviderLazy.Value   // token resolved lazily, only here
    | Domain.Local  ->
        Ok { Tracker = LocalClient(fileSystem, config.ProviderRoot) :> IIssueTracker
             Prs     = None
             Repos   = None }
```

Because GitHub auth is only resolved inside `ghProviderLazy` on first actual
use, a local-only job never touches `resolveAuthContext()` — no separate
"peek every YAML file before resolving auth" step is needed. Command modules
already call `deps.ResolveProvider config` once per job (per
`split-provider-interface.md`); this plan adds no new call sites there,
just the `Local` match arm.

### `AssignCopilot` on a local job

`assign-copilot` just becomes `tracker.AssignIssue repo issue "copilot"`,
same as any other assignee — the PAT/GitHub-App branching lives entirely
inside `GhCliClient` after `split-provider-interface.md`'s Copilot fold-in,
so `RunCommand` never needs to know or care that the current provider is
local when handling this action type.

---

## Implementation steps

### 1. Domain (`Domain.fs`)
- Add `type Provider = GitHub | Local`.
- Add `Provider : Provider` field to `JobConfig` (default `GitHub` when the
  YAML omits `provider:`).
- Add `ProviderRoot : string option` to `JobConfig` (resolved absolute path;
  `None` when provider is `GitHub`).

### 2. YAML parsing (`YamlConfig.fs`)
- Add `YamlProvider = { ``type``: string; root: string }` DTO and `provider:
  YamlProvider` field on `YamlRoot`.
- Parse `type` → `Provider` (`null`/`""`/`"github"` → `GitHub`, `"local"` →
  `Local`, anything else → validation error).
- Resolve `root` relative to the YAML's directory, default
  `<yamlDir>/.orcai-local`.

### 3. New project `OrcAI.Local`
- Mirrors `OrcAI.GitHub`'s shape: one file, `LocalClient.fs`, implementing
  `IIssueTracker` per the table above, using `IFileSystem` (not raw
  `System.IO`) so it's testable the same way `GhCliClient` is faked in tests.
- ULID generation: no existing dependency provides this — add a small ULID
  helper (timestamp + crypto-random suffix, Crockford base32) or take a
  minimal NuGet dependency (e.g. `Ulid`) rather than hand-rolling encoding.
- YAML frontmatter parsing/writing for issue files via YamlDotNet (already a
  dependency); simple split on the `---` delimiters.
- Add project reference + solution wiring (`OrcAI.sln`, `OrcAI.Tool.fsproj`
  references it alongside `OrcAI.GitHub`).

### 4. `Program.fs`
- Add the `Local` match arm to `resolveProvider` (shown above).

### 5. `GenerateCommand.fs`
- Add a `--provider local` flag to `orcai generate` that emits the
  `provider:` block in the scaffolded YAML instead of the commented-out
  `action:` placeholder convention already used there.

### 6. Tests
- New `tests/OrcAI.Local.Tests/` (or add to `OrcAI.Core.Tests`) covering
  `LocalClient` against a fake `IFileSystem` (`Testably.Abstractions`,
  already used elsewhere): create/find/reopen/delete issue, create/find
  project (including the "two concurrent creates converge on one file"
  idempotency case), label create/list, comment append.
- `RunCommand`/`CleanupCommand`/`InfoCommand` tests: add a local-provider
  variant of existing fixture YAMLs and assert the same outcomes
  (Created/AlreadyExisted/etc.) using a `ProviderClients` built from
  `LocalClient` instead of the GitHub fake.
- `YamlConfig.parse` tests: `provider:` omitted → `GitHub`; `type: local` →
  `Local` with default/explicit `root`; unknown `type` → error.

### 7. Docs / examples
- `ARCHITECTURE.md`: add `OrcAI.Local` to the project table and document the
  `provider:` field next to auth resolution.
- Add one `example/*.yml` demonstrating `provider: local`.

---

## Known limitations (acceptable for this pass)

- No PR tracking locally — `nudge`'s "closed-PR" handling and `info`'s PR
  summary will always show zero PRs for local jobs, because `Prs = None`.
  This matches the "same fan-out model, local backend" scope decision: the
  local provider only replaces issue/project tracking, not the git/PR
  write-back actions (`cmd-to-github`, `cmd-checkout` still assume a real,
  clonable repo regardless of provider).
- No repo-inspection locally (`Repos = None`) — `depends_on` gating that
  needs bulk repo state, and any archived-repo pre-check, is simply
  unavailable for local jobs (per `split-provider-interface.md`'s handling
  of `Repos = None`, not a local-specific hack).
- File-level writes (issue create, project create) are not transactional.
  The identity scheme above removes the *ID-collision* race; it does not add
  file locking. Two truly simultaneous writes to the *same* issue file
  (e.g. both processes updating the same existing issue's body at once) can
  still race at the OS file-write level — acceptable for now since that's a
  much narrower window than the create-time counter race this plan actually
  set out to fix.

## Out of scope (future)

- "One project, many distinct issues" model (would require reshaping
  `JobConfig` away from `Repos : RepoName list` + single `IssueTitle`/`Body`).
- A real Jira provider (`generalize-tracker-ids.md` preps identity shape only).
- File locking / transactional writes for the local store.
