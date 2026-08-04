module OrcAI.Core.Migrations.LockStep1To2

// ---------------------------------------------------------------------------
// Migrates a lock file from the schema last shipped in v0.8.1 ("v1" — no
// 'formatVersion' field, int issue/project numbers, no PR 'state') to the
// current schema ("v2" — see LockFile.fs's LockFileDto).
//
// Purely local: no GitHub calls. The one field that can't be losslessly
// recovered offline — pullRequests[].state, which didn't exist in v1 — is
// defaulted to "OPEN" with a warning; the caller can refresh it later via
// `orcai nudge --save-lock`.
// ---------------------------------------------------------------------------

open System
open System.Text.Json
open System.Text.Json.Serialization
open OrcAI.Core.LockFile

[<CLIMutable>]
type ProjectInfoDtoV1 =
    { [<JsonPropertyName("org")>]    org:    string
      [<JsonPropertyName("number")>] number: int
      [<JsonPropertyName("title")>]  title:  string
      [<JsonPropertyName("url")>]    url:    string }

[<CLIMutable>]
type IssueRefDtoV1 =
    { [<JsonPropertyName("repo")>]      repo:      string
      [<JsonPropertyName("number")>]    number:    int
      [<JsonPropertyName("url")>]       url:       string
      [<JsonPropertyName("assignees")>] assignees: string[] }

[<CLIMutable>]
type PullRequestRefDtoV1 =
    { [<JsonPropertyName("repo")>]        repo:        string
      [<JsonPropertyName("number")>]      number:      int
      [<JsonPropertyName("url")>]         url:         string
      [<JsonPropertyName("closesIssue")>] closesIssue: int }

[<CLIMutable>]
type LockFileDtoV1 =
    { [<JsonPropertyName("formatVersion")>] formatVersion: Nullable<int>  // absent in a real v1 file; typed so an already-migrated file is detected
      [<JsonPropertyName("lockedAt")>]      lockedAt:     string
      [<JsonPropertyName("yamlHash")>]      yamlHash:     string
      [<JsonPropertyName("templateHash")>]  templateHash: string
      [<JsonPropertyName("project")>]       project:      ProjectInfoDtoV1
      [<JsonPropertyName("repos")>]         repos:        string[]
      [<JsonPropertyName("issues")>]        issues:       IssueRefDtoV1[]
      [<JsonPropertyName("pullRequests")>]  pullRequests: PullRequestRefDtoV1[]
      [<JsonPropertyName("skippedRepos")>]  skippedRepos: string[]
      [<JsonPropertyName("failures")>]      failures:     RepoFailureDto[] }

let private jsonOptions =
    let opts = JsonSerializerOptions(WriteIndented = true)
    opts.PropertyNameCaseInsensitive <- true
    opts

/// Migrate v1 lock JSON text to v2. Returns:
///   Ok (None, [])            — already at/past this step, nothing to do.
///   Ok (Some newText, warns) — migrated; warns flags fields that couldn't be recovered exactly.
///   Error msg                 — malformed input.
let apply (jsonText: string) : Result<string option * string list, string> =
    try
        // Peek at formatVersion generically first — an already-v2(+) file has
        // string ids that don't fit LockFileDtoV1's int-typed fields, so it
        // must never be run through that strict shape.
        use peek = JsonDocument.Parse(jsonText)
        let alreadyMigrated =
            match peek.RootElement.TryGetProperty("formatVersion") with
            | true, el when el.ValueKind = JsonValueKind.Number -> el.GetInt32() >= currentFormatVersion
            | _ -> false
        if alreadyMigrated then
            Ok (None, [])
        else
        match JsonSerializer.Deserialize<LockFileDtoV1>(jsonText, jsonOptions) |> Option.ofObj with
        | None -> Error "Lock file deserialised to null."
        | Some dto ->
            let migrated : LockFileDto =
                { formatVersion = currentFormatVersion
                  lockedAt      = dto.lockedAt
                  yamlHash      = dto.yamlHash
                  templateHash  = dto.templateHash
                  project =
                      { org   = dto.project.org
                        id    = string dto.project.number
                        title = dto.project.title
                        url   = dto.project.url }
                  repos = dto.repos
                  issues =
                      dto.issues
                      |> Array.map (fun i ->
                          { repo      = i.repo
                            id        = string i.number
                            url       = i.url
                            assignees = i.assignees })
                  pullRequests =
                      dto.pullRequests
                      |> Array.map (fun pr ->
                          { repo        = pr.repo
                            number      = pr.number
                            url         = pr.url
                            closesIssue = string pr.closesIssue
                            state       = "OPEN" })
                  skippedRepos = dto.skippedRepos
                  failures     = dto.failures }
            let warnings =
                if isNull dto.pullRequests || dto.pullRequests.Length = 0 then []
                else
                    [ $"{dto.pullRequests.Length} pull request(s) had 'state' defaulted to OPEN (this field didn't exist before this schema version) — run 'orcai nudge --save-lock' afterwards to refresh accurate states from GitHub." ]
            Ok (Some (JsonSerializer.Serialize(migrated, jsonOptions)), warnings)
    with ex ->
        Error $"Failed to parse lock file: {ex.Message}"
