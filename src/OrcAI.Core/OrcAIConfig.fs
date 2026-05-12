module OrcAI.Core.OrcAIConfig

open System.IO.Abstractions
open System.Text.Json
open System.Text.Json.Serialization
open OrcAI.Core.Domain

// ---------------------------------------------------------------------------
// Config type
// ---------------------------------------------------------------------------

/// Layered configuration loaded from ~/.config/orcai/config.json (global)
/// and .orcai/config.json (local).  All fields are option types so that
/// absence is distinguishable from false/0.
type OrcAIConfig =
    { SkipCopilot      : bool option
      DefaultLabels    : string list option
      AutoCreateLabels : bool option
      MaxConcurrency   : int option
      ContinueOnError  : bool option
      DefaultOrg       : string option
      WritesPerMinute  : int option
      RateLimitRetries : int option
      Assign           : AssignConfig option
      Nudge            : NudgeConfig option }

/// All-None config — represents "no config loaded".
let empty : OrcAIConfig =
    { SkipCopilot      = None
      DefaultLabels    = None
      AutoCreateLabels = None
      MaxConcurrency   = None
      ContinueOnError  = None
      DefaultOrg       = None
      WritesPerMinute  = None
      RateLimitRetries = None
      Assign           = None
      Nudge            = None }

// ---------------------------------------------------------------------------
// Merge: local wins per field when Some; falls back to global otherwise.
// ---------------------------------------------------------------------------

/// Merge a global and a local config.  Local wins when a field is Some.
let merge (globalCfg: OrcAIConfig) (localCfg: OrcAIConfig) : OrcAIConfig =
    let pick l g = match l with Some _ -> l | None -> g
    let mergeAssign (l: AssignConfig option) (g: AssignConfig option) =
        match l, g with
        | None,   _       -> g
        | Some la, None   -> Some la
        | Some la, Some ga ->
            Some { To      = la.To      |> Option.orElse ga.To
                   Via     = la.Via     |> Option.orElse ga.Via
                   Comment = la.Comment |> Option.orElse ga.Comment }
    let mergeNudge (l: NudgeConfig option) (g: NudgeConfig option) =
        match l, g with
        | None,   _       -> g
        | Some ln, None   -> Some ln
        | Some ln, Some gn ->
            Some { Mode    = ln.Mode    |> Option.orElse gn.Mode
                   Comment = ln.Comment |> Option.orElse gn.Comment }
    { SkipCopilot      = pick localCfg.SkipCopilot      globalCfg.SkipCopilot
      DefaultLabels    = pick localCfg.DefaultLabels     globalCfg.DefaultLabels
      AutoCreateLabels = pick localCfg.AutoCreateLabels  globalCfg.AutoCreateLabels
      MaxConcurrency   = pick localCfg.MaxConcurrency    globalCfg.MaxConcurrency
      ContinueOnError  = pick localCfg.ContinueOnError   globalCfg.ContinueOnError
      DefaultOrg       = pick localCfg.DefaultOrg        globalCfg.DefaultOrg
      WritesPerMinute  = pick localCfg.WritesPerMinute   globalCfg.WritesPerMinute
      RateLimitRetries = pick localCfg.RateLimitRetries  globalCfg.RateLimitRetries
      Assign           = mergeAssign localCfg.Assign     globalCfg.Assign
      Nudge            = mergeNudge  localCfg.Nudge      globalCfg.Nudge }

// ---------------------------------------------------------------------------
// Path helpers
// ---------------------------------------------------------------------------

/// Path to the global config: ~/.config/orcai/config.json
let globalConfigPath (home: string) : string =
    System.IO.Path.Combine(home, ".config", "orcai", "config.json")

/// Path to the local config: <cwd>/.orcai/config.json
let localConfigPath (cwd: string) : string =
    System.IO.Path.Combine(cwd, ".orcai", "config.json")

// ---------------------------------------------------------------------------
// JSON DTO — uses Nullable<T> for value-type optionals so System.Text.Json
// can distinguish "absent" from false/0.  Must be non-private so that
// [<CLIMutable>] generates the parameterless constructor the deserialiser needs.
// ---------------------------------------------------------------------------

[<CLIMutable>]
type AssignConfigDto =
    { [<JsonPropertyName("to")>]
      To      : string option
      [<JsonPropertyName("via")>]
      Via     : string option
      [<JsonPropertyName("comment")>]
      Comment : string option }

[<CLIMutable>]
type NudgeConfigDto =
    { [<JsonPropertyName("mode")>]
      Mode    : string option
      [<JsonPropertyName("comment")>]
      Comment : string option }

[<CLIMutable>]
type OrcAIConfigDto =
    { [<JsonPropertyName("skipCopilot")>]
      SkipCopilot      : System.Nullable<bool>
      [<JsonPropertyName("defaultLabels")>]
      DefaultLabels    : string[] option
      [<JsonPropertyName("autoCreateLabels")>]
      AutoCreateLabels : System.Nullable<bool>
      [<JsonPropertyName("maxConcurrency")>]
      MaxConcurrency   : System.Nullable<int>
      [<JsonPropertyName("continueOnError")>]
      ContinueOnError  : System.Nullable<bool>
      [<JsonPropertyName("defaultOrg")>]
      DefaultOrg       : string option
      [<JsonPropertyName("writesPerMinute")>]
      WritesPerMinute  : System.Nullable<int>
      [<JsonPropertyName("rateLimitRetries")>]
      RateLimitRetries : System.Nullable<int>
      [<JsonPropertyName("assign")>]
      Assign           : AssignConfigDto option
      [<JsonPropertyName("nudge")>]
      Nudge            : NudgeConfigDto option }

let private jsonOptions =
    let opts = JsonSerializerOptions()
    opts.PropertyNameCaseInsensitive <- true
    opts.DefaultIgnoreCondition      <- JsonIgnoreCondition.WhenWritingNull
    opts

let private ofDto (dto: OrcAIConfigDto) : OrcAIConfig =
    let ofAssignDto (d: AssignConfigDto) : AssignConfig =
        { To = d.To; Via = d.Via; Comment = d.Comment }
    let ofNudgeDto (d: NudgeConfigDto) : NudgeConfig =
        { Mode = d.Mode; Comment = d.Comment }
    { SkipCopilot      = if dto.SkipCopilot.HasValue     then Some dto.SkipCopilot.Value     else None
      DefaultLabels    = dto.DefaultLabels |> Option.map Array.toList
      AutoCreateLabels = if dto.AutoCreateLabels.HasValue then Some dto.AutoCreateLabels.Value else None
      MaxConcurrency   = if dto.MaxConcurrency.HasValue   then Some dto.MaxConcurrency.Value   else None
      ContinueOnError  = if dto.ContinueOnError.HasValue  then Some dto.ContinueOnError.Value  else None
      DefaultOrg       = dto.DefaultOrg
      WritesPerMinute  = if dto.WritesPerMinute.HasValue  then Some dto.WritesPerMinute.Value  else None
      RateLimitRetries = if dto.RateLimitRetries.HasValue then Some dto.RateLimitRetries.Value else None
      Assign           = dto.Assign |> Option.map ofAssignDto
      Nudge            = dto.Nudge  |> Option.map ofNudgeDto }

// ---------------------------------------------------------------------------
// File I/O
// ---------------------------------------------------------------------------

/// Read a config file at `path`.
/// Returns `Ok empty` if the file does not exist.
/// Returns `Error msg` if the file exists but contains malformed JSON.
let readFile (fs: IFileSystem) (path: string) : Result<OrcAIConfig, string> =
    if not (fs.File.Exists(path)) then
        Ok empty
    else
        try
            let json = fs.File.ReadAllText(path)
            match JsonSerializer.Deserialize<OrcAIConfigDto>(json, jsonOptions) |> Option.ofObj with
            | None     -> Ok empty
            | Some dto -> Ok (ofDto dto)
        with
        | :? System.IO.FileNotFoundException ->
            Ok empty
        | ex ->
            Error $"Malformed config at '{path}': {ex.Message}"

/// Resolve the effective config by reading global then local, merging them.
/// IO errors other than file-not-found propagate as exceptions (by design —
/// permission errors are not silently swallowed).
let resolve (fs: IFileSystem) (home: string) (cwd: string) : OrcAIConfig =
    let globalPath = globalConfigPath home
    let localPath  = localConfigPath  cwd

    let globalCfg =
        match readFile fs globalPath with
        | Ok cfg  -> cfg
        | Error e ->
            eprintfn "Warning: %s" e
            empty

    let localCfg =
        match readFile fs localPath with
        | Ok cfg  -> cfg
        | Error e ->
            eprintfn "Warning: %s" e
            empty

    merge globalCfg localCfg
