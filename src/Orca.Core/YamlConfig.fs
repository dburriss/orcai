module Orca.Core.YamlConfig

open System
open System.IO
open System.Security.Cryptography
open YamlDotNet.Serialization
open YamlDotNet.Serialization.NamingConventions
open Orca.Core.Domain

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
    { title:     string
      org:       string
      skipCopilot: bool }

[<CLIMutable>]
type YamlIssue =
    { template: string
      labels:   System.Collections.Generic.List<string> }

[<CLIMutable>]
type YamlRoot =
    { job:   YamlJob
      repos: System.Collections.Generic.List<string>
      issue: YamlIssue }

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
            Ok { Org          = OrgName root.job.org
                 ProjectTitle = root.job.title
                 Repos        = root.repos |> Seq.map (fun r -> RepoName $"{root.job.org}/{r}") |> List.ofSeq
                 IssueTitle   = root.job.title
                 IssueBody    = templateContent
                 Labels       = labels
                 SkipCopilot  = root.job.skipCopilot }
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
let parseFile (path: string) : Result<JobConfig, string> =
    if not (File.Exists(path)) then
        Error $"YAML config file not found: {path}"
    else
        try
            let yaml = File.ReadAllText(path)
            // Peek at the raw YAML to resolve the template path before full validation.
            let root = deserializer.Deserialize<YamlRoot>(yaml)
            if isNull (box root) || isNull (box root.issue) || String.IsNullOrWhiteSpace(root.issue.template) then
                // Let `parse` produce the proper validation error message.
                parse yaml "" ""
            else
                let yamlDir      = Path.GetDirectoryName(Path.GetFullPath(path))
                let templatePath = Path.GetFullPath(Path.Combine(yamlDir, root.issue.template))
                if not (File.Exists(templatePath)) then
                    Error $"Issue template file not found: {templatePath}"
                else
                    let templateContent = File.ReadAllText(templatePath)
                    parse yaml templatePath templateContent
        with ex ->
            Error $"Failed to parse YAML file '{path}': {ex.Message}"

/// Compute the SHA-256 hash of the raw YAML file content.
/// Used to populate the yamlHash field in the lock file.
let computeHash (path: string) : string =
    hashBytes (File.ReadAllBytes(path))
