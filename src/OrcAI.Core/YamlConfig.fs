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
      onClosedIssue: string
      redoOnClosed:  System.Nullable<bool> }

[<CLIMutable>]
type YamlIssue =
    { template: string
      labels:   System.Collections.Generic.List<string> }

[<CLIMutable>]
type YamlAction =
    { ``type``        : string
      comment         : string
      ``to``          : string
      execute         : obj
      cwd             : string
      writeBack       : string
      errorIfNoDiff   : System.Nullable<bool>
      branch          : string
      commitMessage   : string
      prTitle         : string
      prBody          : string }

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

let private parseCmdExec (execute: obj) (typeName: string) : CmdExec =
    match execute with
    | null ->
        failwith $"action type '{typeName}' requires 'execute: <command>'."
    | :? string as s when not (String.IsNullOrWhiteSpace s) ->
        Shell s
    | :? string ->
        failwith $"action type '{typeName}': 'execute' must not be blank."
    | :? System.Collections.Generic.List<obj> as lst when lst.Count > 0 ->
        let items = lst |> Seq.map string |> List.ofSeq
        Exec(List.head items, List.tail items)
    | :? System.Collections.Generic.List<obj> ->
        failwith $"action type '{typeName}': exec list form requires at least one element (the command)."
    | _ ->
        failwith $"action type '{typeName}': 'execute' must be a string (shell form) or a list e.g. [cmd, arg1, arg2] (exec form)."

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
        elif yamlText.Contains("skip_closed_issues:") || yamlText.Contains("skipClosedIssues:") then
            Error "'skip_closed_issues' has been removed. Use 'job.redo_on_closed: true' to re-run the action when the issue or PR is closed."
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
                    | "cmd" | "cmd-checkout" | "cmd-to-pr" ->
                        let typeName = root.action.``type``
                        let cmdExec  = parseCmdExec root.action.execute typeName
                        match typeName with
                        | "cmd" ->
                            Cmd(cmdExec, nullStr root.action.cwd)
                        | "cmd-checkout" ->
                            CmdCheckout(cmdExec, nullStr root.action.cwd)
                        | _ ->
                            let writeBack =
                                match root.action.writeBack with
                                | null | ""            -> None
                                | "pr-to-origin"       -> Some PrToOrigin
                                | "commit-to-origin"   -> Some CommitToOrigin
                                | "fork-and-pr"        -> Some ForkAndPr
                                | other -> failwith $"Unknown writeBack value: '{other}'. Valid: pr-to-origin, commit-to-origin, fork-and-pr."
                            CmdToPr
                                { Execute       = cmdExec
                                  Cwd           = nullStr root.action.cwd
                                  WriteBack     = writeBack
                                  ErrorIfNoDiff = root.action.errorIfNoDiff |> Option.ofNullable |> Option.defaultValue false
                                  Branch        = nullStr root.action.branch
                                  CommitMessage = nullStr root.action.commitMessage
                                  PrTitle       = nullStr root.action.prTitle
                                  PrBody        = nullStr root.action.prBody }
                    | "noop" -> Noop
                    | other  -> failwith $"Unknown action type: '{other}'. Valid: assign-copilot, assign, comment, comment-and-assign, cmd, cmd-checkout, cmd-to-pr, noop."
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
            let redoOnClosed =
                if root.job.redoOnClosed.HasValue then Some root.job.redoOnClosed.Value else None
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
                 DependsOn     = dependsOnList
                 RedoOnClosed  = redoOnClosed }
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
