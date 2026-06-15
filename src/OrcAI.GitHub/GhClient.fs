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
        | :? ExitCodeReadException as ex ->
            let stderr = ex.StandardError.Trim()
            let msg =
                if stderr.Length > 0 then stderr
                else $"gh exited with code {ex.ExitCode}"
            return Error msg
        | :? ExitCodeException as ex ->
            return Error $"gh exited with code {ex.ExitCode}"
        | ex ->
            return Error $"failed to run 'gh {args}': {ex.Message}"
    }

// GraphQL responses with partial failures exit with code 1 but still contain
// valid JSON in stdout. This variant recovers stdout from the exception so the
// caller can parse data+errors from a single response.
// Async.AwaitTask may wrap the inner exception in AggregateException, so we
// unwrap one level before checking for ExitCodeReadException.
let private runGhGraphQL (token: string) (args: string) : Async<Result<string, string>> =
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
        | :? ExitCodeReadException as ex ->
            let stdout = ex.StandardOutput.Trim()
            if stdout.Length > 0 then return Ok stdout
            else
                let stderr = ex.StandardError.Trim()
                return Error (if stderr.Length > 0 then stderr else $"gh exited with code {ex.ExitCode}")
        | :? AggregateException as aex ->
            let found = aex.Flatten().InnerExceptions |> Seq.tryFind (fun e -> e :? ExitCodeReadException)
            match found with
            | Some (:? ExitCodeReadException as ex) ->
                let stdout = ex.StandardOutput.Trim()
                if stdout.Length > 0 then return Ok stdout
                else
                    let stderr = ex.StandardError.Trim()
                    return Error (if stderr.Length > 0 then stderr else $"gh exited with code {ex.ExitCode}")
            | _ ->
                return Error $"failed to run 'gh {args}': {aex.Message}"
        | :? ExitCodeException as ex ->
            return Error $"gh exited with code {ex.ExitCode}"
        | ex ->
            return Error $"failed to run 'gh {args}': {ex.Message}"
    }

// ------------------------------------------------------------------
// Rate limiting and retry helpers
// ------------------------------------------------------------------

let internal isRateLimit (msg: string) =
    msg.Contains("API rate limit exceeded", StringComparison.OrdinalIgnoreCase)
    || msg.Contains("secondary rate limit", StringComparison.OrdinalIgnoreCase)
    || msg.Contains("abuse detection mechanism", StringComparison.OrdinalIgnoreCase)
    || msg.Contains("was submitted too quickly", StringComparison.OrdinalIgnoreCase)

let internal isTransient (msg: string) =
    let m = msg.ToLowerInvariant()
    [ "i/o timeout"; "connection refused"; "connection reset"
      "no such host"; "tls handshake"; "remote end closed"
      "502 bad gateway"; "503 service unavailable"; "504 gateway timeout" ]
    |> List.exists m.Contains

// ±25% jitter on a delay, with a floor so a degenerate negative can't become 0.
let internal jitter (ms: int) =
    let delta = max 1 (ms / 4)
    ms + System.Random.Shared.Next(-delta, delta + 1)

// Two independent backoff schedules (rate-limit vs transient) sharing one
// attempt budget. Initial delays are parameters so tests don't have to sleep
// the production defaults.
let internal withRetryDelays
        (maxAttempts: int)
        (initialRl: int)
        (initialTx: int)
        (run: unit -> Async<Result<'a, string>>)
        : Async<Result<'a, string>> =
    let rec loop attempt (rlDelay: int) (txDelay: int) = async {
        let! result = run()
        match result with
        | Error msg when isRateLimit msg && attempt < maxAttempts ->
            do! Async.Sleep (max 500 (jitter rlDelay))
            return! loop (attempt + 1) (min (rlDelay * 2) 300_000) txDelay
        | Error msg when isTransient msg && attempt < maxAttempts ->
            do! Async.Sleep (max 500 (jitter txDelay))
            return! loop (attempt + 1) rlDelay (min (txDelay * 2) 30_000)
        | other -> return other
    }
    loop 1 initialRl initialTx

let private withRetry maxAttempts run = withRetryDelays maxAttempts 60_000 2_000 run

type internal ApiBucket(perMinuteCap: int, getNow: unit -> DateTime) =
    // Start at 80% of capacity instead of 100% — a full bucket lets the first
    // ~perMinuteCap calls bypass pacing entirely; 80% leaves a small warm-up
    // burst without effectively disabling the throttle for the first minute.
    let mutable tokens = perMinuteCap * 4 / 5
    let mutable lastRefill = getNow()
    let gate = obj()

    new(perMinuteCap: int) = ApiBucket(perMinuteCap, fun () -> DateTime.UtcNow)

    member _.Acquire() =
        lock gate (fun () ->
            let now = getNow()
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

// Like runGh but acquires a token from the shared bucket first and retries on
// rate-limit and transient errors. Loops on Acquire() so concurrent callers
// each wait for their own token instead of stampeding after a single shared
// sleep.
let private runGhApi (bucket: ApiBucket) (retries: int) (token: string) (args: string) : Async<Result<string, string>> =
    withRetry retries (fun () -> async {
        let rec waitForToken () = async {
            let waitMs = bucket.Acquire()
            if waitMs > 0 then
                do! Async.Sleep waitMs
                return! waitForToken ()
        }
        do! waitForToken ()
        return! runGh token args
    })

let private runGhApiGraphQL (bucket: ApiBucket) (retries: int) (token: string) (args: string) : Async<Result<string, string>> =
    withRetry retries (fun () -> async {
        let rec waitForToken () = async {
            let waitMs = bucket.Acquire()
            if waitMs > 0 then
                do! Async.Sleep waitMs
                return! waitForToken ()
        }
        do! waitForToken ()
        return! runGhGraphQL token args
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
    let bucket  = ApiBucket(writesPerMinute)
    let retries = rateLimitRetries

    // ------------------------------------------------------------------
    // Projects
    // ------------------------------------------------------------------

    /// `gh project list --owner <org> --format json`
    /// Returns the JSON shape:  { "projects": [ { "number": 1, "title": "..." } ] }
    member private _.FindProjectImpl(org: OrgName) (title: string) : Async<ProjectInfo option> =
        async {
            let (OrgName orgStr) = org
            match! runGhApi bucket retries ghToken $"project list --owner {orgStr} --format json" with
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
    member private _.FindIssueImpl(repo: RepoName) (title: string) : Async<Result<IssueRef option, string>> =
        async {
            let (RepoName repoStr) = repo
            match! runGhApi bucket retries ghToken $"issue list --repo {repoStr} --state open --search \"{title} in:title\" --limit 100 --json title,number,url,assignees" with
            | Error e -> return Error e
            | Ok json ->
                let arr = JsonDocument.Parse(json).RootElement
                return Ok (
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
                        | _ -> None))
        }

    /// `gh issue list --repo <org/repo> --state closed --json title,number,url,assignees`
    member private _.FindClosedIssueImpl(repo: RepoName) (title: string) : Async<Result<IssueRef option, string>> =
        async {
            let (RepoName repoStr) = repo
            match! runGhApi bucket retries ghToken $"issue list --repo {repoStr} --state closed --search \"{title} in:title\" --limit 100 --json title,number,url,assignees" with
            | Error e -> return Error e
            | Ok json ->
                let arr = JsonDocument.Parse(json).RootElement
                return Ok (
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
                        | _ -> None))
        }

    /// Reopen a closed issue and return the refreshed IssueRef.
    member private _.ReopenIssueImpl(repo: RepoName) (issue: IssueNumber) : Async<Result<IssueRef, string>> =
        async {
            let (RepoName repoStr)   = repo
            let (IssueNumber issueN) = issue
            match! runGhApi bucket retries ghToken $"issue reopen {issueN} --repo {repoStr}" with
            | Error e -> return Error e
            | Ok _ ->
                match! runGhApi bucket retries ghToken $"issue view {issueN} --repo {repoStr} --json number,url,assignees" with
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

    /// Look up PRs that reference an issue. Combines closingPullRequests (linked
    /// via "fixes #N"/"closes #N") with cross-referenced PRs from the issue
    /// timeline so we also catch Copilot-authored PRs that omit a closing
    /// keyword. Results are deduplicated by PR number.
    member private _.FindPrsForIssueImpl(repo: RepoName) (issue: IssueNumber) : Async<PullRequestRef list> =
        async {
            let (RepoName repoStr)   = repo
            let (IssueNumber issueN) = issue
            let parts    = repoStr.Split('/', 2)
            let owner    = parts.[0]
            let repoName = parts.[1]
            let query =
                "query($owner:String!,$repo:String!,$issue:Int!){repository(owner:$owner,name:$repo){issue(number:$issue){"
                + "closingPullRequests(first:25){nodes{number url state}}"
                + "timelineItems(itemTypes:[CROSS_REFERENCED_EVENT],first:50){nodes{... on CrossReferencedEvent{source{... on PullRequest{number url state}}}}}"
                + "}}}"
            match! runGhApi bucket retries ghToken $"api graphql -f \"query={query}\" -f owner={owner} -f repo={repoName} -F issue={issueN}" with
            | Error _ -> return []
            | Ok json ->
                let doc = JsonDocument.Parse(json).RootElement
                let issueEl =
                    match doc.TryGetProperty("data") with
                    | true, data ->
                        match data.TryGetProperty("repository") with
                        | true, repoEl ->
                            match repoEl.TryGetProperty("issue") with
                            | true, ie when ie.ValueKind <> JsonValueKind.Null -> Some ie
                            | _ -> None
                        | _ -> None
                    | _ -> None
                let closingNodes =
                    match issueEl with
                    | Some ie ->
                        match ie.TryGetProperty("closingPullRequests") with
                        | true, prs ->
                            match prs.TryGetProperty("nodes") with
                            | true, ns -> ns.EnumerateArray() |> Seq.toList
                            | _        -> []
                        | _ -> []
                    | None -> []
                let crossRefNodes =
                    match issueEl with
                    | Some ie ->
                        match ie.TryGetProperty("timelineItems") with
                        | true, ti ->
                            match ti.TryGetProperty("nodes") with
                            | true, ns ->
                                ns.EnumerateArray()
                                |> Seq.choose (fun el ->
                                    match el.TryGetProperty("source") with
                                    | true, src when src.ValueKind = JsonValueKind.Object
                                                     && (match src.TryGetProperty("number") with true, _ -> true | _ -> false) ->
                                        Some src
                                    | _ -> None)
                                |> Seq.toList
                            | _ -> []
                        | _ -> []
                    | None -> []
                let toRef (el: JsonElement) =
                    match intProp el "number", strProp el "url" with
                    | Some n, Some url ->
                        Some { Repo        = repo
                               Number      = PrNumber n
                               Url         = url
                               ClosesIssue = issue }
                    | _ -> None
                return
                    (closingNodes @ crossRefNodes)
                    |> List.choose toRef
                    |> List.distinctBy (fun pr -> pr.Number)
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
                match! runGhApi bucket retries ghToken $"label list --repo {repoStr} --json name --limit 1000" with
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
                match! runGhApi bucket retries ghToken $"label create \"{name}\" --repo {repoStr}" with
                | Ok _    -> return Ok ()
                | Error e ->
                    if e.Contains("already exists", StringComparison.OrdinalIgnoreCase)
                       || e.Contains("already been taken", StringComparison.OrdinalIgnoreCase) then
                        logger.LogWarning("Label '{Label}' already exists in repo '{Repo}' — treating as success.", name, repoStr)
                        return Ok ()
                    else
                        return Error e
            }

        member this.CreateProject org title =
            async {
                let (OrgName orgStr) = org
                match! runGhApi bucket retries ghToken $"project create --title \"{title}\" --owner {orgStr} --format json" with
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
                match! runGhApi bucket retries ghToken $"project delete {project.Number} --owner {orgStr}" with
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
                    match! runGhApi bucket retries ghToken $"issue create --repo {repoStr} --title \"{title}\" --body-file \"{tmpFile}\"{labelPart}" with
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

        member _.UpdateIssue repo issue title body =
            async {
                let (RepoName repoStr)   = repo
                let (IssueNumber issueN) = issue
                let tmpFile = System.IO.Path.GetTempFileName()
                try
                    System.IO.File.WriteAllText(tmpFile, body)
                    match! runGhApi bucket retries ghToken $"issue edit {issueN} --repo {repoStr} --title \"{title}\" --body-file \"{tmpFile}\"" with
                    | Ok _    -> return Ok ()
                    | Error e -> return Error e
                finally
                    System.IO.File.Delete(tmpFile)
            }

        member _.DeleteIssue repo issue =
            async {
                let (RepoName repoStr)   = repo
                let (IssueNumber issueN) = issue
                match! runGhApi bucket retries ghToken $"issue delete {issueN} --repo {repoStr} --yes" with
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
                match! runGhApi bucket retries ghToken $"project item-add {project.Number} --owner {orgStr} --url {issue.Url}" with
                | Ok _    -> return Ok ()
                | Error e -> return Error e
            }

        member _.AssignIssue repo issue assignee =
            async {
                let (RepoName repoStr)   = repo
                let (IssueNumber issueN) = issue
                match! runGhApi bucket retries ghToken $"issue edit {issueN} --repo {repoStr} --add-assignee {assignee}" with
                | Error e -> return Error e
                | Ok _    -> return Ok ()
            }

        member _.UnassignIssue repo issue assignee =
            async {
                let (RepoName repoStr)   = repo
                let (IssueNumber issueN) = issue
                match! runGhApi bucket retries ghToken $"issue edit {issueN} --repo {repoStr} --remove-assignee {assignee}" with
                | Error e -> return Error e
                | Ok _    -> return Ok ()
            }

        member _.PostComment repo issue body =
            async {
                let (RepoName repoStr)   = repo
                let (IssueNumber issueN) = issue
                let tmpFile = System.IO.Path.GetTempFileName()
                try
                    System.IO.File.WriteAllText(tmpFile, body)
                    match! runGhApi bucket retries ghToken $"issue comment {issueN} --repo {repoStr} --body-file \"{tmpFile}\"" with
                    | Error e -> return Error e
                    | Ok _    -> return Ok ()
                finally
                    System.IO.File.Delete(tmpFile)
            }

        member _.ClosePr repo pr =
            async {
                let (RepoName repoStr) = repo
                let (PrNumber prN)     = pr
                match! runGhApi bucket retries ghToken $"pr close {prN} --repo {repoStr}" with
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
                match! runGhApi bucket retries ghToken $"repo list {orgStr} --json name --limit 1000" with
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
                match! runGhApi bucket retries ghToken $"repo view {repoStr} --json name" with
                | Ok _    -> return Ok ()
                | Error e -> return Error e
            }

        member _.ReposExist repos =
            async {
                if List.isEmpty repos then
                    return Map.empty
                else

                let results = System.Collections.Generic.Dictionary<RepoName, Result<unit, string>>()

                for chunk in List.chunkBySize 100 repos do
                    // Build one aliased field per repo: r0: repository(owner:"o",name:"n"){id}
                    let fields =
                        chunk
                        |> List.mapi (fun i (RepoName r) ->
                            let parts = r.Split('/', 2)
                            $"r{i}: repository(owner:\"{parts.[0]}\",name:\"{parts.[1]}\"){{id}}")
                        |> String.concat " "
                    let query = $"{{ {fields} }}"

                    let tmpFile = System.IO.Path.GetTempFileName()
                    try
                        System.IO.File.WriteAllText(tmpFile, JsonSerializer.Serialize({| query = query |}))
                        match! runGhApiGraphQL bucket retries ghToken $"api graphql --input \"{tmpFile}\"" with
                        | Error e ->
                            for repo in chunk do results.[repo] <- Error e
                        | Ok json ->
                            let doc  = JsonDocument.Parse(json).RootElement
                            let data =
                                match doc.TryGetProperty("data") with
                                | true, d -> Some d
                                | _       -> None
                            // Collect error messages keyed by alias ("r0", "r1", …)
                            let errorsByAlias =
                                match doc.TryGetProperty("errors") with
                                | true, arr ->
                                    arr.EnumerateArray()
                                    |> Seq.choose (fun err ->
                                        let alias =
                                            match err.TryGetProperty("path") with
                                            | true, p -> p.EnumerateArray() |> Seq.tryHead |> Option.map (fun e -> e.GetString())
                                            | _ -> None
                                        let msg =
                                            match err.TryGetProperty("message") with
                                            | true, m -> m.GetString()
                                            | _ -> "inaccessible"
                                        alias |> Option.map (fun a -> a, msg))
                                    |> Map.ofSeq
                                | _ -> Map.empty
                            chunk |> List.iteri (fun i repo ->
                                let alias = $"r{i}"
                                let isNull =
                                    match data with
                                    | Some d ->
                                        match d.TryGetProperty(alias) with
                                        | true, v -> v.ValueKind = JsonValueKind.Null
                                        | _       -> true
                                    | None -> true
                                results.[repo] <-
                                    if isNull then
                                        Error (Map.tryFind alias errorsByAlias |> Option.defaultValue "not found or inaccessible")
                                    else Ok ())
                    finally
                        if System.IO.File.Exists(tmpFile) then System.IO.File.Delete(tmpFile)

                return results |> Seq.map (fun kv -> kv.Key, kv.Value) |> Map.ofSeq
            }

        member _.IsArchived repo =
            async {
                let (RepoName repoStr) = repo
                match! runGhApi bucket retries ghToken $"repo view {repoStr} --json isArchived" with
                | Error e -> return Error e
                | Ok json ->
                    let el = JsonDocument.Parse(json).RootElement
                    match el.TryGetProperty("isArchived") with
                    | true, v when v.ValueKind = JsonValueKind.True  -> return Ok true
                    | true, _                                        -> return Ok false
                    | _ -> return Error $"Missing 'isArchived' field in response for {repoStr}"
            }

        member _.FetchCodeowners repo =
            async {
                let (RepoName r) = repo
                let paths = [ "CODEOWNERS"; ".github/CODEOWNERS"; "docs/CODEOWNERS" ]
                let rec tryPaths = function
                    | [] -> async { return None }
                    | (p: string) :: rest ->
                        async {
                            match! runGhApi bucket retries ghToken $"api repos/{r}/contents/{p}" with
                            | Error _ -> return! tryPaths rest
                            | Ok json ->
                                let el = JsonDocument.Parse(json).RootElement
                                match strProp el "content", strProp el "encoding" with
                                | Some b64, Some "base64" ->
                                    let cleaned = b64.Replace("\n", "").Replace("\r", "")
                                    let bytes = Convert.FromBase64String(cleaned)
                                    return Some (Text.Encoding.UTF8.GetString(bytes))
                                | _ -> return! tryPaths rest
                        }
                return! tryPaths paths
            }

        member _.GetIssueState repo issue =
            async {
                let (RepoName repoStr)   = repo
                let (IssueNumber issueN) = issue
                match! runGhApi bucket retries ghToken $"issue view {issueN} --repo {repoStr} --json state" with
                | Error _ -> return None
                | Ok json ->
                    let el = JsonDocument.Parse(json).RootElement
                    return strProp el "state"
            }

        member _.GetPrState repo pr =
            async {
                let (RepoName repoStr) = repo
                let (PrNumber prN)     = pr
                match! runGhApi bucket retries ghToken $"pr view {prN} --repo {repoStr} --json state" with
                | Error _ -> return None
                | Ok json ->
                    let el = JsonDocument.Parse(json).RootElement
                    return strProp el "state"
            }
