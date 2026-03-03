module Orca.Tool.Args

open Argu

// ---------------------------------------------------------------------------
// Argu argument definitions for every subcommand.
// Each DU case maps to a subcommand described in ARCHITECTURE.md.
// ---------------------------------------------------------------------------

[<CliPrefix(CliPrefix.DoubleDash)>]
type RunArgs =
    | [<MainCommand; Mandatory>] Yaml_File of path: string
    | Verbose
    | Auto_Create_Labels
    | Skip_Copilot
    | Skip_Lock
    | Json
    interface IArgParserTemplate with
        member a.Usage =
            match a with
            | Yaml_File _        -> "Path to the YAML job configuration file."
            | Verbose            -> "Enable verbose output."
            | Auto_Create_Labels -> "Create any labels that don't exist in a repo before adding them to issues."
            | Skip_Copilot       -> "Skip assigning @copilot to issues."
            | Skip_Lock          -> "Bypass the lock file and always fetch live state from GitHub."
            | Json               -> "Emit machine-readable JSON output to stdout."

[<CliPrefix(CliPrefix.DoubleDash)>]
type CleanupArgs =
    | [<MainCommand; Mandatory>] Yaml_File of path: string
    | Dryrun
    | Force
    | Json
    interface IArgParserTemplate with
        member a.Usage =
            match a with
            | Yaml_File _ -> "Path to the YAML job configuration file."
            | Dryrun      -> "Preview what would be deleted without making any changes."
            | Force       -> "Skip the confirmation prompt and proceed with cleanup immediately."
            | Json        -> "Emit machine-readable JSON output to stdout. If --dryrun is also set, lists resources that would be cleaned up."

[<CliPrefix(CliPrefix.DoubleDash)>]
type InfoArgs =
    | [<MainCommand; Mandatory>] Yaml_File of path: string
    | Skip_Lock
    | Save_Lock
    | Json
    interface IArgParserTemplate with
        member a.Usage =
            match a with
            | Yaml_File _ -> "Path to the YAML job configuration file."
            | Skip_Lock   -> "Bypass the lock file and always fetch live state."
            | Save_Lock   -> "Write a new lock file after fetching live state."
            | Json        -> "Emit machine-readable JSON output to stdout."

[<CliPrefix(CliPrefix.DoubleDash)>]
type AuthPatArgs =
    | [<Mandatory>] Token of token: string
    interface IArgParserTemplate with
        member a.Usage =
            match a with
            | Token _ -> "GitHub Personal Access Token."

[<CliPrefix(CliPrefix.DoubleDash)>]
type AuthAppArgs =
    | [<Mandatory>] App_Id          of id: string
    | [<Mandatory>] Key             of path: string
    | [<Mandatory>] Installation_Id of id: string
    interface IArgParserTemplate with
        member a.Usage =
            match a with
            | App_Id _          -> "GitHub App ID."
            | Key _             -> "Path to the GitHub App private key file."
            | Installation_Id _ -> "GitHub App installation ID for the target organisation."

[<CliPrefix(CliPrefix.DoubleDash)>]
type AuthCreateAppArgs =
    | App_Name      of name: string
    | Org           of org: string
    | Port          of port: int
    interface IArgParserTemplate with
        member a.Usage =
            match a with
            | App_Name _ -> "Name of the GitHub App to register (default: orca)."
            | Org _      -> "Register the app under an organisation instead of your personal account."
            | Port _     -> "Local callback port for the OAuth redirect (default: 9876)."

[<CliPrefix(CliPrefix.DoubleDash)>]
type AuthArgs =
    | [<CliPrefix(CliPrefix.None); SubCommand>] Pat        of ParseResults<AuthPatArgs>
    | [<CliPrefix(CliPrefix.None); SubCommand>] App        of ParseResults<AuthAppArgs>
    | [<CliPrefix(CliPrefix.None); SubCommand>] Create_App of ParseResults<AuthCreateAppArgs>
    interface IArgParserTemplate with
        member a.Usage =
            match a with
            | Pat _        -> "Authenticate with a Personal Access Token."
            | App _        -> "Authenticate with a GitHub App."
            | Create_App _ -> "Register a new GitHub App via the manifest flow and store its credentials."

[<CliPrefix(CliPrefix.DoubleDash)>]
type GenerateArgs =
    | Name         of name: string
    | Org          of org:  string
    | Repo         of repo: string
    | Output       of path: string
    | Skip_Copilot
    | Interactive
    interface IArgParserTemplate with
        member a.Usage =
            match a with
            | Name _        -> "Job name (used as project title and issue title). Required unless --interactive."
            | Org _         -> "GitHub organisation. Required unless --interactive."
            | Repo _        -> "Repo short-name to include (repeatable, e.g. --repo my-repo). Optional."
            | Output _      -> "Output YAML file path. Defaults to <slug>.yml in the current directory."
            | Skip_Copilot  -> "Set skipCopilot: true in the generated config."
            | Interactive   -> "Prompt for any missing values and select repos via an interactive TUI."

[<CliPrefix(CliPrefix.None)>]
type OrcaArgs =
    | [<SubCommand>] Run      of ParseResults<RunArgs>
    | [<SubCommand>] Cleanup  of ParseResults<CleanupArgs>
    | [<SubCommand>] Info     of ParseResults<InfoArgs>
    | [<SubCommand>] Auth     of ParseResults<AuthArgs>
    | [<SubCommand>] Generate of ParseResults<GenerateArgs>
    interface IArgParserTemplate with
        member a.Usage =
            match a with
            | Run _      -> "Execute a job defined in a YAML configuration file."
            | Cleanup _  -> "Tear down everything created by a run command."
            | Info _     -> "Display the current state of a job."
            | Auth _     -> "Configure authentication for Orca."
            | Generate _ -> "Generate a YAML job config from a name, org, and optional repo list."
