module Orca.Core.CleanupCommand

// ---------------------------------------------------------------------------
// Implements the `orca cleanup` command.
//
// For the job described in the YAML config (or its lock file):
//   1. Closes any open PRs that reference the managed issues.
//   2. Deletes the managed issues from each repository.
//   3. Deletes the GitHub Project.
//
// When --dryrun is set, the command lists what would be deleted but makes
// no API calls.
//
// Lock file preference:
//   If a lock file exists alongside the YAML, it is used to determine the
//   exact project number and issue list (avoiding extra GitHub API calls).
//   If no lock file exists, the project is located by title via the GitHub
//   API and issues are read from the YAML config repos.
// ---------------------------------------------------------------------------

open System.IO
open Orca.Core.Domain
open Orca.Core.GhClient
open Orca.Core.Deps

/// Input parameters derived from parsed CLI arguments.
type CleanupInput =
    { YamlPath : string
      DryRun   : bool }

/// A resource that was (or would be) cleaned up.
type CleanedResource =
    | CleanedPr      of repo: string * prNumber: int
    | CleanedIssue   of repo: string * issueNumber: int
    | CleanedProject of org: string * name: string * number: int
    | RemovedLockFile

/// The result returned to the caller for display.
type CleanupResult =
    { DryRun    : bool
      Resources : CleanedResource list }

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

/// Process cleanup for a single issue: close its PRs then delete the issue.
/// In dry-run mode, makes no API calls.
/// Returns Ok with the list of resources acted on, or Error.
let private cleanupIssue
    (client : IGhClient)
    (issue  : IssueRef)
    (dryRun : bool)
    : Async<Result<CleanedResource list, string>> =
    async {
        let (RepoName repoStr)   = issue.Repo
        let (IssueNumber issueN) = issue.Number

        // 1. Find PRs that close this issue
        let! prs = client.FindPrsForIssue issue.Repo issue.Number

        // 2. Close each PR
        let mutable prResources : CleanedResource list = []
        for pr in prs do
            let (PrNumber prN) = pr.Number
            if not dryRun then
                match! client.ClosePr issue.Repo pr.Number with
                | Error e -> eprintfn "Warning: failed to close PR #%d in %s: %s" prN repoStr e
                | Ok ()   -> ()
            prResources <- prResources @ [CleanedPr(repoStr, prN)]

        // 3. Delete the issue
        if dryRun then
            return Ok (prResources @ [CleanedIssue(repoStr, issueN)])
        else
            match! client.CloseIssue issue.Repo issue.Number with
            | Error e -> return Error $"Failed to delete issue #{issueN} in {repoStr}: {e}"
            | Ok ()   -> return Ok (prResources @ [CleanedIssue(repoStr, issueN)])
    }

// ---------------------------------------------------------------------------
// Execute
// ---------------------------------------------------------------------------

/// Execute the cleanup command.
/// Returns a CleanupResult on success, or an error string.
let execute (deps: OrcaDeps) (input: CleanupInput) : Result<CleanupResult, string> =
    // 1. Parse YAML to get org and project title (needed whether or not lock exists)
    match YamlConfig.parseFile input.YamlPath with
    | Error e -> Error e
    | Ok config ->

    // 2. Resolve auth token
    match deps.AuthContext.GetToken() |> Async.RunSynchronously with
    | Error e -> Error $"Auth error: {e}"
    | Ok _ ->

    // 3. Resolve project and issues — prefer lock file
    let projectAndIssues : Result<ProjectInfo * IssueRef list, string> =
        match LockFile.tryRead input.YamlPath with
        | Some lock ->
            Ok (lock.Project, lock.Issues)
        | None ->
            // No lock file: find project by title, build stub IssueRefs from config
            // (without issue numbers we cannot delete — require a lock file or live query)
            match deps.GhClient.FindProject config.Org config.ProjectTitle |> Async.RunSynchronously with
            | None ->
                let (OrgName orgStr) = config.Org
                Error $"Project '{config.ProjectTitle}' not found in '{orgStr}'. Nothing to clean up."
            | Some project ->
                // Without a lock file we don't have issue numbers.
                // Look up each issue by title in each repo.
                let issues =
                    config.Repos
                    |> List.choose (fun repo ->
                        deps.GhClient.FindIssue repo config.IssueTitle
                        |> Async.RunSynchronously)
                Ok (project, issues)

    match projectAndIssues with
    | Error e -> Error e
    | Ok (project, issues) ->

    // 4. Cleanup each issue (PRs first, then issue)
    let issueCleanupResults =
        issues
        |> List.map (fun issue ->
            cleanupIssue deps.GhClient issue input.DryRun
            |> Async.RunSynchronously)

    let issueErrors =
        issueCleanupResults
        |> List.choose (function Error e -> Some e | Ok _ -> None)

    if issueErrors.Length > 0 then
        Error (issueErrors |> String.concat "; ")
    else

    let issueResources =
        issueCleanupResults
        |> List.collect (function Ok rs -> rs | Error _ -> [])

    // 5. Delete the project
    let (OrgName orgStr) = project.Org
    let projectResource  = CleanedProject(orgStr, project.Title, project.Number)

    let deleteResult =
        if input.DryRun then
            Ok projectResource
        else
            match deps.GhClient.DeleteProject project |> Async.RunSynchronously with
            | Error e -> Error $"Failed to delete project: {e}"
            | Ok ()   -> Ok projectResource

    match deleteResult with
    | Error e -> Error e
    | Ok projRes ->

    // 6. Delete the lock file on success (not in dry-run)
    let lockFileResource =
        if not input.DryRun then
            let lockPath = LockFile.lockFilePath input.YamlPath
            if File.Exists(lockPath) then
                File.Delete(lockPath)
                [RemovedLockFile]
            else
                []
        else
            []

    Ok { DryRun    = input.DryRun
         Resources = issueResources @ [projRes] @ lockFileResource }
