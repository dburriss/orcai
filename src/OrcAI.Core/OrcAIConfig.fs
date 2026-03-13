module OrcAI.Core.OrcAIConfig

open System.IO.Abstractions
open System.Text.Json
open System.Text.Json.Serialization

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
      DefaultOrg       : string option }

/// All-None config — represents "no config loaded".
let empty : OrcAIConfig =
    { SkipCopilot      = None
      DefaultLabels    = None
      AutoCreateLabels = None
      MaxConcurrency   = None
      ContinueOnError  = None
      DefaultOrg       = None }

// ---------------------------------------------------------------------------
// Merge: local wins per field when Some; falls back to global otherwise.
// ---------------------------------------------------------------------------

/// Merge a global and a local config.  Local wins when a field is Some.
let merge (globalCfg: OrcAIConfig) (localCfg: OrcAIConfig) : OrcAIConfig =
    let pick l g = match l with Some _ -> l | None -> g
    { SkipCopilot      = pick localCfg.SkipCopilot      globalCfg.SkipCopilot
      DefaultLabels    = pick localCfg.DefaultLabels     globalCfg.DefaultLabels
      AutoCreateLabels = pick localCfg.AutoCreateLabels  globalCfg.AutoCreateLabels
      MaxConcurrency   = pick localCfg.MaxConcurrency    globalCfg.MaxConcurrency
      ContinueOnError  = pick localCfg.ContinueOnError   globalCfg.ContinueOnError
      DefaultOrg       = pick localCfg.DefaultOrg        globalCfg.DefaultOrg }

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
      DefaultOrg       : string option }

let private jsonOptions =
    let opts = JsonSerializerOptions()
    opts.PropertyNameCaseInsensitive <- true
    opts.DefaultIgnoreCondition      <- JsonIgnoreCondition.WhenWritingNull
    opts

let private ofDto (dto: OrcAIConfigDto) : OrcAIConfig =
    { SkipCopilot      = if dto.SkipCopilot.HasValue     then Some dto.SkipCopilot.Value     else None
      DefaultLabels    = dto.DefaultLabels |> Option.map Array.toList
      AutoCreateLabels = if dto.AutoCreateLabels.HasValue then Some dto.AutoCreateLabels.Value else None
      MaxConcurrency   = if dto.MaxConcurrency.HasValue   then Some dto.MaxConcurrency.Value   else None
      ContinueOnError  = if dto.ContinueOnError.HasValue  then Some dto.ContinueOnError.Value  else None
      DefaultOrg       = dto.DefaultOrg }

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
