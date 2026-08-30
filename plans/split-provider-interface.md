# Plan: Split `IGhClient` into tracker / PR / repo-inspector interfaces (run before `local-provider.md`)

## Goal

Clean up the current abstraction before adding a second provider:

1. Split the monolithic `IGhClient` into a small mandatory interface every
   provider implements, plus two optional capability interfaces that only
   GitHub-like providers support.
2. Remove the `CopilotClient : IGhClient option` field from `OrcAIDeps` —
   fold the GitHub-App-can't-assign-copilot workaround into `GhCliClient`
   itself, where it belongs.
3. Replace `OrcAIDeps.GhClient` with a single per-job resolver instead of a
   bag of same-shaped fields.
4. Delete `RepoExists` — dead code (see audit below).

This is pure refactor, no behavior change. It's a prerequisite for
`local-provider.md`: once it lands, `LocalClient` only needs to implement the
mandatory tracker interface (16 methods with real local meaning) instead of
faking no-op behaviour for PR-linking and repo-inspection methods that don't
apply to a local backend.

---

## Audit: current `IGhClient` methods → callers

| Method | Called from | Classification |
|---|---|---|
| `FindProject` | RunCommand, CleanupCommand, InfoCommand | Tracker |
| `CreateProject` | RunCommand | Tracker |
| `DeleteProject` | CleanupCommand | Tracker |
| `ListLabels` | RunCommand | Tracker |
| `CreateLabel` | RunCommand | Tracker |
| `FindIssue` | RunCommand, CleanupCommand, InfoCommand | Tracker |
| `FindClosedIssue` | RunCommand | Tracker |
| `ReopenIssue` | RunCommand | Tracker |
| `CreateIssue` | RunCommand | Tracker |
| `UpdateIssue` | RunCommand | Tracker |
| `DeleteIssue` | CleanupCommand | Tracker |
| `AddIssueToProject` | RunCommand | Tracker |
| `AssignIssue` | RunCommand, NudgeCommand | Tracker |
| `UnassignIssue` | NudgeCommand | Tracker |
| `PostComment` | Comments | Tracker |
| `GetIssueState` | DependencyResolution, NotifyCommand | Tracker |
| `FindPrsForIssue` | CleanupCommand, DependencyResolution, InfoCommand, NudgeCommand | PR linker |
| `ClosePr` | CleanupCommand | PR linker |
| `GetPrState` | NotifyCommand | PR linker |
| `ListRepos` | GenerateCommand (scaffolding, not the run loop) | Repo inspector |
| `RepoExists` | *(nobody — only implemented, never called)* | **Delete** |
| `ReposExist` | ValidateCommand | Repo inspector |
| `IsArchived` | RunCommand | Repo inspector |
| `FetchReposState` | RunCommand | Repo inspector |
| `FetchCodeowners` | Comments | Repo inspector |

Git plumbing (clone/worktree/commit/push/fork) is **not** on `IGhClient` at
all — `CheckoutManager.fs` shells out to raw `git`/`gh` directly from
`RunCommand.fs` for `cmd-checkout`/`cmd-to-github` actions, with zero interface
behind it today. Out of scope here (see below).

---

## New shape (`OrcAI.Core/Provider.fs`, replaces `GhClient.fs`)

```fsharp
/// Mandatory — every provider (GitHub, Local, future Jira) implements this.
type IIssueTracker =
    abstract FindProject       : org:OrgName -> title:string -> Async<ProjectInfo option>
    abstract CreateProject     : org:OrgName -> title:string -> Async<Result<ProjectInfo, string>>
    abstract DeleteProject     : project:ProjectInfo -> Async<Result<unit, string>>
    abstract ListLabels        : repo:RepoName -> Async<Result<string list, string>>
    abstract CreateLabel       : repo:RepoName -> name:string -> Async<Result<unit, string>>
    abstract FindIssue         : repo:RepoName -> title:string -> Async<Result<IssueRef option, string>>
    abstract FindClosedIssue   : repo:RepoName -> title:string -> Async<Result<IssueRef option, string>>
    abstract ReopenIssue       : repo:RepoName -> issue:IssueNumber -> Async<Result<IssueRef, string>>
    abstract CreateIssue       : repo:RepoName -> title:string -> body:string -> labels:string list -> Async<Result<IssueRef, string>>
    abstract UpdateIssue       : repo:RepoName -> issue:IssueNumber -> title:string -> body:string -> Async<Result<unit, string>>
    abstract DeleteIssue       : repo:RepoName -> issue:IssueNumber -> Async<Result<unit, string>>
    abstract AddIssueToProject : project:ProjectInfo -> issue:IssueRef -> Async<Result<unit, string>>
    abstract AssignIssue       : repo:RepoName -> issue:IssueNumber -> assignee:string -> Async<Result<unit, string>>
    abstract UnassignIssue     : repo:RepoName -> issue:IssueNumber -> assignee:string -> Async<Result<unit, string>>
    abstract PostComment       : repo:RepoName -> issue:IssueNumber -> body:string -> Async<Result<unit, string>>
    abstract GetIssueState     : repo:RepoName -> issue:IssueNumber -> Async<string option>

/// Optional — GitHub-API PR tracking. None for providers with no PR concept.
type IPullRequestLinker =
    abstract FindPrsForIssue : repo:RepoName -> issue:IssueNumber -> Async<PullRequestRef list>
    abstract ClosePr         : repo:RepoName -> pr:PrNumber -> Async<Result<unit, string>>
    abstract GetPrState      : repo:RepoName -> pr:PrNumber -> Async<string option>

/// Optional — GitHub-API repo metadata. None for providers with no repo concept.
type IRepoInspector =
    abstract ListRepos      : org:OrgName -> Async<Result<string list, string>>
    abstract ReposExist     : repos:RepoName list -> Async<Map<RepoName, Result<unit, string>>>
    abstract IsArchived     : repo:RepoName -> Async<Result<bool, string>>
    abstract FetchReposState: repos:RepoName list -> title:string -> Async<Map<RepoName, Result<RepoState, string>>>
    abstract FetchCodeowners: repo:RepoName -> Async<string option>

/// What a resolved provider offers for one job.
type ProviderClients =
    { Tracker : IIssueTracker
      Prs     : IPullRequestLinker option
      Repos   : IRepoInspector option }
```

`GhCliClient` implements all three interfaces (no behavior change to its
methods, just regrouped). A future `LocalClient` implements only
`IIssueTracker`.

---

## Copilot dual-client fold-in

Today: `Deps.fs` carries `CopilotClient : IGhClient option`; `RunCommand.fs`
(~lines 461–467) branches on `isPrimaryAuthApp` and picks
`deps.CopilotClient |> Option.defaultValue client` before calling
`AssignIssue`.

After: `GhCliClient` takes both tokens at construction and picks internally:

```fsharp
type GhCliClient(primaryToken: string, copilotToken: string option, writesPerMinute, rateLimitRetries, logger) =
    ...
    member _.AssignIssueImpl repo issue assignee =
        let token =
            if assignee.TrimStart('@').Equals("copilot", StringComparison.OrdinalIgnoreCase)
            then copilotToken |> Option.defaultValue primaryToken
            else primaryToken
        ...
```

`Program.fs`'s existing PAT-for-copilot resolution (constructing a second
`GhCliClient` today) becomes just resolving a second token string and passing
it into the one client's constructor. `RunCommand.fs` loses the
`isPrimaryAuthApp`/`CopilotClient` branch entirely — it just calls
`tracker.AssignIssue`, same as any other assignee.

---

## `Deps.fs`

```fsharp
type OrcAIDeps =
    { ResolveProvider : JobConfig -> Result<ProviderClients, string>
      AuthContext     : IAuthContext
      FileSystem      : IFileSystem
      Config          : OrcAIConfig }
```

`Program.fs` builds `ResolveProvider` once, closing over a
`Lazy<Result<ProviderClients, string>>` for GitHub (token resolution happens
on first actual use, not at startup — this is what lets a `provider: local`
job skip GitHub auth entirely once `local-provider.md` adds the `Local` case).
For now, with only GitHub existing, `ResolveProvider` always returns the lazy
GitHub bundle regardless of `JobConfig` — the per-job dispatch only starts
doing real work once `local-provider.md` adds a second branch.

---

## Command module changes

Each module replaces its `client : IGhClient` parameter (or `deps.GhClient`
call site) with a resolved `ProviderClients`, then narrows to the field(s) it
needs:

- **RunCommand**: `providerClients.Tracker` for everything; `providerClients.Repos` for the archived pre-check and `FetchReposState` prefetch — already written as `RepoState option`-driven fallback (`prefetchedState : RepoState option`), so `Repos |> Option.map/bind` slots in naturally, no new branching needed.
- **CleanupCommand**: `.Tracker` for delete issue/project; `.Prs` for the PR-closing step — when `None`, skip straight to deleting the issue (today's `cleanupIssue` always looks for PRs first; make that conditional).
- **InfoCommand**: `.Tracker` + `.Prs` (`None` → report zero PRs, which is already a valid state the summary handles).
- **NudgeCommand**: `.Tracker` (assign/unassign) + `.Prs` (`None` → nudge logic that depends on "closed PR" detection is skipped with a clear message instead of silently doing nothing).
- **NotifyCommand**: `.Tracker` (comment, issue state) + `.Prs` (PR state) — same `None` handling as Nudge.
- **DependencyResolution**: `.Tracker` (issue state) + `.Prs` — a `depends_on: { condition: pr_merged }` entry against a provider with `Prs = None` should be a clear validation error ("this provider does not support pr_merged conditions"), not a silent no-op, since the job author asked for a specific condition that can't be evaluated.
- **Comments**: `.Tracker` (post comment) + `.Repos` (codeowners — `None` → skip codeowners resolution, same as today's "file not found" path already handles).

## File/module renames

- `OrcAI.Core/GhClient.fs` → `OrcAI.Core/Provider.fs`, module `OrcAI.Core.Provider`, holding the three interfaces + `ProviderClients`.
- `OrcAI.GitHub/GhClient.fs` / `GhCliClient` keep their names — still accurate as a concrete, provider-specific implementation.
- **Not touched**: `Domain.fs`'s `OrgName`/`RepoName`/`IssueNumber`/`ProjectInfo` stay as-is. Making those provider-neutral (opaque ids) is the separate, larger piece of work already deferred in `local-provider.md`'s Out of Scope — keeping that split explicit so this cleanup stays small and mechanical.

## Tests

- `FakeGhClient` (`tests/OrcAI.Core.Tests/FakeGhClient.fs`) implements all three interfaces on one fake type (least churn — fakes are allowed to over-implement) and test setup wraps it in a `ProviderClients { Tracker = fake; Prs = Some fake; Repos = Some fake }` fixture.
- Add fixtures/tests exercising `Prs = None` and `Repos = None` paths in CleanupCommand/NudgeCommand/DependencyResolution to lock in the graceful-degradation behavior described above.

## Order of operations

1. This plan — refactor only, verify with `dotnet test` and a real `orcai run` / `cleanup` / `nudge` / `notify` against a sandbox repo to confirm no behavior change.
2. `local-provider.md`, updated afterward so `LocalClient : IIssueTracker` and its `ProviderClients` is `{ Tracker = LocalClient(...); Prs = None; Repos = None }` — this deletes that plan's entire "no-op stand-in" table for PR/repo-inspector methods.

## Out of scope

- Domain type genericization (opaque issue/project ids) for a real Jira provider — still deferred.
- `CheckoutManager.fs` (git clone/worktree/commit/push/fork-and-pr): confirmed to already bypass `IGhClient` entirely, hardcoding GitHub HTTPS URLs and `gh auth git-credential`/`gh repo fork` with no interface at all. Worth its own cleanup eventually, but it's an "action execution" concern, not a "tracker" concern, so it doesn't belong in this split.
