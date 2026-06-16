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
      ClosesIssue : IssueNumber }

type ClosedIssueAction = | Create | Reopen | Skip | Fail

/// How the assignee is triggered on a new issue.
/// "assign" (default) | "comment" | "comment-and-assign"
type AssignConfig =
    { To      : string option
      Via     : string option
      Comment : string option }

/// How the nudge command re-triggers the assignee on a stale issue.
/// mode: "reassign" (default) | "comment-only" | "comment-and-reassign"
type NudgeConfig =
    { Mode    : string option
      Comment : string option }

/// Configuration for the notify command — posts a templated comment to lock file items.
type NotifyConfig =
    { Comment : string option }

/// Top-level job configuration parsed from the YAML file.
type JobConfig =
    { Org           : OrgName
      ProjectTitle  : string
      Repos         : RepoName list
      IssueTitle    : string
      IssueBody     : string
      Labels        : string list
      SkipCopilot   : bool
      OnClosedIssue : ClosedIssueAction
      Assign        : AssignConfig option
      Nudge         : NudgeConfig option
      Notify        : NotifyConfig option
      JobOwner      : string option
      /// Max retry attempts per (repo, category) failure before the step is skipped.
      /// None → use the built-in default.
      MaxAttempts   : int option }

/// Replace {key} placeholders in a template string.
/// Tokens not present in vars are left unreplaced.
let renderTemplate (vars: Map<string, string>) (tmpl: string) =
    vars |> Map.fold (fun (s: string) k v -> s.Replace("{" + k + "}", v)) tmpl

/// Which step a recorded failure belongs to. Each (repo, category) is unique
/// within the lock file's `Failures` list.
type RepoFailureCategory =
    | FindIssue
    | CreateIssue
    | ReopenIssue
    | AssignIssue
    | AddToProject
    | UpdateBody

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
