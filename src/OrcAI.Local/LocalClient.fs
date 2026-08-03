module OrcAI.Local.LocalClient

// Production implementation of OrcAI.Core.Provider's IIssueTracker backed by
// YAML project files + Markdown issue files on disk, instead of a live GitHub
// API. See plans/local-provider.md for the storage layout and identity
// scheme (ULID issue ids, org+title slug project ids).
//
// Implements IIssueTracker only — no IPullRequestLinker/IRepoInspector.
// All file access goes through the injected IFileSystem so this is testable
// with Testably.Abstractions.Testing.MockFileSystem, the same way OrcAI.Core's
// YamlConfig/OrcAIConfig modules are tested.

open System
open System.IO.Abstractions
open YamlDotNet.Serialization
open YamlDotNet.Serialization.NamingConventions
open OrcAI.Core.Domain
open OrcAI.Core.Provider

[<CLIMutable>]
type ProjectFileDto =
    { id: string
      title: string
      org: string
      issues: string[]
      createdAt: string }

[<CLIMutable>]
type IssueFrontmatterDto =
    { id: string
      title: string
      state: string
      labels: string[]
      assignees: string[]
      createdAt: string
      updatedAt: string }

let private serializer =
    SerializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .Build()

let private deserializer =
    DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build()

let private nowIso () = DateTimeOffset.UtcNow.ToString("o")

/// Lowercase, non-alphanumeric runs collapsed to '-', trimmed. Deterministic
/// by construction: two callers computing a slug from the same org+title
/// always converge on the same filename — see plans/local-provider.md
/// "Identity scheme" for why this replaces a shared counter.
let private slugify (s: string) : string =
    let lowered  = s.ToLowerInvariant()
    let replaced = System.Text.RegularExpressions.Regex.Replace(lowered, "[^a-z0-9]+", "-")
    replaced.Trim('-')

/// RepoName is always "org/repo" (see YamlConfig.parse); split it back apart
/// for filesystem paths that namespace org and repo as separate segments.
let private repoParts (repo: RepoName) : string * string =
    let (RepoName r) = repo
    match r.Split([| '/' |], 2) with
    | [| org; repoOnly |] -> org, repoOnly
    | _                   -> r, r

let private projectPath (root: string) (org: OrgName) (slug: string) : string =
    let (OrgName orgStr) = org
    IO.Path.Combine(root, "projects", orgStr, slug + ".yaml")

let private repoDir (root: string) (org: string) (repo: string) : string =
    IO.Path.Combine(root, "repos", org, repo)

let private issuesDir (root: string) (org: string) (repo: string) : string =
    IO.Path.Combine(repoDir root org repo, "issues")

let private issuePath (root: string) (org: string) (repo: string) (issue: IssueId) : string =
    let (IssueId id) = issue
    IO.Path.Combine(issuesDir root org repo, id + ".md")

let private labelsPath (root: string) (org: string) (repo: string) : string =
    IO.Path.Combine(repoDir root org repo, "labels.yaml")

/// Frontmatter is a "---"-delimited YAML block followed by the Markdown body.
/// Simple line-based split — no full Markdown parsing needed.
let private splitFrontmatter (content: string) : string * string =
    let normalized = content.Replace("\r\n", "\n")
    let lines = normalized.Split('\n')
    if lines.Length = 0 || lines.[0].Trim() <> "---" then
        "", normalized
    else
        match lines |> Array.skip 1 |> Array.tryFindIndex (fun l -> l.Trim() = "---") with
        | None -> "", normalized
        | Some relIdx ->
            let closeIdx = relIdx + 1
            let yamlText = String.Join("\n", lines.[1 .. closeIdx - 1])
            let bodyText = String.Join("\n", lines.[closeIdx + 1 ..]).TrimStart('\n')
            yamlText, bodyText

let private joinFrontmatter (frontmatterYaml: string) (body: string) : string =
    $"---\n{frontmatterYaml.TrimEnd('\n')}\n---\n{body}"

let private toIssueRef (repo: RepoName) (dto: IssueFrontmatterDto) : IssueRef =
    let org, r = repoParts repo
    { Repo      = repo
      Id        = IssueId dto.id
      Url       = $"local://{org}/{r}/issues/{dto.id}"
      Assignees = if isNull (box dto.assignees) then [] else dto.assignees |> List.ofArray }

let private tryReadProject (fs: IFileSystem) (root: string) (org: OrgName) (title: string) : ProjectInfo option =
    let (OrgName orgStr) = org
    let slug = slugify $"{orgStr}/{title}"
    let path = projectPath root org slug
    if not (fs.File.Exists(path)) then None
    else
        let dto = deserializer.Deserialize<ProjectFileDto>(fs.File.ReadAllText(path))
        Some { Org = OrgName dto.org; Id = ProjectId dto.id; Title = dto.title; Url = $"local://{orgStr}/{dto.id}" }

let private writeProject (fs: IFileSystem) (root: string) (project: ProjectInfo) (issues: string[]) : unit =
    let (ProjectId idStr) = project.Id
    let (OrgName orgStr)  = project.Org
    let path = projectPath root project.Org idStr
    fs.Directory.CreateDirectory(IO.Path.GetDirectoryName(path)) |> ignore
    let dto = { id = idStr; title = project.Title; org = orgStr; issues = issues; createdAt = nowIso () }
    fs.File.WriteAllText(path, serializer.Serialize(dto))

let private readLabels (fs: IFileSystem) (root: string) (org: string) (repo: string) : string list =
    let path = labelsPath root org repo
    if not (fs.File.Exists(path)) then []
    else
        let arr = deserializer.Deserialize<string[]>(fs.File.ReadAllText(path))
        if isNull (box arr) then [] else arr |> List.ofArray

let private writeLabels (fs: IFileSystem) (root: string) (org: string) (repo: string) (labels: string list) : unit =
    let path = labelsPath root org repo
    fs.Directory.CreateDirectory(IO.Path.GetDirectoryName(path)) |> ignore
    fs.File.WriteAllText(path, serializer.Serialize(labels |> List.toArray))

let private tryReadIssueFile (fs: IFileSystem) (root: string) (repo: RepoName) (issue: IssueId) : (IssueFrontmatterDto * string) option =
    let org, r = repoParts repo
    let path = issuePath root org r issue
    if not (fs.File.Exists(path)) then None
    else
        let yamlText, body = splitFrontmatter (fs.File.ReadAllText(path))
        Some (deserializer.Deserialize<IssueFrontmatterDto>(yamlText), body)

let private writeIssueFile (fs: IFileSystem) (root: string) (repo: RepoName) (issue: IssueId) (dto: IssueFrontmatterDto) (body: string) : unit =
    let org, r = repoParts repo
    let path = issuePath root org r issue
    fs.Directory.CreateDirectory(IO.Path.GetDirectoryName(path)) |> ignore
    fs.File.WriteAllText(path, joinFrontmatter (serializer.Serialize(dto)) body)

let private allIssueFiles (fs: IFileSystem) (root: string) (repo: RepoName) : string list =
    let org, r = repoParts repo
    let dir = issuesDir root org r
    if not (fs.Directory.Exists(dir)) then []
    else fs.Directory.GetFiles(dir, "*.md") |> List.ofArray

let private findIssueByTitleState (fs: IFileSystem) (root: string) (repo: RepoName) (title: string) (wantState: string) : IssueRef option =
    allIssueFiles fs root repo
    |> List.tryPick (fun path ->
        let yamlText, _ = splitFrontmatter (fs.File.ReadAllText(path))
        let dto = deserializer.Deserialize<IssueFrontmatterDto>(yamlText)
        if dto.title = title && dto.state = wantState then Some (toIssueRef repo dto) else None)

/// File-backed IIssueTracker. Writes are not transactional — see
/// plans/local-provider.md "Known limitations" for the accepted race window.
type LocalClient(fs: IFileSystem, root: string) =

    interface IIssueTracker with

        member _.FindProject org title =
            async { return tryReadProject fs root org title }

        member _.CreateProject org title =
            async {
                match tryReadProject fs root org title with
                | Some existing -> return Ok existing
                | None ->
                    let (OrgName orgStr) = org
                    let slug = slugify $"{orgStr}/{title}"
                    let project = { Org = org; Id = ProjectId slug; Title = title; Url = $"local://{orgStr}/{slug}" }
                    writeProject fs root project [||]
                    return Ok project
            }

        member _.DeleteProject project =
            async {
                let (ProjectId idStr) = project.Id
                let path = projectPath root project.Org idStr
                if fs.File.Exists(path) then fs.File.Delete(path)
                return Ok ()
            }

        member _.ListLabels repo =
            async {
                let org, r = repoParts repo
                return Ok (readLabels fs root org r)
            }

        member _.CreateLabel repo name =
            async {
                let org, r = repoParts repo
                let existing = readLabels fs root org r
                if existing |> List.contains name then return Ok ()
                else
                    writeLabels fs root org r (existing @ [ name ])
                    return Ok ()
            }

        member _.FindIssue repo title =
            async { return Ok (findIssueByTitleState fs root repo title "open") }

        member _.FindClosedIssue repo title =
            async { return Ok (findIssueByTitleState fs root repo title "closed") }

        member _.ReopenIssue repo issue =
            async {
                match tryReadIssueFile fs root repo issue with
                | None -> return Error $"Issue not found: {issue}"
                | Some (dto, body) ->
                    let updated = { dto with state = "open"; updatedAt = nowIso () }
                    writeIssueFile fs root repo issue updated body
                    return Ok (toIssueRef repo updated)
            }

        member _.CreateIssue repo title body labels =
            async {
                let id  = Ulid.NewUlid().ToString()
                let now = nowIso ()
                let dto =
                    { id = id; title = title; state = "open"
                      labels = labels |> List.toArray; assignees = [||]
                      createdAt = now; updatedAt = now }
                writeIssueFile fs root repo (IssueId id) dto body
                return Ok (toIssueRef repo dto)
            }

        member _.UpdateIssue repo issue title body =
            async {
                match tryReadIssueFile fs root repo issue with
                | None -> return Error $"Issue not found: {issue}"
                | Some (dto, _) ->
                    let updated = { dto with title = title; updatedAt = nowIso () }
                    writeIssueFile fs root repo issue updated body
                    return Ok ()
            }

        member _.DeleteIssue repo issue =
            async {
                let org, r = repoParts repo
                let path = issuePath root org r issue
                if fs.File.Exists(path) then fs.File.Delete(path)
                return Ok ()
            }

        member _.AddIssueToProject project issue =
            async {
                let (ProjectId idStr) = project.Id
                let path = projectPath root project.Org idStr
                if not (fs.File.Exists(path)) then
                    return Error $"Project not found: {idStr}"
                else
                    let dto = deserializer.Deserialize<ProjectFileDto>(fs.File.ReadAllText(path))
                    let (IssueId issueIdStr) = issue.Id
                    let _, repoOnly = repoParts issue.Repo
                    let entry = $"{repoOnly}#{issueIdStr}"
                    let issues = if isNull (box dto.issues) then [||] else dto.issues
                    if issues |> Array.contains entry then
                        return Ok ()
                    else
                        let updatedDto = { dto with issues = Array.append issues [| entry |] }
                        fs.File.WriteAllText(path, serializer.Serialize(updatedDto))
                        return Ok ()
            }

        member _.AssignIssue repo issue assignee =
            async {
                match tryReadIssueFile fs root repo issue with
                | None -> return Error $"Issue not found: {issue}"
                | Some (dto, body) ->
                    let assignees = if isNull (box dto.assignees) then [||] else dto.assignees
                    if assignees |> Array.contains assignee then return Ok ()
                    else
                        let updated = { dto with assignees = Array.append assignees [| assignee |]; updatedAt = nowIso () }
                        writeIssueFile fs root repo issue updated body
                        return Ok ()
            }

        member _.UnassignIssue repo issue assignee =
            async {
                match tryReadIssueFile fs root repo issue with
                | None -> return Error $"Issue not found: {issue}"
                | Some (dto, body) ->
                    let assignees = if isNull (box dto.assignees) then [||] else dto.assignees
                    let updated = { dto with assignees = assignees |> Array.filter (fun a -> a <> assignee); updatedAt = nowIso () }
                    writeIssueFile fs root repo issue updated body
                    return Ok ()
            }

        member _.PostComment repo issue body =
            async {
                match tryReadIssueFile fs root repo issue with
                | None -> return Error $"Issue not found: {issue}"
                | Some (dto, existingBody) ->
                    let timestamp = nowIso ()
                    let newBody = existingBody.TrimEnd('\n') + $"\n\n### Comment {timestamp}\n{body}\n"
                    writeIssueFile fs root repo issue { dto with updatedAt = timestamp } newBody
                    return Ok ()
            }

        member _.GetIssueState repo issue =
            async {
                match tryReadIssueFile fs root repo issue with
                | None -> return None
                | Some (dto, _) -> return Some dto.state
            }
