module OrcAI.Core.GraphCommand

// ---------------------------------------------------------------------------
// Implements `orcai graph` — renders the depends_on DAG as an ASCII tree.
// File-system only; no GitHub API calls.
// ---------------------------------------------------------------------------

open System.IO
open OrcAI.Core.Domain
open OrcAI.Core.Deps

/// Input parameters derived from parsed CLI arguments.
type GraphInput =
    { YamlPath : string }

/// The result returned to the CLI for display.
type GraphResult =
    { Lines : string list }

// ---------------------------------------------------------------------------
// Rendering helpers
// ---------------------------------------------------------------------------

let private depLabelStr (dep: DependsOnConfig) =
    let condStr     = match dep.Condition with | PrMerged -> "pr_merged" | IssueClosed -> "issue_closed"
    let scopeStr    = match dep.Scope with | PerRepo -> "per_repo" | AllRepos -> "all_repos"
    let untrackedStr =
        match dep.UntrackedRepos with
        | UntrackedReposBehavior.Include -> ""
        | UntrackedReposBehavior.Skip    -> ", skip-untracked"
    $" ({condStr} · {scopeStr}{untrackedStr})"

let rec private renderChildren
    (fs           : System.IO.Abstractions.IFileSystem)
    (parentAbs    : string)
    (deps         : DependsOnConfig list)
    (parentPrefix : string)
    (visited      : Set<string>)
    : string list =
    deps
    |> List.mapi (fun i dep ->
        let isLast      = i = deps.Length - 1
        let connector   = if isLast then "└── " else "├── "
        let childPrefix = parentPrefix + (if isLast then "    " else "│   ")
        let depAbs      = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(parentAbs), dep.Job))
        let header      = $"{parentPrefix}{connector}{Path.GetFileName(depAbs)}{depLabelStr dep}"
        if Set.contains depAbs visited then
            [ header + " (↑ cycle)" ]
        else
            let childDeps =
                if fs.File.Exists(depAbs) then
                    match YamlConfig.parseFile fs depAbs with
                    | Ok config -> config.DependsOn
                    | Error _   -> []
                else []
            let children = renderChildren fs depAbs childDeps childPrefix (Set.add depAbs visited)
            header :: children)
    |> List.concat

// ---------------------------------------------------------------------------
// Public entry point
// ---------------------------------------------------------------------------

let execute (deps: OrcAIDeps) (input: GraphInput) : Result<GraphResult, string> =
    let absPath = Path.GetFullPath(input.YamlPath)
    match YamlConfig.parseFile deps.FileSystem absPath with
    | Error e -> Error e
    | Ok config ->
        let rootLine   = Path.GetFileName(absPath)
        let childLines = renderChildren deps.FileSystem absPath config.DependsOn "" (Set.singleton absPath)
        Ok { Lines = rootLine :: childLines }
