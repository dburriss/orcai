module OrcAI.GitHub.GhClient

// ---------------------------------------------------------------------------
// Production implementation of OrcAI.Core.GhClient.IGhClient.
//
// All GitHub API calls are delegated to the `gh` CLI via SimpleExec.
// GH_TOKEN is injected into each subprocess environment by the caller
// (resolved from IAuthContext before construction).
// ---------------------------------------------------------------------------

open System
open System.Text.Json
open Microsoft.Extensions.Logging
open SimpleExec
open OrcAI.Core.Domain
open OrcAI.Core.GhClient

// ------------------------------------------------------------------
// Helper: run a gh command and return stdout as a string
// ------------------------------------------------------------------

let private runGh (token: string) (args: string) : Async<Result<string, string>> =
    async {
        try
            let! (stdout, _stderr) =
                Command.ReadAsync(
                    "gh", args,
                    configureEnvironment = Action<Collections.Generic.IDictionary<string,string>>(fun env ->
                        env.["GH_TOKEN"] <- token))
                |> Async.AwaitTask
            return Ok (stdout.Trim())
        with
        | :? ExitCodeException as ex ->
            return Error $"gh exited with code {ex.ExitCode}: {ex.Message}"
        | ex ->
            return Error $"Failed to run 'gh {args}': {ex.Message}"
    }

// ------------------------------------------------------------------
// Rate limiting and retry helpers
// ------------------------------------------------------------------

let private isRateLimit (msg: string) =
    msg.Contains("API rate limit exceeded", StringComparison.OrdinalIgnoreCase)
    || msg.Contains("secondary rate limit", StringComparison.OrdinalIgnoreCase)
    || msg.Contains("abuse detection mechanism", StringComparison.OrdinalIgnoreCase)
    || msg.Contains("was submitted too quickly", StringComparison.OrdinalIgnoreCase)

let private withRetry maxAttempts (run: unit -> Async<Result<'a, string>>) : Async<Result<'a, string>> =
    let rec loop attempt (delay: int) = async {
        let! result = run()
        match result with
        | Error msg when isRateLimit msg && attempt < maxAttempts ->
            do! Async.Sleep delay
            return! loop (attempt + 1) (min (delay * 2) 300_000)
        | other -> return other
    }
    loop 1 60_000  // first retry after 60s, doubles each time, capped at 5min

type private WriteBucket(perMinuteCap: int) =
    let mutable tokens = perMinuteCap
    let mutable lastRefill = DateTime.UtcNow
    let gate = obj()

    member _.Acquire() =
        lock gate (fun () ->
            let now = DateTime.UtcNow
            let elapsed = (now - lastRefill).TotalSeconds
            let refilled = int (elapsed * float perMinuteCap / 60.0)
            if refilled > 0 then
                tokens <- min perMinuteCap (tokens + refilled)
                lastRefill <- now
            if tokens > 0 then
                tokens <- tokens - 1
                0  // no wait needed
            else
                int (60_000.0 / float perMinuteCap) + 1)  // ms until next token

// Like runGh but acquires a write token first and retries on rate-limit errors.
let private runGhWrite (bucket: WriteBucket) (retries: int) (token: string) (args: string) : Async<Result<string, string>> =
    withRetry retries (fun () -> async {
        let waitMs = bucket.Acquire()
        if waitMs > 0 then do! Async.Sleep waitMs
        return! runGh token args
    })

// ------------------------------------------------------------------
// JSON helpers for parsing gh CLI output
// ------------------------------------------------------------------

/// Try to get a named string property from a JsonElement.
let private strProp (el: JsonElement) (name: string) =
    match el.TryGetProperty(name) with
    | true, v -> v.GetString() |> Option.ofObj
    | _       -> None

/// Try to get a named int property from a JsonElement.
let private intProp (el: JsonElement) (name: string) =
    match el.TryGetProperty(name) with
    | true, v ->
        match v.ValueKind with
        | JsonValueKind.Number -> Some (v.GetInt32())
        | _                    -> None
    | _ -> None

// ------------------------------------------------------------------
// Production GhCliClient
// ------------------------------------------------------------------

/// Production implementation that shells out to `gh` via SimpleExec.
type GhCliClient(ghToken: string, writesPerMinute: int, rateLimitRetries: int, logger: ILogger) =
    let bucket  = WriteBucket(writesPerMinute)
    let retries = rateLimitRetries

    // ------------------------------------------------------------------
    // Projects
    // ------------------------------------------------------------------

    /// `gh project list --owner <org> --format json`
    /// Returns the JSON shape:  { "projects": [ { "number": 1, "title": "..." } ] }
    member private _.FindProjectImpl(org: OrgName) (title: string) : Async<ProjectInfo option> =
        async {
            let (OrgName orgStr) = org
            match! runGh ghToken $"project list --owner {orgStr} --format json" with
            | Error _ -> return None
            | Ok json ->
                let doc = JsonDocument.Parse(json)
                let projects =
                    match doc.RootElement.TryGetProperty("projects") with
                    | true, arr -> arr.EnumerateArray() |> Seq.toList
                    | _         -> []
                return
                    projects
                    |> List.tryFind (fun el ->
                        strProp el "title" = Some title)
                    |> Option.bind (fun el ->
                        match intProp el "number", strProp el "url" with
                        | Some n, Some url ->
                            Some { Org    = org
                                   Number = n
                                   Title  = title
                                   Url    = url }
                        | _ -> None)
        }

    // ------------------------------------------------------------------
    // Issues
    // ------------------------------------------------------------------

    /// `gh issue list --repo <org/repo> --state open --json title,number,url,assignees`
    member private _.FindIssueImpl(repo: RepoName) (title: string) : Async<IssueRef option> =
        async {
            let (RepoName repoStr) = repo
            match! runGh ghToken $"issue list --repo {repoStr} --state open --search \"{title} in:title\" --limit 100 --json title,number,url,assignees" with
            | Error _ -> return None
            | Ok json ->
                let arr = JsonDocument.Parse(json).RootElement
                return
                    arr.EnumerateArray()
                    |> Seq.tryFind (fun el ->
                        strProp el "title" = Some title)
                    |> Option.bind (fun el ->
                        match intProp el "number", strProp el "url" with
                        | Some n, Some url ->
                            let assignees =
                                match el.TryGetProperty("assignees") with
                                | true, arr ->
                                    arr.EnumerateArray()
                                    |> Seq.choose (fun a -> strProp a "login")
                                    |> List.ofSeq
                                | _ -> []
                            Some { Repo      = repo
                                   Number    = IssueNumber n
                                   Url       = url
                                   Assignees = assignees }
                        | _ -> None)
        }

    /// `gh issue list --repo <org/repo> --state closed --json title,number,url,assignees`
    member private _.FindClosedIssueImpl(repo: RepoName) (title: string) : Async<IssueRef option> =
        async {
            let (RepoName repoStr) = repo
            match! runGh ghToken $"issue list --repo {repoStr} --state closed --search \"{title} in:title\" --limit 100 --json title,number,url,assignees" with
            | Error _ -> return None
            | Ok json ->
                let arr = JsonDocument.Parse(json).RootElement
                return
                    arr.EnumerateArray()
                    |> Seq.tryFind (fun el ->
                        strProp el "title" = Some title)
                    |> Option.bind (fun el ->
                        match intProp el "number", strProp el "url" with
                        | Some n, Some url ->
                            let assignees =
                                match el.TryGetProperty("assignees") with
                                | true, arr ->
                                    arr.EnumerateArray()
                                    |> Seq.choose (fun a -> strProp a "login")
                                    |> List.ofSeq
                                | _ -> []
                            Some { Repo      = repo
                                   Number    = IssueNumber n
                                   Url       = url
                                   Assignees = assignees }
                        | _ -> None)
        }

    /// Reopen a closed issue and return the refreshed IssueRef.
    member private _.ReopenIssueImpl(repo: RepoName) (issue: IssueNumber) : Async<Result<IssueRef, string>> =
        async {
            let (RepoName repoStr)   = repo
            let (IssueNumber issueN) = issue
            match! runGhWrite bucket retries ghToken $"issue reopen {issueN} --repo {repoStr}" with
            | Error e -> return Error e
            | Ok _ ->
                match! runGh ghToken $"issue view {issueN} --repo {repoStr} --json number,url,assignees" with
                | Error e -> return Error e
                | Ok json ->
                    let el = JsonDocument.Parse(json).RootElement
                    match intProp el "number", strProp el "url" with
                    | Some n, Some url ->
                        let assignees =
                            match el.TryGetProperty("assignees") with
                            | true, arr ->
                                arr.EnumerateArray()
                                |> Seq.choose (fun a -> strProp a "login")
                                |> List.ofSeq
                            | _ -> []
                        return Ok { Repo      = repo
                                    Number    = IssueNumber n
                                    Url       = url
                                    Assignees = assignees }
                    | _ ->
                        return Error $"Could not parse issue view response for issue #{issueN} in {repoStr}"
        }

    // ------------------------------------------------------------------
    // Pull requests
    // ------------------------------------------------------------------

    /// GraphQL query against Issue.closingPullRequests so we never hit a PR-list cap.
    member private _.FindPrsForIssueImpl(repo: RepoName) (issue: IssueNumber) : Async<PullRequestRef list> =
        async {
            let (RepoName repoStr)   = repo
            let (IssueNumber issueN) = issue
            let parts    = repoStr.Split('/', 2)
            let owner    = parts.[0]
            let repoName = parts.[1]
            let query    = "query($owner:String!,$repo:String!,$issue:Int!){repository(owner:$owner,name:$repo){issue(number:$issue){closingPullRequests(first:25){nodes{number url}}}}}"
            match! runGh ghToken $"api graphql -f \"query={query}\" -f owner={owner} -f repo={repoName} -F issue={issueN}" with
            | Error _ -> return []
            | Ok json ->
                let doc = JsonDocument.Parse(json).RootElement
                let nodes =
                    match doc.TryGetProperty("data") with
                    | true, data ->
                        match data.TryGetProperty("repository") with
                        | true, repoEl ->
                            match repoEl.TryGetProperty("issue") with
                            | true, issueEl when issueEl.ValueKind <> JsonValueKind.Null ->
                                match issueEl.TryGetProperty("closingPullRequests") with
                                | true, prs ->
                                    match prs.TryGetProperty("nodes") with
                                    | true, ns -> ns.EnumerateArray() |> Seq.toList
                                    | _        -> []
                                | _ -> []
                            | _ -> []
                        | _ -> []
                    | _ -> []
                return
                    nodes
                    |> List.choose (fun el ->
                        match intProp el "number", strProp el "url" with
                        | Some n, Some url ->
                            Some { Repo        = repo
                                   Number      = PrNumber n
                                   Url         = url
                                   ClosesIssue = issue }
                        | _ -> None)
        }

    // ------------------------------------------------------------------
    // IGhClient interface
    // ------------------------------------------------------------------

    interface IGhClient with
        member this.FindProject      org title       = this.FindProjectImpl org title
        member this.FindIssue        repo title      = this.FindIssueImpl repo title
        member this.FindClosedIssue  repo title      = this.FindClosedIssueImpl repo title
        member this.ReopenIssue      repo issue      = this.ReopenIssueImpl repo issue
        member this.FindPrsForIssue  repo issue      = this.FindPrsForIssueImpl repo issue

        member _.ListLabels repo =
            async {
                let (RepoName repoStr) = repo
                match! runGh ghToken $"label list --repo {repoStr} --json name" with
                | Error e -> return Error e
                | Ok json ->
                    let arr = JsonDocument.Parse(json).RootElement
                    let names =
                        arr.EnumerateArray()
                        |> Seq.choose (fun el -> strProp el "name")
                        |> List.ofSeq
                    return Ok names
            }

        member _.CreateLabel repo name =
            async {
                let (RepoName repoStr) = repo
                match! runGhWrite bucket retries ghToken $"label create \"{name}\" --repo {repoStr}" with
                | Error e -> return Error e
                | Ok _    -> return Ok ()
            }

        member this.CreateProject org title =
            async {
                let (OrgName orgStr) = org
                match! runGhWrite bucket retries ghToken $"project create --title \"{title}\" --owner {orgStr} --format json" with
                | Error e -> return Error e
                | Ok json ->
                    let el = JsonDocument.Parse(json).RootElement
                    match intProp el "number", strProp el "url" with
                    | Some n, Some url ->
                        return Ok { Org = org; Number = n; Title = title; Url = url }
                    | _ ->
                        return Error $"Unexpected response from 'gh project create': {json}"
            }

        member _.DeleteProject project =
            async {
                let (OrgName orgStr) = project.Org
                match! runGhWrite bucket retries ghToken $"project delete {project.Number} --owner {orgStr}" with
                | Error e ->
                    if e.Contains("Could not resolve to a ProjectV2") then
                        logger.LogWarning("Project #{ProjectNumber} in org '{Org}' not found — already deleted.", project.Number, orgStr)
                        return Ok ()
                    else
                        return Error e
                | Ok _    -> return Ok ()
            }

        member this.CreateIssue repo title body labels =
            async {
                let (RepoName repoStr) = repo
                // Write body to a temp file to avoid shell quoting issues
                let tmpFile = System.IO.Path.GetTempFileName()
                try
                    System.IO.File.WriteAllText(tmpFile, body)
                    let labelPart =
                        if List.isEmpty labels then ""
                        else
                            let flags = labels |> List.map (fun l -> $"--label \"{l}\"") |> String.concat " "
                            $" {flags}"
                    // gh issue create outputs the issue URL as plain text (no --json support)
                    match! runGhWrite bucket retries ghToken $"issue create --repo {repoStr} --title \"{title}\" --body-file \"{tmpFile}\"{labelPart}" with
                    | Error e -> return Error e
                    | Ok url ->
                        // URL format: https://github.com/owner/repo/issues/123
                        let url = url.Trim()
                        let lastSegment = url.Split('/') |> Array.last
                        match System.Int32.TryParse(lastSegment) with
                        | true, n ->
                            return Ok { Repo      = repo
                                        Number    = IssueNumber n
                                        Url       = url
                                        Assignees = [] }
                        | _ ->
                            return Error $"Could not parse issue number from 'gh issue create' output: {url}"
                finally
                    System.IO.File.Delete(tmpFile)
            }

        member _.DeleteIssue repo issue =
            async {
                let (RepoName repoStr)   = repo
                let (IssueNumber issueN) = issue
                match! runGhWrite bucket retries ghToken $"issue delete {issueN} --repo {repoStr} --yes" with
                | Ok _    -> return Ok ()
                | Error e ->
                    if e.Contains("Could not resolve to an issue or pull request") then
                        logger.LogWarning("Issue #{IssueNumber} in repo '{Repo}' not found — already deleted.", issueN, repoStr)
                        return Ok ()
                    else
                        return Error e
            }

        member _.AddIssueToProject project issue =
            async {
                let (OrgName orgStr) = project.Org
                // Idempotent: if the item is already in the project the gh CLI succeeds silently.
                // We intentionally ignore non-zero exits here (e.g. "already exists" variants).
                let! _ = runGhWrite bucket retries ghToken $"project item-add {project.Number} --owner {orgStr} --url {issue.Url}"
                return Ok ()
            }

        member _.AssignIssue repo issue assignee =
            async {
                let (RepoName repoStr)   = repo
                let (IssueNumber issueN) = issue
                match! runGhWrite bucket retries ghToken $"issue edit {issueN} --repo {repoStr} --add-assignee {assignee}" with
                | Error e -> return Error e
                | Ok _    -> return Ok ()
            }

        member _.ClosePr repo pr =
            async {
                let (RepoName repoStr) = repo
                let (PrNumber prN)     = pr
                match! runGhWrite bucket retries ghToken $"pr close {prN} --repo {repoStr}" with
                | Error e ->
                    if e.Contains("Could not resolve to a PullRequest") then
                        logger.LogWarning("PR #{PrNumber} in repo '{Repo}' not found — already closed/deleted.", prN, repoStr)
                        return Ok ()
                    else
                        return Error e
                | Ok _    -> return Ok ()
            }

        member _.ListRepos org =
            async {
                let (OrgName orgStr) = org
                match! runGh ghToken $"repo list {orgStr} --json name --limit 1000" with
                | Error e -> return Error e
                | Ok json ->
                    let arr = JsonDocument.Parse(json).RootElement
                    let names =
                        arr.EnumerateArray()
                        |> Seq.choose (fun el -> strProp el "name")
                        |> List.ofSeq
                    return Ok names
            }

        member _.RepoExists repo =
            async {
                let (RepoName repoStr) = repo
                match! runGh ghToken $"repo view {repoStr} --json name" with
                | Ok _    -> return Ok ()
                | Error e -> return Error e
            }
