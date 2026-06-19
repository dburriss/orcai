module OrcAI.Core.DependencyResolution

// ---------------------------------------------------------------------------
// Topological ordering, cycle detection, and per-repo eligibility checks
// for depends_on job chains.
// ---------------------------------------------------------------------------

open System
open System.IO
open System.IO.Abstractions
open OrcAI.Core.Domain
open OrcAI.Core.GhClient

// ---------------------------------------------------------------------------
// Topological ordering
// ---------------------------------------------------------------------------

/// Walk the depends_on chain from yamlPath, returning absolute paths in
/// topological order (dependencies first, then the file itself).
/// Returns Error on cycle detection or a missing upstream file.
let resolveOrder (fs: IFileSystem) (yamlPath: string) : Result<string list, string> =
    let rec dfs
        (visitingChain : string list)
        (visited       : Set<string>)
        (absPath       : string)
        : Result<string list * Set<string>, string> =
        if not (fs.File.Exists(absPath)) then
            Error $"Dependency file not found: {Path.GetFileName(absPath)}"
        elif List.contains absPath visitingChain then
            let chainStr =
                (visitingChain @ [ absPath ])
                |> List.map Path.GetFileName
                |> String.concat " → "
            Error $"Circular dependency detected: {chainStr}"
        elif Set.contains absPath visited then
            Ok([], visited)
        else
            match YamlConfig.parseFile fs absPath with
            | Error msg -> Error $"Failed to read '{Path.GetFileName(absPath)}': {msg}"
            | Ok config ->
                let yamlDir = Path.GetDirectoryName(absPath)
                let chain'  = visitingChain @ [ absPath ]
                let folder (acc: Result<string list * Set<string>, string>) (dep: DependsOnConfig) =
                    match acc with
                    | Error e -> Error e
                    | Ok (order, vis) ->
                        let depAbs = Path.GetFullPath(Path.Combine(yamlDir, dep.Job))
                        match dfs chain' vis depAbs with
                        | Error e -> Error e
                        | Ok (depOrder, vis') -> Ok(order @ depOrder, vis')
                match config.DependsOn |> List.fold folder (Ok([], visited)) with
                | Error e -> Error e
                | Ok (depOrder, visited') -> Ok(depOrder @ [ absPath ], Set.add absPath visited')
    dfs [] Set.empty (Path.GetFullPath(yamlPath)) |> Result.map fst

/// Expand a list of user-provided paths into a topologically ordered, deduplicated
/// chain. Returns (absolutePath * isDependency) pairs; isDependency is true only
/// for paths introduced by a depends_on chain rather than supplied directly by
/// the user.
///
/// Paths that cannot be parsed (missing or invalid YAML) are passed through as-is
/// rather than causing a chain-level failure; executeSingle will surface the error.
/// Cycle and missing-dependency errors in otherwise-valid jobs ARE propagated.
let resolveChain (fs: IFileSystem) (userPaths: string list) : Result<(string * bool) list, string> =
    let userAbsSet = userPaths |> List.map Path.GetFullPath |> Set.ofList
    let rec collect
        (remaining : string list)
        (seen      : Set<string>)
        (acc       : (string * bool) list)
        : Result<(string * bool) list, string> =
        match remaining with
        | [] -> Ok acc
        | p :: rest ->
            let absP = Path.GetFullPath(p)
            // If the path is missing or has invalid YAML, include it as-is so that
            // executeSingle can report the proper error for that specific file.
            let canExpand =
                fs.File.Exists(absP)
                && match YamlConfig.parseFile fs absP with Ok _ -> true | Error _ -> false
            let expandResult =
                if canExpand then resolveOrder fs absP
                else Ok [ absP ]
            match expandResult with
            | Error e -> Error e
            | Ok ordered ->
                let fresh   = ordered |> List.filter (fun q -> not (Set.contains q seen))
                let entries = fresh |> List.map (fun q -> q, not (Set.contains q userAbsSet))
                let seen'   = fresh |> List.fold (fun s q -> Set.add q s) seen
                collect rest seen' (acc @ entries)
    collect userPaths Set.empty []

// ---------------------------------------------------------------------------
// Dependency condition checking
// ---------------------------------------------------------------------------

/// Check whether the condition is met for a repo tracked by the upstream lock.
/// Returns false if the repo has no issue recorded in the lock.
let private checkConditionForTrackedRepo
    (client    : IGhClient)
    (lock      : LockFile)
    (condition : DependencyCondition)
    (repo      : RepoName)
    : Async<bool> =
    async {
        let issueOpt = lock.Issues |> List.tryFind (fun i -> i.Repo = repo)
        match issueOpt with
        | None ->
            // Repo is in lock.Repos but no issue was created — condition cannot be met.
            return false
        | Some issue ->
            match condition with
            | IssueClosed ->
                let! stateOpt = client.GetIssueState repo issue.Number
                return stateOpt = Some "CLOSED"
            | PrMerged ->
                let mergedInLock =
                    lock.PullRequests
                    |> List.exists (fun pr ->
                        pr.Repo         = repo
                        && pr.ClosesIssue = issue.Number
                        && pr.State       = "MERGED")
                if mergedInLock then
                    return true
                else
                    let! prs = client.FindPrsForIssue repo issue.Number
                    return prs |> List.exists (fun pr -> pr.State = "MERGED")
    }

/// Apply a single depends_on entry to filter candidateRepos to those eligible.
/// Returns Error if an all_repos gate is not met.
let private applyDependency
    (client         : IGhClient)
    (upstreamConfig : JobConfig)
    (upstreamLock   : LockFile option)
    (dep            : DependsOnConfig)
    (candidateRepos : RepoName list)
    : Async<Result<RepoName list, string>> =
    async {
        let upstreamRepoSet =
            match upstreamLock with
            | Some lock -> lock.Repos |> Set.ofList
            | None      -> upstreamConfig.Repos |> Set.ofList
        let isTracked        repo = Set.contains repo upstreamRepoSet
        let includeUntracked      = dep.UntrackedRepos = UntrackedReposBehavior.Include
        let condStr               = match dep.Condition with | PrMerged -> "pr_merged" | IssueClosed -> "issue_closed"

        match dep.Scope with
        | AllRepos ->
            match upstreamLock with
            | None ->
                return Error $"Dependency gate not met: upstream job '{dep.Job}' has no lock file — has it been run yet?"
            | Some lock ->
                let! condResults =
                    lock.Repos
                    |> List.map (fun repo ->
                        async {
                            let! met = checkConditionForTrackedRepo client lock dep.Condition repo
                            return repo, met
                        })
                    |> Async.Parallel
                let failing = condResults |> Array.filter (not << snd)
                if failing.Length > 0 then
                    let examples =
                        failing
                        |> Array.truncate 3
                        |> Array.map (fun (RepoName r, _) -> r)
                        |> String.concat ", "
                    return Error $"Dependency gate not met: {failing.Length} repo(s) have not satisfied '{condStr}' in '{dep.Job}' (e.g. {examples})"
                else
                    let eligible = candidateRepos |> List.filter (fun r -> isTracked r || includeUntracked)
                    return Ok eligible

        | PerRepo ->
            let! eligibility =
                candidateRepos
                |> List.map (fun repo ->
                    async {
                        if not (isTracked repo) then
                            return includeUntracked
                        else
                            match upstreamLock with
                            | None   -> return false
                            | Some lock ->
                                return! checkConditionForTrackedRepo client lock dep.Condition repo
                    })
                |> Async.Parallel
            let eligible =
                List.zip candidateRepos (eligibility |> Array.toList)
                |> List.choose (fun (repo, elig) -> if elig then Some repo else None)
            return Ok eligible
    }

/// Apply all depends_on entries for config, returning the eligible subset of
/// config.Repos. Returns Error if any all_repos gate is not met.
let filterRepos
    (client   : IGhClient)
    (fs       : IFileSystem)
    (config   : JobConfig)
    (yamlDir  : string)
    : Async<Result<RepoName list, string>> =
    config.DependsOn
    |> List.fold
        (fun accAsync dep ->
            async {
                match! accAsync with
                | Error e -> return Error e
                | Ok repos ->
                    let upstreamPath = Path.GetFullPath(Path.Combine(yamlDir, dep.Job))
                    match YamlConfig.parseFile fs upstreamPath with
                    | Error msg ->
                        return Error $"Failed to read upstream job '{dep.Job}': {msg}"
                    | Ok upstreamConfig ->
                        let upstreamLock = LockFile.tryRead fs upstreamPath
                        return! applyDependency client upstreamConfig upstreamLock dep repos
            })
        (async { return Ok config.Repos })
