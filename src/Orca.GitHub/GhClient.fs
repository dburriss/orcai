module Orca.GitHub.GhClient

// ---------------------------------------------------------------------------
// Production implementation of Orca.Core.GhClient.IGhClient.
//
// All GitHub API calls are delegated to the `gh` CLI via SimpleExec.
// GH_TOKEN is injected into each subprocess environment by the caller
// (resolved from IAuthContext before construction).
// ---------------------------------------------------------------------------

open System
open System.Text.Json
open SimpleExec
open Orca.Core.Domain
open Orca.Core.GhClient

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
type GhCliClient(ghToken: string) =

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
            match! runGh ghToken $"issue list --repo {repoStr} --state open --json title,number,url,assignees" with
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

    // ------------------------------------------------------------------
    // Pull requests
    // ------------------------------------------------------------------

    /// `gh pr list --repo <org/repo> --state all --json number,url,closingIssuesReferences`
    /// Filters PRs whose closingIssuesReferences contains the given issue number.
    member private _.FindPrsForIssueImpl(repo: RepoName) (issue: IssueNumber) : Async<PullRequestRef list> =
        async {
            let (RepoName repoStr)  = repo
            let (IssueNumber issueN) = issue
            match! runGh ghToken $"pr list --repo {repoStr} --state all --json number,url,closingIssuesReferences" with
            | Error _ -> return []
            | Ok json ->
                let arr = JsonDocument.Parse(json).RootElement
                return
                    arr.EnumerateArray()
                    |> Seq.choose (fun el ->
                        let closingIssues =
                            match el.TryGetProperty("closingIssuesReferences") with
                            | true, refs ->
                                refs.EnumerateArray()
                                |> Seq.choose (fun r -> intProp r "number")
                                |> List.ofSeq
                            | _ -> []
                        if List.contains issueN closingIssues then
                            match intProp el "number", strProp el "url" with
                            | Some n, Some url ->
                                Some { Repo        = repo
                                       Number      = PrNumber n
                                       Url         = url
                                       ClosesIssue = issue }
                            | _ -> None
                        else None)
                    |> List.ofSeq
        }

    // ------------------------------------------------------------------
    // IGhClient interface
    // ------------------------------------------------------------------

    interface IGhClient with
        member this.FindProject org title           = this.FindProjectImpl org title
        member this.FindIssue   repo title          = this.FindIssueImpl repo title
        member this.FindPrsForIssue repo issue      = this.FindPrsForIssueImpl repo issue

        member this.CreateProject org title =
            async {
                let (OrgName orgStr) = org
                match! runGh ghToken $"project create --title \"{title}\" --owner {orgStr} --format json" with
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
                match! runGh ghToken $"project delete {project.Number} --owner {orgStr}" with
                | Error e -> return Error e
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
                    match! runGh ghToken $"issue create --repo {repoStr} --title \"{title}\" --body-file \"{tmpFile}\"{labelPart}" with
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

        member _.CloseIssue repo issue =
            async {
                let (RepoName repoStr)   = repo
                let (IssueNumber issueN) = issue
                match! runGh ghToken $"issue delete {issueN} --repo {repoStr} --yes" with
                | Error e -> return Error e
                | Ok _    -> return Ok ()
            }

        member _.AddIssueToProject project issue =
            async {
                let (OrgName orgStr) = project.Org
                // Idempotent: if the item is already in the project the gh CLI succeeds silently.
                // We intentionally ignore non-zero exits here (e.g. "already exists" variants).
                let! _ = runGh ghToken $"project item-add {project.Number} --owner {orgStr} --url {issue.Url}"
                return Ok ()
            }

        member _.AssignIssue repo issue assignee =
            async {
                let (RepoName repoStr)   = repo
                let (IssueNumber issueN) = issue
                match! runGh ghToken $"issue edit {issueN} --repo {repoStr} --add-assignee {assignee}" with
                | Error e -> return Error e
                | Ok _    -> return Ok ()
            }

        member _.ClosePr repo pr =
            async {
                let (RepoName repoStr) = repo
                let (PrNumber prN)     = pr
                match! runGh ghToken $"pr close {prN} --repo {repoStr}" with
                | Error e -> return Error e
                | Ok _    -> return Ok ()
            }
