module OrcAI.Core.DependencyResolution

// ---------------------------------------------------------------------------
// Topological ordering, cycle detection, and per-repo eligibility checks
// for depends_on job chains.
// ---------------------------------------------------------------------------

open System
open System.IO
open System.IO.Abstractions
open OrcAI.Core.Domain
open OrcAI.Core.Provider

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
            Error $"Dependency file not found: {fs.Path.GetFileName(absPath)}"
        elif List.contains absPath visitingChain then
            let chainStr =
                (visitingChain @ [ absPath ])
                |> List.map fs.Path.GetFileName
                |> String.concat " → "
            Error $"Circular dependency detected: {chainStr}"
        elif Set.contains absPath visited then
            Ok([], visited)
        else
            match YamlConfig.parseFile fs absPath with
            | Error msg -> Error $"Failed to read '{fs.Path.GetFileName(absPath)}': {msg}"
            | Ok config ->
                let yamlDir = fs.Path.GetDirectoryName(absPath)
                let chain'  = visitingChain @ [ absPath ]
                let folder (acc: Result<string list * Set<string>, string>) (dep: DependsOnConfig) =
                    match acc with
                    | Error e -> Error e
                    | Ok (order, vis) ->
                        let depAbs = fs.Path.GetFullPath(fs.Path.Combine(yamlDir, dep.Job))
                        match dfs chain' vis depAbs with
                        | Error e -> Error e
                        | Ok (depOrder, vis') -> Ok(order @ depOrder, vis')
                match config.DependsOn |> List.fold folder (Ok([], visited)) with
                | Error e -> Error e
                | Ok (depOrder, visited') -> Ok(depOrder @ [ absPath ], Set.add absPath visited')
    dfs [] Set.empty (fs.Path.GetFullPath(yamlPath)) |> Result.map fst

/// Expand a list of user-provided paths into a topologically ordered, deduplicated
/// chain. Returns (absolutePath * isDependency) pairs; isDependency is true only
/// for paths introduced by a depends_on chain rather than supplied directly by
/// the user.
///
/// Paths that cannot be parsed (missing or invalid YAML) are passed through as-is
/// rather than causing a chain-level failure; executeSingle will surface the error.
/// Cycle and missing-dependency errors in otherwise-valid jobs ARE propagated.
let resolveChain (fs: IFileSystem) (userPaths: string list) : Result<(string * bool) list, string> =
    let userAbsSet = userPaths |> List.map fs.Path.GetFullPath |> Set.ofList
    let rec collect
        (remaining : string list)
        (seen      : Set<string>)
        (acc       : (string * bool) list)
        : Result<(string * bool) list, string> =
        match remaining with
        | [] -> Ok acc
        | p :: rest ->
            let absP = fs.Path.GetFullPath(p)
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
/// Returns Ok false if the repo has no issue recorded in the lock, or Error if the
/// condition requires a capability (pr_merged → PR linking) the provider doesn't have.
let private checkConditionForTrackedRepo
    (tracker   : IIssueTracker)
    (prs       : IPullRequestLinker option)
    (lock      : LockFile)
    (condition : DependencyCondition)
    (repo      : RepoName)
    : Async<Result<bool, string>> =
    async {
        let issueOpt = lock.Issues |> List.tryFind (fun i -> i.Repo = repo)
        match issueOpt with
        | None ->
            // Repo is in lock.Repos but no issue was created — condition cannot be met.
            return Ok false
        | Some issue ->
            match condition with
            | IssueClosed ->
                let! stateOpt = tracker.GetIssueState repo issue.Id
                let isClosed =
                    stateOpt
                    |> Option.map (fun s -> System.String.Equals(s, "CLOSED", System.StringComparison.OrdinalIgnoreCase))
                    |> Option.defaultValue false
                return Ok isClosed
            | PrMerged ->
                match prs with
                | None -> return Error "This provider does not support 'pr_merged' depends_on conditions (no pull-request linker available)."
                | Some prs ->
                    let mergedInLock =
                        lock.PullRequests
                        |> List.exists (fun pr ->
                            pr.Repo         = repo
                            && pr.ClosesIssue = issue.Id
                            && pr.State       = "MERGED")
                    if mergedInLock then
                        return Ok true
                    else
                        let! foundPrs = prs.FindPrsForIssue repo issue.Id
                        return Ok (foundPrs |> List.exists (fun pr -> pr.State = "MERGED"))
    }

/// Apply a single depends_on entry to filter candidateRepos to those eligible.
/// Returns Error if an all_repos gate is not met, or if a condition check fails.
let private applyDependency
    (tracker        : IIssueTracker)
    (prs            : IPullRequestLinker option)
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
                            let! met = checkConditionForTrackedRepo tracker prs lock dep.Condition repo
                            return repo, met
                        })
                    |> Async.Parallel
                let firstError = condResults |> Array.tryPick (fun (_, r) -> match r with Error e -> Some e | Ok _ -> None)
                match firstError with
                | Some e -> return Error e
                | None ->
                let failing = condResults |> Array.choose (fun (repo, r) -> match r with Ok false -> Some repo | _ -> None)
                if failing.Length > 0 then
                    let examples =
                        failing
                        |> Array.truncate 3
                        |> Array.map (fun (RepoName r) -> r)
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
                            return Ok includeUntracked
                        else
                            match upstreamLock with
                            | None   -> return Ok false
                            | Some lock ->
                                return! checkConditionForTrackedRepo tracker prs lock dep.Condition repo
                    })
                |> Async.Parallel
            let firstError = eligibility |> Array.tryPick (function Error e -> Some e | Ok _ -> None)
            match firstError with
            | Some e -> return Error e
            | None ->
            let eligible =
                List.zip candidateRepos (eligibility |> Array.toList)
                |> List.choose (fun (repo, elig) -> match elig with Ok true -> Some repo | _ -> None)
            return Ok eligible
    }

/// Apply all depends_on entries for config, returning the eligible subset of
/// config.Repos. Returns Error if any all_repos gate is not met.
let filterRepos
    (tracker  : IIssueTracker)
    (prs      : IPullRequestLinker option)
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
                    let upstreamPath = fs.Path.GetFullPath(fs.Path.Combine(yamlDir, dep.Job))
                    match YamlConfig.parseFile fs upstreamPath with
                    | Error msg ->
                        return Error $"Failed to read upstream job '{dep.Job}': {msg}"
                    | Ok upstreamConfig ->
                        let upstreamLock = LockFile.tryRead fs upstreamPath
                        return! applyDependency tracker prs upstreamConfig upstreamLock dep repos
            })
        (async { return Ok config.Repos })
