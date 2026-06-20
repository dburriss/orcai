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
//   action:
//     type: assign-copilot   # | assign | comment | comment-and-assign | cmd | noop

[<CLIMutable>]
type YamlJob =
    { title:         string
      org:           string
      owner:         string
      onClosedIssue: string }

[<CLIMutable>]
type YamlIssue =
    { template: string
      labels:   System.Collections.Generic.List<string> }

[<CLIMutable>]
type YamlAction =
    { ``type``  : string
      comment   : string
      ``to``    : string
      execute   : string
      run       : string
      args      : System.Collections.Generic.List<string>
      cwd       : string }

[<CLIMutable>]
type YamlNudge =
    { mode    : string
      comment : string }

[<CLIMutable>]
type YamlNotify =
    { comment : string }

[<CLIMutable>]
type YamlFailures =
    { maxAttempts : System.Nullable<int> }

[<CLIMutable>]
type YamlDependsOn =
    { job            : string
      condition      : string
      scope          : string
      untrackedRepos : string }

[<CLIMutable>]
type YamlRoot =
    { job:       YamlJob
      repos:     System.Collections.Generic.List<string>
      issue:     YamlIssue
      action:    YamlAction
      nudge:     YamlNudge
      notify:    YamlNotify
      failures:  YamlFailures
      dependsOn: System.Collections.Generic.List<YamlDependsOn> }

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
        elif yamlText.Contains("skipCopilot:") then
            Error "'job.skipCopilot' has been removed. Use 'action: { type: noop }' to skip assignment, or omit 'action:' to assign @copilot."
        elif not (isNull (box (root :> obj))) && yamlText.Contains("\nassign:") || yamlText.StartsWith("assign:") then
            Error "The 'assign:' field has been replaced by 'action:'. Migrate to: action: { type: assign-copilot, ... }"
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
            let actionConfig =
                if isNull (box root.action) then
                    AssignCopilot None
                else
                    let actionArgs =
                        if isNull (box root.action.args) then []
                        else root.action.args |> Seq.toList
                    match root.action.``type`` with
                    | null | "" | "assign-copilot" ->
                        AssignCopilot (nullStr root.action.comment)
                    | "assign" ->
                        match nullStr root.action.``to`` with
                        | None   -> failwith "action type 'assign' requires a 'to' field."
                        | Some t -> Assign(t, nullStr root.action.comment)
                    | "comment" ->
                        match nullStr root.action.comment with
                        | None   -> failwith "action type 'comment' requires a 'comment' field."
                        | Some c -> Comment c
                    | "comment-and-assign" ->
                        match nullStr root.action.``to``, nullStr root.action.comment with
                        | None, _       -> failwith "action type 'comment-and-assign' requires a 'to' field."
                        | _, None       -> failwith "action type 'comment-and-assign' requires a 'comment' field."
                        | Some t, Some c -> CommentAndAssign(t, c)
                    | "cmd" ->
                        let hasExecute = not (String.IsNullOrWhiteSpace(root.action.execute))
                        let hasRun     = not (String.IsNullOrWhiteSpace(root.action.run))
                        if hasExecute && hasRun then
                            failwith "action type 'cmd': 'execute' and 'run' are mutually exclusive — provide only one."
                        elif not hasExecute && not hasRun then
                            failwith "action type 'cmd' requires either 'execute' (script path) or 'run' (inline command)."
                        else
                            let source = if hasExecute then Script root.action.execute else Inline root.action.run
                            Cmd(source, actionArgs, nullStr root.action.cwd)
                    | "noop" -> Noop
                    | other  -> failwith $"Unknown action type: '{other}'. Valid: assign-copilot, assign, comment, comment-and-assign, cmd, noop."
            let nudgeConfig =
                if isNull (box root.nudge) then None
                else Some { Mode    = nullStr root.nudge.mode
                            Comment = nullStr root.nudge.comment }
            let notifyConfig =
                if isNull (box root.notify) then None
                else Some { Comment = nullStr root.notify.comment }
            let maxAttempts =
                if isNull (box root.failures) then None
                elif root.failures.maxAttempts.HasValue then Some root.failures.maxAttempts.Value
                else None
            let parseDependsOnEntry (dto: YamlDependsOn) : DependsOnConfig =
                if String.IsNullOrWhiteSpace(dto.job) then
                    failwith "A depends_on entry is missing the required 'job' field."
                let condition =
                    match dto.condition with
                    | null | "" | "pr_merged" -> PrMerged
                    | "issue_closed"          -> IssueClosed
                    | other                   -> failwith $"Unknown depends_on condition: '{other}'. Valid values: pr_merged, issue_closed."
                let scope =
                    match dto.scope with
                    | null | "" | "per_repo" -> PerRepo
                    | "all_repos"            -> AllRepos
                    | other                  -> failwith $"Unknown depends_on scope: '{other}'. Valid values: per_repo, all_repos."
                let untrackedRepos =
                    match dto.untrackedRepos with
                    | null | "" | "include" -> UntrackedReposBehavior.Include
                    | "skip"                -> UntrackedReposBehavior.Skip
                    | other                 -> failwith $"Unknown depends_on untracked_repos: '{other}'. Valid values: include, skip."
                { Job = dto.job; Condition = condition; Scope = scope; UntrackedRepos = untrackedRepos }
            let dependsOnList =
                if isNull (box root.dependsOn) then []
                else root.dependsOn |> Seq.map parseDependsOnEntry |> List.ofSeq
            Ok { Org           = OrgName root.job.org
                 ProjectTitle  = root.job.title
                 Repos         = root.repos |> Seq.map (fun r -> RepoName $"{root.job.org}/{r}") |> List.ofSeq
                 IssueTitle    = root.job.title
                 IssueBody     = templateContent
                 Labels        = labels
                 Action        = actionConfig
                 OnClosedIssue = closedIssueAction
                 Nudge         = nudgeConfig
                 Notify        = notifyConfig
                 JobOwner      = nullStr root.job.owner
                 MaxAttempts   = maxAttempts
                 DependsOn     = dependsOnList }
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

/// Resolve the absolute path of the issue template referenced in a YAML job config.
/// Returns None if the file does not exist or the template path cannot be resolved.
let resolveTemplatePath (fs: IFileSystem) (path: string) : string option =
    if not (fs.File.Exists(path)) then None
    else
        try
            let yaml = fs.File.ReadAllText(path)
            let root = deserializer.Deserialize<YamlRoot>(yaml)
            if isNull (box root) || isNull (box root.issue) || String.IsNullOrWhiteSpace(root.issue.template) then None
            else
                let yamlDir      = Path.GetDirectoryName(Path.GetFullPath(path)) |> Option.ofObj |> Option.defaultValue "."
                let templatePath = Path.GetFullPath(Path.Combine(yamlDir, root.issue.template))
                if fs.File.Exists(templatePath) then Some templatePath else None
        with _ -> None

/// Compute the SHA-256 hash of the raw template file content.
/// Used to populate the templateHash field in the lock file.
let computeTemplateHash (fs: IFileSystem) (path: string) : string =
    hashBytes (fs.File.ReadAllBytes(path))
