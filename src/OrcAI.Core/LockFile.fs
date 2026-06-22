module OrcAI.Core.LockFile

open System
open System.Text.Json
open System.Text.Json.Serialization
open System.IO.Abstractions
open OrcAI.Core.Domain

// ---------------------------------------------------------------------------
// Reads and writes the JSON lock file that sits alongside the YAML config.
// Lock file path convention: <yaml-basename>.lock.json
//
// Single-case DUs (OrgName, RepoName, IssueNumber, PrNumber) are unwrapped
// to primitives in a DTO layer so the JSON is human-readable.
// ---------------------------------------------------------------------------

// ------------------------------------------------------------------
// JSON DTO types — plain records with primitive fields
// ------------------------------------------------------------------

[<CLIMutable>]
type ProjectInfoDto =
    { [<JsonPropertyName("org")>]    org:    string
      [<JsonPropertyName("number")>] number: int
      [<JsonPropertyName("title")>]  title:  string
      [<JsonPropertyName("url")>]    url:    string }

[<CLIMutable>]
type IssueRefDto =
    { [<JsonPropertyName("repo")>]      repo:      string
      [<JsonPropertyName("number")>]    number:    int
      [<JsonPropertyName("url")>]       url:       string
      [<JsonPropertyName("assignees")>] assignees: string[] }

[<CLIMutable>]
type PullRequestRefDto =
    { [<JsonPropertyName("repo")>]        repo:        string
      [<JsonPropertyName("number")>]      number:      int
      [<JsonPropertyName("url")>]         url:         string
      [<JsonPropertyName("closesIssue")>] closesIssue: int
      [<JsonPropertyName("state")>]       state:       string }

[<CLIMutable>]
type RepoFailureDto =
    { [<JsonPropertyName("repo")>]          repo:          string
      [<JsonPropertyName("category")>]      category:      string
      [<JsonPropertyName("cause")>]         cause:         string
      [<JsonPropertyName("attempts")>]      attempts:      int
      [<JsonPropertyName("firstFailedAt")>] firstFailedAt: string
      [<JsonPropertyName("lastFailedAt")>]  lastFailedAt:  string
      [<JsonPropertyName("lastMessage")>]   lastMessage:   string }

[<CLIMutable>]
type LockFileDto =
    { [<JsonPropertyName("lockedAt")>]      lockedAt:     string
      [<JsonPropertyName("yamlHash")>]      yamlHash:     string
      [<JsonPropertyName("templateHash")>]  templateHash: string
      [<JsonPropertyName("project")>]       project:      ProjectInfoDto
      [<JsonPropertyName("repos")>]         repos:        string[]
      [<JsonPropertyName("issues")>]        issues:       IssueRefDto[]
      [<JsonPropertyName("pullRequests")>]  pullRequests: PullRequestRefDto[]
      [<JsonPropertyName("skippedRepos")>]  skippedRepos: string[]
      [<JsonPropertyName("failures")>]      failures:     RepoFailureDto[] }

// ------------------------------------------------------------------
// JSON serialiser options
// ------------------------------------------------------------------

let private jsonOptions =
    let opts = JsonSerializerOptions(WriteIndented = true)
    opts.PropertyNameCaseInsensitive <- true
    opts

// ------------------------------------------------------------------
// Failure category / cause helpers (string ↔ union)
// ------------------------------------------------------------------

let internal categoryToString (c: RepoFailureCategory) : string =
    match c with
    | FindIssue             -> "FindIssue"
    | CreateIssue           -> "CreateIssue"
    | ReopenIssue           -> "ReopenIssue"
    | AssignIssue           -> "AssignIssue"
    | AddToProject          -> "AddToProject"
    | UpdateBody            -> "UpdateBody"
    | CmdCheckoutFailed     -> "CmdCheckoutFailed"
    | CmdToPrCheckoutFailed -> "CmdToPrCheckoutFailed"
    | CmdToPrNoDiff         -> "CmdToPrNoDiff"
    | CmdToPrPushFailed     -> "CmdToPrPushFailed"
    | CmdToPrOpenPrFailed   -> "CmdToPrOpenPrFailed"

let internal categoryOfString (s: string) : RepoFailureCategory option =
    match s with
    | "FindIssue"             -> Some FindIssue
    | "CreateIssue"           -> Some CreateIssue
    | "ReopenIssue"           -> Some ReopenIssue
    | "AssignIssue"           -> Some AssignIssue
    | "AddToProject"          -> Some AddToProject
    | "UpdateBody"            -> Some UpdateBody
    | "CmdCheckoutFailed"     -> Some CmdCheckoutFailed
    | "CmdToPrCheckoutFailed" -> Some CmdToPrCheckoutFailed
    | "CmdToPrNoDiff"         -> Some CmdToPrNoDiff
    | "CmdToPrPushFailed"     -> Some CmdToPrPushFailed
    | "CmdToPrOpenPrFailed"   -> Some CmdToPrOpenPrFailed
    | _                       -> None

let internal causeToString (c: RepoFailureCause) : string =
    match c with
    | RateLimit        -> "RateLimit"
    | NotFound         -> "NotFound"
    | Permission       -> "Permission"
    | UserError        -> "UserError"
    | NetworkTransient -> "NetworkTransient"
    | Unknown          -> "Unknown"

let internal causeOfString (s: string) : RepoFailureCause =
    match s with
    | "RateLimit"        -> RateLimit
    | "NotFound"         -> NotFound
    | "Permission"       -> Permission
    | "UserError"        -> UserError
    | "NetworkTransient" -> NetworkTransient
    | _                  -> Unknown

/// Classify a raw `gh` error message into a RepoFailureCause.
/// First match wins; falls back to Unknown.
let classifyCause (msg: string) : RepoFailureCause =
    if isNull msg then Unknown
    else
        let m = msg.ToLowerInvariant()
        let any (parts: string list) = parts |> List.exists m.Contains
        if   any [ "rate limit"; "secondary rate limit"; "abuse detection"; "submitted too quickly" ] then RateLimit
        // UserError must beat NotFound: gh emits "could not resolve user 'foo'" for bad assignees.
        elif any [ "could not resolve user"; "no such user"; "invalid login"; "invalid user" ]        then UserError
        elif any [ "could not resolve to"; "404"; "not found" ]                                       then NotFound
        elif any [ "403"; "permission"; "forbidden" ]                                                 then Permission
        elif any [ "timeout"; "connection refused"; "connection reset"; "tls handshake"
                   "remote end closed"; "no such host"; " eof"; "network" ]                          then NetworkTransient
        else Unknown

/// Merge per-step attempt results with the prior failure list.
///   - Ok      → drop any matching (repo, category) entry.
///   - Error m → upsert: increment Attempts (or start at 1), update LastFailedAt/LastMessage/Cause,
///               preserve FirstFailedAt from the prior entry when present.
/// Previous entries for steps NOT attempted this run are kept unchanged. Hash-change
/// based clearing is handled by the caller before invoking processRepo (it controls
/// which steps get attempted).
let mergeFailures
    (previous : RepoFailure list)
    (attempted : (RepoName * RepoFailureCategory * Result<unit, string>) list)
    (now : DateTimeOffset)
    : RepoFailure list =
    let prevMap =
        previous |> List.map (fun f -> (f.Repo, f.Category), f) |> Map.ofList
    let afterAttempts =
        attempted
        |> List.fold (fun map (repo, cat, result) ->
            match result with
            | Ok () ->
                Map.remove (repo, cat) map
            | Error msg ->
                let cause = classifyCause msg
                let updated =
                    match Map.tryFind (repo, cat) map with
                    | Some e ->
                        { e with
                            Attempts     = e.Attempts + 1
                            LastFailedAt = now
                            LastMessage  = msg
                            Cause        = cause }
                    | None ->
                        { Repo          = repo
                          Category      = cat
                          Cause         = cause
                          Attempts      = 1
                          FirstFailedAt = now
                          LastFailedAt  = now
                          LastMessage   = msg }
                Map.add (repo, cat) updated map
        ) prevMap
    afterAttempts
    |> Map.toList
    |> List.map snd
    |> List.sortBy (fun f ->
        let (RepoName r) = f.Repo
        r, categoryToString f.Category)

// ------------------------------------------------------------------
// Domain ↔ DTO conversion
// ------------------------------------------------------------------

let private toDto (lock: LockFile) : LockFileDto =
    let (OrgName orgStr) = lock.Project.Org
    { lockedAt    = lock.LockedAt.ToString("o")
      yamlHash    = lock.YamlHash
      templateHash = lock.TemplateHash
      project  =
          { org    = orgStr
            number = lock.Project.Number
            title  = lock.Project.Title
            url    = lock.Project.Url }
      repos =
          lock.Repos
          |> List.map (fun (RepoName r) -> r)
          |> Array.ofList
      issues =
          lock.Issues
          |> List.map (fun i ->
              let (RepoName r)    = i.Repo
              let (IssueNumber n) = i.Number
              { repo      = r
                number    = n
                url       = i.Url
                assignees = i.Assignees |> Array.ofList })
          |> Array.ofList
      pullRequests =
          lock.PullRequests
          |> List.map (fun pr ->
              let (RepoName r)    = pr.Repo
              let (PrNumber n)    = pr.Number
              let (IssueNumber c) = pr.ClosesIssue
              { repo        = r
                number      = n
                url         = pr.Url
                closesIssue = c
                state       = pr.State })
          |> Array.ofList
      skippedRepos =
          lock.SkippedRepos
          |> List.map (fun (RepoName r) -> r)
          |> Array.ofList
      failures =
          lock.Failures
          |> List.map (fun f ->
              let (RepoName r) = f.Repo
              { repo          = r
                category      = categoryToString f.Category
                cause         = causeToString f.Cause
                attempts      = f.Attempts
                firstFailedAt = f.FirstFailedAt.ToString("o")
                lastFailedAt  = f.LastFailedAt.ToString("o")
                lastMessage   = f.LastMessage })
          |> Array.ofList }

let private ofDto (dto: LockFileDto) : LockFile =
    { LockedAt     = DateTimeOffset.Parse(dto.lockedAt)
      YamlHash     = dto.yamlHash
      TemplateHash = if isNull dto.templateHash then "" else dto.templateHash
      Project      =
          { Org    = OrgName dto.project.org
            Number = dto.project.number
            Title  = dto.project.title
            Url    = dto.project.url }
      Repos =
          dto.repos |> Array.toList |> List.map RepoName
      Issues =
          dto.issues
          |> Array.toList
          |> List.map (fun i ->
              { Repo      = RepoName i.repo
                Number    = IssueNumber i.number
                Url       = i.url
                Assignees = i.assignees |> Array.toList })
      PullRequests =
          dto.pullRequests
          |> Array.toList
          |> List.map (fun pr ->
              { Repo        = RepoName pr.repo
                Number      = PrNumber pr.number
                Url         = pr.url
                ClosesIssue = IssueNumber pr.closesIssue
                State       = if isNull pr.state || pr.state = "" then "OPEN" else pr.state })
      SkippedRepos =
          if isNull dto.skippedRepos then []
          else dto.skippedRepos |> Array.toList |> List.map RepoName
      Failures =
          if isNull dto.failures then []
          else
              dto.failures
              |> Array.toList
              |> List.choose (fun f ->
                  match categoryOfString f.category with
                  | None     -> None  // unknown category — drop the entry rather than fail the read
                  | Some cat ->
                      Some { Repo          = RepoName f.repo
                             Category      = cat
                             Cause         = causeOfString f.cause
                             Attempts      = f.attempts
                             FirstFailedAt = DateTimeOffset.Parse(f.firstFailedAt)
                             LastFailedAt  = DateTimeOffset.Parse(f.lastFailedAt)
                             LastMessage   = if isNull f.lastMessage then "" else f.lastMessage }) }

// ------------------------------------------------------------------
// Public API
// ------------------------------------------------------------------

/// Derive the lock file path from the YAML config file path.
/// Uses the same path separator convention as the input (forward-slash safe).
let lockFilePath (yamlPath: string) : string =
    let normalised = yamlPath.Replace('\\', '/')
    let dir  = System.IO.Path.GetDirectoryName(normalised) |> Option.ofObj |> Option.defaultValue "" |> fun s -> s.Replace('\\', '/')
    let stem = System.IO.Path.GetFileNameWithoutExtension(normalised)
    if dir = "" || dir = "." then $"{stem}.lock.json"
    else $"{dir}/{stem}.lock.json"

/// Read and deserialise a lock file.
/// Returns None if the file does not exist.
let tryRead (fs: IFileSystem) (yamlPath: string) : LockFile option =
    let path = lockFilePath yamlPath
    if not (fs.File.Exists(path)) then
        None
    else
        let json = fs.File.ReadAllText(path)
        match JsonSerializer.Deserialize<LockFileDto>(json, jsonOptions) |> Option.ofObj with
        | None     -> failwith $"Lock file '{path}' deserialised to null."
        | Some dto -> Some (ofDto dto)

/// Serialise and write a lock file to disk.
let write (fs: IFileSystem) (yamlPath: string) (lock: LockFile) : unit =
    let path = lockFilePath yamlPath
    let dir  = System.IO.Path.GetDirectoryName(path) |> Option.ofObj |> Option.defaultValue "."
    fs.Directory.CreateDirectory(dir) |> ignore
    let json = JsonSerializer.Serialize(toDto lock, jsonOptions)
    fs.File.WriteAllText(path, json)
