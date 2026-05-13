module OrcAI.Core.Comments

open OrcAI.Core.Domain
open OrcAI.Core.GhClient

let buildCommentVars (assignTo: string) (jobOwner: string option) (repoOwners: string option) : Map<string, string> =
    [ "assignee", assignTo
      yield! jobOwner   |> Option.map (fun v -> "job.owner",       v) |> Option.toList
      yield! repoOwners |> Option.map (fun v -> "repo.codeowners", v) |> Option.toList ]
    |> Map.ofList

let postTemplatedComment
        (client    : IGhClient)
        (repo      : RepoName)
        (issue     : IssueNumber)
        (assignTo  : string)
        (jobOwner  : string option)
        (template  : string)
        (verbose   : bool)
        (label     : string)
        (extraVars : Map<string, string>)
        : Async<unit> =
    async {
        let (RepoName repoStr)   = repo
        let! codeownersContent   = client.FetchCodeowners repo
        let repoOwners           = codeownersContent |> Option.bind Codeowners.parseCatchAll
        let builtIn              = buildCommentVars assignTo jobOwner repoOwners
        let vars                 = Map.fold (fun acc k v -> Map.add k v acc) builtIn extraVars
        let body                 = renderTemplate vars template
        if verbose then eprintfn "[%s] Posting %s comment" repoStr label
        match! client.PostComment repo issue body with
        | Error e -> eprintfn "[%s] Warning: failed to post %s comment: %s" repoStr label e
        | Ok ()   -> ()
    }
