module OrcAI.Core.ValidateCommand

// ---------------------------------------------------------------------------
// Implements the `orcai validate` command.
//
// Performs a non-destructive pre-flight check against a YAML job config:
//   1. File exists on disk.
//   2. Schema is valid (parses correctly, all required fields present,
//      referenced template exists).
//   3. Every referenced repository is accessible with the current credentials.
//
// All errors across all checks are collected; validation does not fail fast.
// ---------------------------------------------------------------------------

open OrcAI.Core.Domain
open OrcAI.Core.GhClient
open OrcAI.Core.Deps

/// Input parameters derived from parsed CLI arguments.
type ValidateInput =
    { YamlPath        : string
      NoParallel      : bool
      MaxConcurrency  : int
      ContinueOnError : bool
      SkipLock        : bool }

/// The result returned to the CLI for display.
type ValidateResult =
    { ConfigErrors   : string list
      ReposTrusted   : RepoName list            // in lock file — not re-checked
      RepoSuccesses  : RepoName list            // live-checked and accessible
      RepoErrors     : (RepoName * string) list // live-checked and inaccessible
      IsValid        : bool }

// ---------------------------------------------------------------------------
// Execute (internal — single resolved path)
// ---------------------------------------------------------------------------

/// Check a single YAML path. Exposed as `execute` after mapping over the list.
let private executeSingle (deps: OrcAIDeps) (skipLock: bool) (path: string) : Async<ValidateResult> =
    async {
        // Step 1 + 2: file existence and schema validation delegated to parseFile,
        // which checks file-exists first and then validates the schema.
        match YamlConfig.parseFile deps.FileSystem path with
        | Error e ->
            // No point checking repos — return early with config error.
            return { ConfigErrors = [e]; ReposTrusted = []; RepoSuccesses = []; RepoErrors = []; IsValid = false }
        | Ok config ->

        // Step 3: determine which repos need a live accessibility check.
        // With --skip-lock, or on first run (no lock file), all repos are checked.
        // When a lock file exists, only repos new to the YAML (not yet in the lock)
        // need checking — repos already in the lock were verified by a prior run.
        let reposToCheck, reposTrusted =
            if skipLock then
                config.Repos, []
            else
                match LockFile.tryRead deps.FileSystem path with
                | None      -> config.Repos, []   // first run — check everything
                | Some lock ->
                    let lockRepoSet = lock.Repos |> Set.ofList
                    let toCheck  = config.Repos |> List.filter (fun r -> not (Set.contains r lockRepoSet))
                    let trusted  = config.Repos |> List.filter (fun r -> Set.contains r lockRepoSet)
                    toCheck, trusted

        if reposToCheck.IsEmpty then
            return { ConfigErrors = []; ReposTrusted = reposTrusted; RepoSuccesses = []; RepoErrors = []; IsValid = true }
        else

        // Live-check only the repos that need it via a single batched GraphQL call.
        let! repoResultMap = deps.GhClient.ReposExist reposToCheck

        let repoSuccesses, repoErrors =
            repoResultMap
            |> Map.toList
            |> List.fold (fun (succs, errs) (repo, result) ->
                match result with
                | Ok ()   -> (repo :: succs, errs)
                | Error e -> (succs, (repo, e) :: errs))
                ([], [])

        return
            { ConfigErrors  = []
              ReposTrusted  = reposTrusted
              RepoSuccesses = repoSuccesses
              RepoErrors    = repoErrors
              IsValid       = repoErrors.IsEmpty }
    }

// ---------------------------------------------------------------------------
// Public entry point
// ---------------------------------------------------------------------------

/// Execute the validate command over a list of resolved file paths.
///
/// Files are processed in parallel up to MaxConcurrency (or sequentially when
/// NoParallel=true). With ContinueOnError all files are attempted; without it,
/// processing stops on the first failure.
///
/// Returns a list of (path * ValidateResult) pairs.
let execute (deps: OrcAIDeps) (paths: string list) (input: ValidateInput) : Async<(string * ValidateResult) list> =
    async {
        if input.NoParallel then
            let results = System.Collections.Generic.List<string * ValidateResult>()
            let mutable stop = false
            for path in paths do
                if not stop then
                    let! r = executeSingle deps input.SkipLock path
                    results.Add((path, r))
                    if not r.IsValid && not input.ContinueOnError then
                        stop <- true
            return results |> Seq.toList
        else
            let semaphore = new System.Threading.SemaphoreSlim(input.MaxConcurrency)
            let runOne (path: string) : Async<string * ValidateResult> =
                async {
                    do! semaphore.WaitAsync() |> Async.AwaitTask
                    try
                        let! r = executeSingle deps input.SkipLock path
                        return (path, r)
                    finally
                        semaphore.Release() |> ignore
                }
            let! pairs = paths |> List.map runOne |> Async.Parallel
            return pairs |> Array.toList
    }
