module OrcAI.Core.NotifyCommand

// ---------------------------------------------------------------------------
// Implements the `orcai notify` command.
//
// Posts a templated comment to issues and/or PRs in the lock file,
// with optional filtering by target type (issues | prs | both) and
// current GitHub state (open | closed | all).
// ---------------------------------------------------------------------------

open OrcAI.Core.Domain
open OrcAI.Core.Provider
open OrcAI.Core.Deps

type NotifyInput =
    { YamlPath  : string
      DryRun    : bool
      Verbose   : bool
      Target    : string              // "issues" (default) | "prs" | "both"
      State     : string              // "open" (default) | "closed" | "all"
      Template  : string option       // CLI override for notify.comment
      ExtraVars : Map<string, string> // extra template variables from --data / --json-data
    }

type NotifyOutcome = | Skipped | Notified | DryRunWouldNotify

type NotifyResult =
    { Repo    : RepoName
      Number  : string
      Kind    : string  // "issue" | "pr"
      Outcome : NotifyOutcome }

[<NoComparison>]
type private NotifyItem =
    | IssueItem of IssueRef
    | PrItem    of PullRequestRef

let execute (deps: OrcAIDeps) (input: NotifyInput) : Result<NotifyResult list, string> =
    match YamlConfig.parseFile deps.FileSystem input.YamlPath with
    | Error e -> Error e
    | Ok jobConfig ->

    match LockFile.tryRead deps.FileSystem input.YamlPath with
    | None -> Error "No lock file found — run 'orcai run' first."
    | Some lock ->

    match deps.ResolveProvider jobConfig with
    | Error e -> Error $"Provider error: {e}"
    | Ok providerClients ->

    let client = providerClients.Tracker

    let pickNotify f =
        jobConfig.Notify |> Option.bind f
        |> Option.orElse (deps.Config.Notify |> Option.bind f)

    let assignTo         = extractAssignee jobConfig.Action |> Option.defaultValue "@copilot"
    let effectiveTemplate =
        input.Template
        |> Option.orElse (pickNotify (fun n -> n.Comment))
    let jobOwner =
        jobConfig.JobOwner
        |> Option.orElseWith (fun () ->
            let dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(input.YamlPath)) |> Option.ofObj |> Option.defaultValue "."
            Codeowners.tryReadLocal deps.FileSystem dir)

    let items : NotifyItem list =
        let issues =
            if input.Target = "issues" || input.Target = "both" then
                lock.Issues |> List.map IssueItem
            else []
        let prs =
            if input.Target = "prs" || input.Target = "both" then
                lock.PullRequests |> List.map PrItem
            else []
        issues @ prs

    let matchesState (filter: string) (liveState: string) =
        match filter with
        | "open"   -> liveState = "OPEN"
        | "closed" -> liveState = "CLOSED" || liveState = "MERGED"
        | _        -> true

    let results =
        items
        |> List.map (fun item ->
            async {
                let repo, issueId, kind =
                    match item with
                    | IssueItem i -> i.Repo, i.Id, "issue"
                    | PrItem p    ->
                        let (PrNumber n) = p.Number
                        p.Repo, IssueId (string n), "pr"
                let (RepoName repoStr) = repo
                let (IssueId num)      = issueId

                let! shouldSkip =
                    if input.State = "all" then async { return false }
                    else
                        async {
                            let! liveState =
                                match item with
                                | IssueItem _ -> client.GetIssueState repo issueId
                                | PrItem p    ->
                                    match providerClients.Prs with
                                    | Some prs -> prs.GetPrState repo p.Number
                                    | None     -> async { return None }
                            return
                                match liveState with
                                | None        -> false
                                | Some s      -> not (matchesState input.State s)
                        }

                if shouldSkip then
                    if input.Verbose then eprintfn "[%s #%s] Filtered by --state %s, skipping" repoStr num input.State
                    return { Repo = repo; Number = num; Kind = kind; Outcome = Skipped }
                else

                if input.DryRun then
                    if input.Verbose then eprintfn "[%s #%s] DRY RUN: would notify" repoStr num
                    return { Repo = repo; Number = num; Kind = kind; Outcome = DryRunWouldNotify }
                else

                match effectiveTemplate with
                | Some tmpl ->
                    do! Comments.postTemplatedComment client providerClients.Repos repo issueId assignTo jobOwner tmpl input.Verbose "notify" input.ExtraVars
                | None ->
                    if input.Verbose then eprintfn "[%s #%s] No notify.comment configured, skipping" repoStr num

                return { Repo = repo; Number = num; Kind = kind; Outcome = Notified }
            })
        |> Async.Parallel
        |> Async.RunSynchronously
        |> Array.toList

    Ok results
