module OrcAI.Core.YamlConfig

open System
open System.IO
open System.Security.Cryptography
open YamlDotNet.Serialization
open YamlDotNet.Serialization.NamingConventions
open System.IO.Abstractions
open OrcAI.Core.Domain

// ---------------------------------------------------------------------------
// Parses the YAML job configuration file into a JobConfig record.
// Uses YamlDotNet for deserialisation.
// ---------------------------------------------------------------------------

// Private DTO types that mirror the nested YAML schema:
//
//   job:
//     title: "..."
//     org:   "..."
//   repos:
//     - "..."
//   issue:
//     template: "./something.md"
//     labels:   [...]

[<CLIMutable>]
type YamlJob =
    { title:         string
      org:           string
      skipCopilot:   bool
      onClosedIssue: string }

[<CLIMutable>]
type YamlIssue =
    { template: string
      labels:   System.Collections.Generic.List<string> }

[<CLIMutable>]
type YamlAssign =
    { ``to``  : string
      via     : string
      comment : string }

[<CLIMutable>]
type YamlNudge =
    { mode    : string
      comment : string }

[<CLIMutable>]
type YamlRoot =
    { job:    YamlJob
      repos:  System.Collections.Generic.List<string>
      issue:  YamlIssue
      assign: YamlAssign
      nudge:  YamlNudge }

let private deserializer =
    DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build()

/// Pure: parse YAML text and a pre-loaded template body into a JobConfig.
/// `org` is taken from the parsed YAML; repos are prefixed with it.
let parse (yamlText: string) (templatePath: string) (templateContent: string) : Result<JobConfig, string> =
    try
        let root = deserializer.Deserialize<YamlRoot>(yamlText)

        if isNull (box root) then
            Error "YAML file is empty or could not be parsed."
        elif isNull (box root.job) then
            Error "YAML is missing required 'job' section."
        elif String.IsNullOrWhiteSpace(root.job.title) then
            Error "YAML 'job.title' is required."
        elif String.IsNullOrWhiteSpace(root.job.org) then
            Error "YAML 'job.org' is required."
        elif isNull (box root.repos) || root.repos.Count = 0 then
            Error "YAML 'repos' list is required and must not be empty."
        elif isNull (box root.issue) then
            Error "YAML is missing required 'issue' section."
        elif String.IsNullOrWhiteSpace(root.issue.template) then
            Error "YAML 'issue.template' is required."
        else
            let labels =
                if isNull (box root.issue.labels) then []
                else root.issue.labels |> Seq.toList
            let closedIssueAction =
                match root.job.onClosedIssue with
                | null | "" | "create" -> Create
                | "reopen"             -> Reopen
                | "skip"               -> Skip
                | "fail"               -> Fail
                | other                -> failwith $"Unknown onClosedIssue value: '{other}'. Valid values: create, reopen, skip, fail."
            let nullStr (s: string) = match box s with | null -> None | _ -> Some s
            let assignConfig =
                if isNull (box root.assign) then None
                else Some { To      = nullStr root.assign.``to``
                            Via     = nullStr root.assign.via
                            Comment = nullStr root.assign.comment }
            let nudgeConfig =
                if isNull (box root.nudge) then None
                else Some { Mode    = nullStr root.nudge.mode
                            Comment = nullStr root.nudge.comment }
            Ok { Org           = OrgName root.job.org
                 ProjectTitle  = root.job.title
                 Repos         = root.repos |> Seq.map (fun r -> RepoName $"{root.job.org}/{r}") |> List.ofSeq
                 IssueTitle    = root.job.title
                 IssueBody     = templateContent
                 Labels        = labels
                 SkipCopilot   = root.job.skipCopilot
                 OnClosedIssue = closedIssueAction
                 Assign        = assignConfig
                 Nudge         = nudgeConfig }
    with ex ->
        Error $"Failed to parse YAML: {ex.Message}"

/// Pure: compute the SHA-256 hash of raw bytes, returning a lowercase hex string.
let hashBytes (bytes: byte[]) : string =
    use sha  = SHA256.Create()
    let hash = sha.ComputeHash(bytes)
    Convert.ToHexStringLower(hash)

/// Parse a YAML job configuration from a file path.
/// Reads the YAML and its referenced template from disk, then delegates to `parse`.
/// Returns an error string if any file is missing or the content is malformed.
let parseFile (fs: IFileSystem) (path: string) : Result<JobConfig, string> =
    if not (fs.File.Exists(path)) then
        Error $"YAML config file not found: {path}"
    else
        try
            let yaml = fs.File.ReadAllText(path)
            // Peek at the raw YAML to resolve the template path before full validation.
            let root = deserializer.Deserialize<YamlRoot>(yaml)
            if isNull (box root) || isNull (box root.issue) || String.IsNullOrWhiteSpace(root.issue.template) then
                // Let `parse` produce the proper validation error message.
                parse yaml "" ""
            else
                let yamlDir      = Path.GetDirectoryName(Path.GetFullPath(path)) |> Option.ofObj |> Option.defaultValue "."
                let templatePath = Path.GetFullPath(Path.Combine(yamlDir, root.issue.template))
                if not (fs.File.Exists(templatePath)) then
                    Error $"Issue template file not found: {templatePath}"
                else
                    let templateContent = fs.File.ReadAllText(templatePath)
                    parse yaml templatePath templateContent
        with ex ->
            Error $"Failed to parse YAML file '{path}': {ex.Message}"

/// Compute the SHA-256 hash of the raw YAML file content.
/// Used to populate the yamlHash field in the lock file.
let computeHash (fs: IFileSystem) (path: string) : string =
    hashBytes (fs.File.ReadAllBytes(path))
