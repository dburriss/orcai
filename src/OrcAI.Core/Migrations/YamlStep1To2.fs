module OrcAI.Core.Migrations.YamlStep1To2

// ---------------------------------------------------------------------------
// Migrates a job YAML from the schema last shipped in v0.8.1 ("v1" — no
// 'version:' field, 'assign:' block, optional 'job.skipCopilot') to the
// current schema ("v2" — 'action:' block, 'version: 2', no 'skipCopilot').
//
// Behaviour-preserving, not just field-renaming: the v1 defaults for
// assign.to/via and job.onClosedIssue are reproduced explicitly, because the
// v2 schema changed some of those defaults (onClosedIssue: create -> skip).
// See v0.8.1's RunCommand.fs (the skipCopilot/assign handling around what is
// now this module's `deriveAction`) for the exact old runtime behaviour this
// mirrors.
// ---------------------------------------------------------------------------

open System
open YamlDotNet.Serialization
open YamlDotNet.Serialization.NamingConventions
open OrcAI.Core.YamlConfig

// ------------------------------------------------------------------
// v1-shaped input DTOs (mirrors YamlConfig.fs as of v0.8.1).
// 'action', 'dependsOn', 'provider', and 'version' didn't exist in v1 — they
// are typed here purely so an already-v2-shaped (or partially-migrated) file
// round-trips untouched instead of losing those sections.
// ------------------------------------------------------------------

[<CLIMutable>]
type JobV1 =
    { title:         string
      org:           string
      owner:         string
      skipCopilot:   Nullable<bool>
      onClosedIssue: string }

[<CLIMutable>]
type AssignV1 =
    { ``to``  : string
      via     : string
      comment : string }

[<CLIMutable>]
type RootV1 =
    { job:       JobV1
      repos:     System.Collections.Generic.List<string>
      issue:     YamlIssue
      assign:    AssignV1
      nudge:     YamlNudge
      notify:    YamlNotify
      failures:  YamlFailures
      action:    YamlAction
      dependsOn: System.Collections.Generic.List<YamlDependsOn>
      provider:  YamlProvider
      version:   Nullable<int> }

// ------------------------------------------------------------------
// v2-shaped output DTO. Distinct from YamlRoot (YamlConfig.fs) only in that
// 'job' has no 'skipCopilot' and there's a 'version' field — everything else
// is reused as-is.
// ------------------------------------------------------------------

[<CLIMutable>]
type JobOut =
    { title:         string
      org:           string
      owner:         string
      onClosedIssue: string }

[<CLIMutable>]
type RootOut =
    { job:       JobOut
      repos:     System.Collections.Generic.List<string>
      issue:     YamlIssue
      action:    YamlAction
      nudge:     YamlNudge
      notify:    YamlNotify
      failures:  YamlFailures
      dependsOn: System.Collections.Generic.List<YamlDependsOn>
      provider:  YamlProvider
      version:   int }

let private deserializer =
    DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build()

let private serializer =
    SerializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
        .DisableAliases() // avoids YamlDotNet hashing DTOs by identity — see RootOut's structural GetHashCode issue with null fields
        .Build()

let private nullStr (s: string) = match box s with | null -> None | _ -> Some s

let private emptyAction (t: string) : YamlAction =
    { ``type``        = t
      comment         = null
      ``to``          = null
      execute         = Unchecked.defaultof<obj>
      cwd             = null
      writeBack       = null
      errorIfNoDiff   = Nullable()
      branch          = null
      commitMessage   = null
      prTitle         = null
      prBody          = null
      copy            = null }

/// True for "@copilot", "copilot", case-insensitive — matches the old
/// RunCommand.fs check for whether the assignee is the Copilot bot.
let private isCopilotHandle (handle: string) =
    handle.TrimStart('@').Equals("copilot", StringComparison.OrdinalIgnoreCase)

/// Replace the '{assignee}' token with a literal handle. The new 'comment'
/// action type has no 'to' field, so this token can no longer be resolved at
/// runtime — inlining it here keeps the rendered comment identical to before.
let private inlineAssignee (assignee: string) (tmpl: string) : string =
    tmpl.Replace("{assignee}", assignee)

/// Reproduces v0.8.1 RunCommand.fs's skipCopilot/assign.via handling as an
/// 'action:' block. Returns Error only for a genuinely unrecognised 'via'.
let private deriveAction (job: JobV1) (assign: AssignV1) : Result<YamlAction * string list, string> =
    let hasAssign = not (isNull (box assign))
    if job.skipCopilot.HasValue && job.skipCopilot.Value then
        let warnings =
            if hasAssign then
                [ "job.skipCopilot was true; the 'assign:' block had no effect under the old default (skipCopilot bypassed it entirely) and has been dropped in favour of 'action: { type: noop }'." ]
            else []
        Ok (emptyAction "noop", warnings)
    elif not hasAssign then
        Ok (emptyAction "assign-copilot", [])
    else
        let to_     = nullStr assign.``to`` |> Option.defaultValue "@copilot"
        let via     = nullStr assign.via    |> Option.defaultValue "assign"
        let comment = nullStr assign.comment
        match via with
        | "assign" ->
            // The old runtime never posted a comment for via="assign" — any
            // assign.comment was already inert, so it is dropped silently.
            if isCopilotHandle to_ then
                Ok (emptyAction "assign-copilot", [])
            else
                Ok ({ emptyAction "assign" with ``to`` = to_ }, [])
        | "comment" ->
            match comment with
            | None ->
                Ok (emptyAction "noop",
                    [ "assign.via was \"comment\" with no comment template — the old runtime posted nothing and assigned no one in this case; migrated to 'action: { type: noop }'." ])
            | Some tmpl ->
                let substituted = inlineAssignee to_ tmpl
                let warnings =
                    if substituted <> tmpl then
                        [ $"Inlined assign.to ('{to_}') into the comment template in place of the '{{assignee}}' token — the new 'comment' action type has no 'to' field to resolve it at runtime." ]
                    else []
                Ok ({ emptyAction "comment" with comment = substituted }, warnings)
        | "comment-and-assign" ->
            match comment with
            | None ->
                Ok ({ emptyAction "assign" with ``to`` = to_ },
                    [ "assign.via was \"comment-and-assign\" with no comment template — the old runtime only assigned (posted no comment) in this case; migrated to 'action: { type: assign }'." ])
            | Some c ->
                Ok ({ emptyAction "comment-and-assign" with ``to`` = to_; comment = c }, [])
        | other ->
            Error $"Unknown assign.via value: '{other}'. Cannot migrate automatically — resolve manually first."

let private toOutput (root: RootV1) (action: YamlAction) (onClosedIssue: string) : RootOut =
    { job =
        { title         = root.job.title
          org           = root.job.org
          owner         = root.job.owner
          onClosedIssue = onClosedIssue }
      repos     = root.repos
      issue     = root.issue
      action    = action
      nudge     = root.nudge
      notify    = root.notify
      failures  = root.failures
      dependsOn = root.dependsOn
      provider  = root.provider
      version   = 2 }

/// Migrate v1 YAML text to v2. Returns:
///   Ok (None, [])           — already at/past this step, nothing to do.
///   Ok (Some newText, warns) — migrated; warns explains any lossy/inferred mapping.
///   Error msg                — malformed input, or a conflict/unknown value migrate can't resolve.
let apply (yamlText: string) : Result<string option * string list, string> =
    try
        let root = deserializer.Deserialize<RootV1>(yamlText)
        if isNull (box root) then
            Error "YAML file is empty or could not be parsed."
        elif isNull (box root.job) then
            Error "YAML is missing required 'job' section."
        elif root.version.HasValue && root.version.Value >= 2 then
            Ok (None, [])
        else
            let hasAssign      = not (isNull (box root.assign))
            let hasSkipCopilot = root.job.skipCopilot.HasValue
            let hasAction      = not (isNull (box root.action))
            if hasAction && (hasAssign || hasSkipCopilot) then
                Error "This file has both a legacy 'assign:'/'job.skipCopilot' field and a new 'action:' block. Remove the legacy field(s) by hand once you've confirmed 'action:' reflects the intended behaviour, then re-run migrate."
            elif hasAction then
                // Already v2-shaped; just stamp the version so future steps
                // can detect it without the structural heuristic above.
                // onClosedIssue is passed through verbatim (including absent,
                // which already means "skip" under the current schema) — a
                // v2-shaped file's defaults are not this step's concern.
                Ok (Some (serializer.Serialize(toOutput root root.action root.job.onClosedIssue)), [])
            else
                match deriveAction root.job root.assign with
                | Error e -> Error e
                | Ok (action, actionWarnings) ->
                    // v1's default was 'create'; v2's is 'skip' — preserve old
                    // behaviour explicitly for any file that relied on the default.
                    let onClosedIssue =
                        if String.IsNullOrWhiteSpace root.job.onClosedIssue then "create" else root.job.onClosedIssue
                    Ok (Some (serializer.Serialize(toOutput root action onClosedIssue)), actionWarnings)
    with ex ->
        Error $"Failed to parse YAML: {ex.Message}"
