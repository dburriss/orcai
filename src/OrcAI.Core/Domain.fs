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
      Nudge         : NudgeConfig option }

/// Snapshot of a completed job, persisted as a lock file.
type LockFile =
    { LockedAt     : DateTimeOffset
      YamlHash     : string
      TemplateHash : string
      Project      : ProjectInfo
      Repos        : RepoName list
      Issues       : IssueRef list
      PullRequests : PullRequestRef list }
