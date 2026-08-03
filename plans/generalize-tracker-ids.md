# Plan: Opaque issue/project identity (prep for Jira, fixes local ID scheme)

> **Prerequisite:** run `plans/split-provider-interface.md` first — this plan
> retargets `IIssueTracker`'s signatures, so that interface needs to exist as
> its own thing first. Run this plan **before** `plans/local-provider.md`,
> which currently designs the local provider around a sequential-int
> `IssueNumber`/`ProjectInfo.Number` that this plan removes.

## Goal

`IssueNumber = IssueNumber of int` and `ProjectInfo.Number : int` are GitHub
Projects V2/Issues-shaped: a small integer, unique per repo (issues) or per
org (projects). That shape doesn't fit:

- **Jira**, whose issue identity is a key like `PROJ-123` (a string, unique
  per Jira project, not per "repo"), and whose project identity is a project
  *key* (`PROJ`), not a number.
- **The local file provider**, where a locally-incremented integer requires a
  shared counter — safe for one writer, but exactly the kind of thing that
  produces collisions the moment two clones/processes create an issue
  concurrently without coordination (see `plans/local-provider.md` update).

This plan makes `IIssueTracker`'s identity types opaque strings so each
provider picks its own scheme, without changing the YAML job schema or the
"one issue fanned out across repo-equivalents" model already decided.

This plan does **not** implement a Jira provider — no Jira client, no auth,
no API calls. It only removes the GitHub-shaped assumption from the shared
interface so a Jira provider becomes *possible* later without another domain
rewrite.

---

## Domain changes (`Domain.fs`)

```fsharp
/// Opaque, provider-assigned issue identifier.
/// GitHub: the issue number as a string ("42"). Jira: the issue key ("PROJ-123").
/// Local: a ULID (see local-provider.md).
type IssueId = IssueId of string

/// Opaque, provider-assigned project identifier.
/// GitHub: the project number as a string ("7"). Jira: the project key ("PROJ").
/// Local: a slug derived from the title.
type ProjectId = ProjectId of string

type ProjectInfo =
    { Org   : OrgName
      Id    : ProjectId      // was: Number : int
      Title : string
      Url   : string }

type IssueRef =
    { Repo      : RepoName
      Id        : IssueId    // was: Number : IssueNumber
      Url       : string
      Assignees : string list }
```

`IssueNumber` is deleted (folded into `IssueId`). `RepoFailure`/`LockFile`
reference `IssueRef`/`ProjectInfo` structurally, so they pick up the new
shape automatically — no field renames needed there beyond what the compiler
forces.

`PrNumber`/`PullRequestRef` are **not** changed — pull-request tracking is
already an optional, GitHub-only capability (`IPullRequestLinker`, from
`split-provider-interface.md`), so there's no cross-provider pressure on its
shape. Real GitHub PR numbers stay plain ints.

---

## `IIssueTracker` signature updates

Every method taking `issue:IssueNumber` takes `issue:IssueId` instead;
`FindProject`/`CreateProject` still take `org:OrgName -> title:string` (find
target unchanged) but return `ProjectId`-shaped `ProjectInfo`. No method
count or shape otherwise changes — this is a type substitution, not a new
capability.

---

## `GhCliClient` — mapping GitHub's ints to `IssueId`/`ProjectId`

GitHub's ints become the opaque wrapper at the boundary; the numeric
round-trip needed for `gh` CLI subprocess args happens inside
`OrcAI.GitHub` only:

```fsharp
let private toIssueId (n: int) = IssueId (string n)
let private ghIssueArg (IssueId s) =
    match Int32.TryParse s with
    | true, n -> n
    | false, _ -> failwith $"GitHub issue id '{s}' is not numeric — corrupt state or wrong provider for this job."
```

Every `gh issue <verb> {issueN} --repo ...` call site in
`OrcAI.GitHub/GhClient.fs` unwraps via `ghIssueArg` instead of pattern
matching `IssueNumber issueN` directly. Same pattern for `ProjectId` /
`gh project <verb> {projectN}`.

---

## Call-site updates (mechanical, no behaviour change for GitHub jobs)

- **`RunCommand.fs`**: template var `"issue_number", string issueNum` →
  unwrap `IssueId` directly (already a string); default commit
  message/PR-title interpolation (`$"[{issueNum}] {config.ProjectTitle}"`)
  works unchanged since both are string-interpolated either way.
- **`CleanupCommand.fs`**, **`InfoCommand.fs`**, **`NudgeCommand.fs`**,
  **`NotifyCommand.fs`**: display formatting (`issueN`/`prN` in `eprintfn`)
  unwraps `IssueId`/`ProjectId` as strings instead of `%d`-formatting an int.
- **`Program.fs`** CLI/`--json` output: same — print the raw id string
  instead of an int. For GitHub jobs this still renders as `#42`; for a
  future Jira job it would render `PROJ-123`; for local jobs it renders
  whatever id scheme `local-provider.md` picks.
- **`tests/OrcAI.Core.Tests/FakeGhClient.fs`**: store `IssueId`/`ProjectId`
  instead of raw ints; existing tests that assert on issue numbers switch to
  asserting on the id string (e.g. `IssueId "1"`).

---

## Breaking change: lock file schema

`LockFile.Issues : IssueRef list` and `.Project : ProjectInfo` serialize
`Number : int` today; after this change they serialize `Id : string`.
Existing `.lock.json` files will fail to deserialize. Per the project's
existing convention for breaking changes (see `plans/action-field.md`), this
is a hard break, not a migration:

- Bump a lock file format marker (or just let JSON deserialization fail) and
  surface a clear error: `"Lock file was written by an older OrcAI version
  with incompatible issue/project ids. Delete <path> and re-run."`
- No migration code — deleting and re-running `orcai run` regenerates
  equivalent state (issues are found by title, not by id, on a fresh run).

---

## Known limitation this plan does *not* fix

`RepoName` (`owner/repo`) stays as the fan-out unit — that's the "same
fan-out model" decision already made for the local provider. A real Jira
provider still has no native "repo" concept: it would have to interpret each
`RepoName` entry as something Jira-shaped (a component, a label, or just
reusing one project for every entry), which is a real awkwardness, not
solved here. Opaque ids fix *identity*; they don't give Jira a natural
fan-out axis. Flagging this so a future "add a real Jira provider" plan
doesn't assume this one already solved it.

---

## Order of operations

1. `plans/split-provider-interface.md`
2. **This plan**
3. `plans/local-provider.md`, updated to assign `IssueId`/`ProjectId` via a
   collision-safe local scheme instead of incrementing counters (see that
   plan's updated "Identity scheme" section).

## Out of scope

- An actual Jira `IIssueTracker` implementation.
- Changing `RepoName`/`OrgName` or the fan-out model.
- Changing `IPullRequestLinker`'s GitHub-only int-based PR numbers.
