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
      ContinueOnError : bool }

/// The result returned to the CLI for display.
type ValidateResult =
    { ConfigErrors : string list
      RepoErrors   : (RepoName * string) list
      IsValid      : bool }

// ---------------------------------------------------------------------------
// Execute (internal — single resolved path)
// ---------------------------------------------------------------------------

/// Check a single YAML path. Exposed as `execute` after mapping over the list.
let private executeSingle (deps: OrcAIDeps) (noParallel: bool) (path: string) : Async<ValidateResult> =
    async {
        // Step 1 + 2: file existence and schema validation delegated to parseFile,
        // which checks file-exists first and then validates the schema.
        match YamlConfig.parseFile deps.FileSystem path with
        | Error e ->
            // No point checking repos — return early with config error.
            return { ConfigErrors = [e]; RepoErrors = []; IsValid = false }
        | Ok config ->

        // Step 3: check every repo in parallel (or sequentially if --no-parallel).
        let checkRepo (repo: RepoName) : Async<(RepoName * string) option> =
            async {
                try
                    match! deps.GhClient.RepoExists repo with
                    | Ok ()   -> return None
                    | Error e -> return Some (repo, e)
                with ex ->
                    let (RepoName repoStr) = repo
                    return Some (repo, $"Exception checking repo '{repoStr}': {ex.Message}")
            }

        let! repoCheckResults =
            if noParallel then
                async {
                    let results = System.Collections.Generic.List<(RepoName * string) option>()
                    for repo in config.Repos do
                        let! r = checkRepo repo
                        results.Add(r)
                    return results |> Seq.toArray
                }
            else
                config.Repos
                |> List.map checkRepo
                |> Async.Parallel

        let repoErrors =
            repoCheckResults
            |> Array.choose id
            |> Array.toList

        return
            { ConfigErrors = []
              RepoErrors   = repoErrors
              IsValid      = repoErrors.IsEmpty }
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
                    let! r = executeSingle deps input.NoParallel path
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
                        let! r = executeSingle deps input.NoParallel path
                        return (path, r)
                    finally
                        semaphore.Release() |> ignore
                }
            let! pairs = paths |> List.map runOne |> Async.Parallel
            return pairs |> Array.toList
    }
