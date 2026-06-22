module OrcAI.Core.Domain

open System

// ---------------------------------------------------------------------------
// Core domain types shared across all command modules.
// ---------------------------------------------------------------------------

/// Identifies a GitHub organisation.
type OrgName = OrgName of string

/// Identifies a GitHub repository (owner/repo).
type RepoName = RepoName of string

/// A GitHub issue number within a repository.
type IssueNumber = IssueNumber of int

/// A GitHub pull-request number within a repository.
type PrNumber = PrNumber of int

type ProjectInfo =
    { Org    : OrgName
      Number : int
      Title  : string
      Url    : string }

type IssueRef =
    { Repo      : RepoName
      Number    : IssueNumber
      Url       : string
      Assignees : string list }

/// Pre-fetched per-repo state returned by FetchReposState.
/// Collapses IsArchived, FindIssue, and FindClosedIssue into one bulk GraphQL call.
type RepoState =
    { IsArchived  : bool
      OpenIssue   : IssueRef option
      ClosedIssue : IssueRef option }

type PullRequestRef =
    { Repo        : RepoName
      Number      : PrNumber
      Url         : string
      ClosesIssue : IssueNumber
      State       : string }

type ClosedIssueAction = | Create | Reopen | Skip | Fail

type DependencyCondition    = | IssueClosed | PrMerged
type DependencyScope        = | PerRepo | AllRepos

[<RequireQualifiedAccess>]
type UntrackedReposBehavior = | Include | Skip

type DependsOnConfig =
    { Job            : string
      Condition      : DependencyCondition
      Scope          : DependencyScope
      UntrackedRepos : UntrackedReposBehavior }

/// How nudge handles an issue whose only related PRs are closed without merging.
/// "skip" (default) | "nudge" | "fail"
[<RequireQualifiedAccess>]
type ClosedPrAction = Nudge | Skip | Fail

/// How the nudge command re-triggers the assignee on a stale issue.
/// mode: "reassign" (default) | "comment-only" | "comment-and-reassign"
type NudgeConfig =
    { Mode    : string option
      Comment : string option }

/// Configuration for the notify command — posts a templated comment to lock file items.
type NotifyConfig =
    { Comment : string option }

/// How to invoke the execute: field.
/// Shell wraps the command in sh -c (Unix) or cmd /C (Windows).
/// Exec passes cmd and args directly via ArgumentList — no shell.
type CmdExec =
    | Shell of command: string
    | Exec  of cmd: string * args: string list

/// Write-back strategy for cmd-to-pr.
type WriteBackMode = PrToOrigin | CommitToOrigin | ForkAndPr

/// Configuration for the cmd-to-pr type.
type CmdToPrConfig =
    { Execute       : CmdExec
      Cwd           : string option
      /// None = not specified in YAML; resolved against OrcAIConfig at runtime.
      WriteBack     : WriteBackMode option
      ErrorIfNoDiff : bool
      /// None → default "orcai/{{job_title_slug}}"
      Branch        : string option
      /// None → default "[{{issue_number}}] {{job_title}}"
      CommitMessage : string option
      /// None → same default as CommitMessage
      PrTitle       : string option
      /// None → empty string
      PrBody        : string option }

/// The action to perform after an issue is created/found.
type ActionConfig =
    | AssignCopilot    of comment: string option
    | Assign           of ``to``: string * comment: string option
    | Comment          of comment: string
    | CommentAndAssign of ``to``: string * comment: string
    | Cmd              of exec: CmdExec * cwd: string option
    | CmdCheckout      of exec: CmdExec * cwd: string option
    | CmdToPr          of config: CmdToPrConfig
    | Noop

/// Top-level job configuration parsed from the YAML file.
type JobConfig =
    { Org           : OrgName
      ProjectTitle  : string
      Repos         : RepoName list
      IssueTitle    : string
      IssueBody     : string
      Labels        : string list
      Action        : ActionConfig
      OnClosedIssue : ClosedIssueAction
      Nudge         : NudgeConfig option
      Notify        : NotifyConfig option
      JobOwner      : string option
      /// Max retry attempts per (repo, category) failure before the step is skipped.
      /// None → use the built-in default.
      MaxAttempts   : int option
      DependsOn     : DependsOnConfig list
      /// When true, re-run checkout-based actions even if the lock records a prior success.
      /// None = not specified in YAML; resolved against OrcAIConfig at runtime.
      RedoOnClosed  : bool option }

/// Replace {key} placeholders in a template string.
/// Tokens not present in vars are left unreplaced.
let renderTemplate (vars: Map<string, string>) (tmpl: string) =
    vars |> Map.fold (fun (s: string) k v -> s.Replace("{" + k + "}", v)) tmpl

/// Replace {{key}} placeholders in a template string (used for action templates).
/// Tokens not present in vars are left unreplaced.
let renderActionTemplate (vars: Map<string, string>) (tmpl: string) =
    vars |> Map.fold (fun (s: string) k v -> s.Replace("{{" + k + "}}", v)) tmpl

/// Extract the target assignee handle from an action, if any.
/// Used by nudge and notify to derive the assignee for templating.
let extractAssignee (action: ActionConfig) : string option =
    match action with
    | AssignCopilot _                          -> Some "@copilot"
    | Assign(``to``, _)                        -> Some ``to``
    | CommentAndAssign(``to``, _)              -> Some ``to``
    | Comment _ | Cmd _ | CmdCheckout _
    | CmdToPr _ | Noop                         -> None

/// Which step a recorded failure belongs to. Each (repo, category) is unique
/// within the lock file's `Failures` list.
type RepoFailureCategory =
    | FindIssue
    | CreateIssue
    | ReopenIssue
    | AssignIssue
    | AddToProject
    | UpdateBody
    | CmdCheckoutFailed
    | CmdToPrCheckoutFailed
    | CmdToPrNoDiff
    | CmdToPrPushFailed
    | CmdToPrOpenPrFailed

/// Classified reason for a failure, derived from the raw `gh` error message.
/// Drives retry/skip decisions on subsequent runs.
type RepoFailureCause =
    | RateLimit
    | NotFound
    | Permission
    | UserError
    | NetworkTransient
    | Unknown

/// A persistent record of a failed step. Survives across runs so the tool can
/// count attempts, branch on cause, and skip steps that have exhausted retries.
type RepoFailure =
    { Repo          : RepoName
      Category      : RepoFailureCategory
      Cause         : RepoFailureCause
      Attempts      : int
      FirstFailedAt : DateTimeOffset
      LastFailedAt  : DateTimeOffset
      LastMessage   : string }

/// Snapshot of a completed job, persisted as a lock file.
type LockFile =
    { LockedAt     : DateTimeOffset
      YamlHash     : string
      TemplateHash : string
      Project      : ProjectInfo
      Repos        : RepoName list
      Issues       : IssueRef list
      PullRequests : PullRequestRef list
      /// Repos skipped during the run because they are archived (read-only).
      SkippedRepos : RepoName list
      /// Per-step failures persisted across runs; drives retry decisions.
      Failures     : RepoFailure list }
