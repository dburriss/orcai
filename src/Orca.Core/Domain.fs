module Orca.Core.Domain

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
      Title  : string }

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

/// Top-level job configuration parsed from the YAML file.
type JobConfig =
    { Org           : OrgName
      ProjectTitle  : string
      Repos         : RepoName list
      IssueTitle    : string
      IssueBody     : string }

/// Snapshot of a completed job, persisted as a lock file.
type LockFile =
    { LockedAt    : DateTimeOffset
      YamlHash    : string
      Project     : ProjectInfo
      Repos       : RepoName list
      Issues      : IssueRef list
      PullRequests: PullRequestRef list }
