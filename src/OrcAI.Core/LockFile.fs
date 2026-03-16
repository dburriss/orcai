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
      [<JsonPropertyName("closesIssue")>] closesIssue: int }

[<CLIMutable>]
type LockFileDto =
    { [<JsonPropertyName("lockedAt")>]     lockedAt:     string
      [<JsonPropertyName("yamlHash")>]     yamlHash:     string
      [<JsonPropertyName("project")>]      project:      ProjectInfoDto
      [<JsonPropertyName("repos")>]        repos:        string[]
      [<JsonPropertyName("issues")>]       issues:       IssueRefDto[]
      [<JsonPropertyName("pullRequests")>] pullRequests: PullRequestRefDto[] }

// ------------------------------------------------------------------
// JSON serialiser options
// ------------------------------------------------------------------

let private jsonOptions =
    let opts = JsonSerializerOptions(WriteIndented = true)
    opts.PropertyNameCaseInsensitive <- true
    opts

// ------------------------------------------------------------------
// Domain ↔ DTO conversion
// ------------------------------------------------------------------

let private toDto (lock: LockFile) : LockFileDto =
    let (OrgName orgStr) = lock.Project.Org
    { lockedAt = lock.LockedAt.ToString("o")
      yamlHash = lock.YamlHash
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
                closesIssue = c })
          |> Array.ofList }

let private ofDto (dto: LockFileDto) : LockFile =
    { LockedAt = DateTimeOffset.Parse(dto.lockedAt)
      YamlHash = dto.yamlHash
      Project  =
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
                ClosesIssue = IssueNumber pr.closesIssue }) }

// ------------------------------------------------------------------
// Public API
// ------------------------------------------------------------------

/// Derive the lock file path from the YAML config file path.
/// Uses the same path separator convention as the input (forward-slash safe).
let lockFilePath (yamlPath: string) : string =
    let normalised = yamlPath.Replace('\\', '/')
    let dir  = System.IO.Path.GetDirectoryName(normalised) |> Option.ofObj |> Option.defaultValue ""
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
