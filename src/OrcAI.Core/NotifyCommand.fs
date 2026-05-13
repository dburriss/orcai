module OrcAI.Core.NotifyCommand

// ---------------------------------------------------------------------------
// Implements the `orcai notify` command.
//
// Posts a templated comment to issues and/or PRs in the lock file,
// with optional filtering by target type (issues | prs | both) and
// current GitHub state (open | closed | all).
// ---------------------------------------------------------------------------

open OrcAI.Core.Domain
open OrcAI.Core.GhClient
open OrcAI.Core.Deps

type NotifyInput =
    { YamlPath : string
      DryRun   : bool
      Verbose  : bool
      Target   : string  // "issues" (default) | "prs" | "both"
      State    : string  // "open" (default) | "closed" | "all"
    }

type NotifyOutcome = | Skipped | Notified | DryRunWouldNotify

type NotifyResult =
    { Repo    : RepoName
      Number  : int
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

    let client = deps.GhClient

    let pickNotify f =
        jobConfig.Notify |> Option.bind f
        |> Option.orElse (deps.Config.Notify |> Option.bind f)
    let pickAssign f =
        jobConfig.Assign |> Option.bind f
        |> Option.orElse (deps.Config.Assign |> Option.bind f)

    let assignTo      = pickAssign  (fun a -> a.To)      |> Option.defaultValue "@copilot"
    let notifyComment = pickNotify  (fun n -> n.Comment)
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
                let repo, issueNum, kind =
                    match item with
                    | IssueItem i -> i.Repo, i.Number, "issue"
                    | PrItem p    ->
                        let (PrNumber n) = p.Number
                        p.Repo, IssueNumber n, "pr"
                let (RepoName repoStr)  = repo
                let (IssueNumber num)   = issueNum

                let! shouldSkip =
                    if input.State = "all" then async { return false }
                    else
                        async {
                            let! liveState =
                                match item with
                                | IssueItem _ -> client.GetIssueState repo issueNum
                                | PrItem p    -> client.GetPrState repo p.Number
                            return
                                match liveState with
                                | None        -> false
                                | Some s      -> not (matchesState input.State s)
                        }

                if shouldSkip then
                    if input.Verbose then eprintfn "[%s #%d] Filtered by --state %s, skipping" repoStr num input.State
                    return { Repo = repo; Number = num; Kind = kind; Outcome = Skipped }
                else

                if input.DryRun then
                    if input.Verbose then eprintfn "[%s #%d] DRY RUN: would notify" repoStr num
                    return { Repo = repo; Number = num; Kind = kind; Outcome = DryRunWouldNotify }
                else

                match notifyComment with
                | Some tmpl ->
                    do! Comments.postTemplatedComment client repo issueNum assignTo jobOwner tmpl input.Verbose "notify"
                | None ->
                    if input.Verbose then eprintfn "[%s #%d] No notify.comment configured, skipping" repoStr num

                return { Repo = repo; Number = num; Kind = kind; Outcome = Notified }
            })
        |> Async.Parallel
        |> Async.RunSynchronously
        |> Array.toList

    Ok results
