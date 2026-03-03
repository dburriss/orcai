module Orca.Cli.Args

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
    interface IArgParserTemplate with
        member a.Usage =
            match a with
            | Yaml_File _        -> "Path to the YAML job configuration file."
            | Verbose            -> "Enable verbose output."
            | Auto_Create_Labels -> "Create any labels that don't exist in a repo before adding them to issues."
            | Skip_Copilot       -> "Skip assigning @copilot to issues."

[<CliPrefix(CliPrefix.DoubleDash)>]
type CleanupArgs =
    | [<MainCommand; Mandatory>] Yaml_File of path: string
    | Dryrun
    interface IArgParserTemplate with
        member a.Usage =
            match a with
            | Yaml_File _ -> "Path to the YAML job configuration file."
            | Dryrun      -> "Preview what would be deleted without making any changes."

[<CliPrefix(CliPrefix.DoubleDash)>]
type InfoArgs =
    | [<MainCommand; Mandatory>] Yaml_File of path: string
    | No_Lock
    | Save_Lock
    interface IArgParserTemplate with
        member a.Usage =
            match a with
            | Yaml_File _ -> "Path to the YAML job configuration file."
            | No_Lock     -> "Bypass the lock file and always fetch live state."
            | Save_Lock   -> "Write a new lock file after fetching live state."

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

[<CliPrefix(CliPrefix.None)>]
type OrcaArgs =
    | [<SubCommand>] Run     of ParseResults<RunArgs>
    | [<SubCommand>] Cleanup of ParseResults<CleanupArgs>
    | [<SubCommand>] Info    of ParseResults<InfoArgs>
    | [<SubCommand>] Auth    of ParseResults<AuthArgs>
    interface IArgParserTemplate with
        member a.Usage =
            match a with
            | Run _     -> "Execute a job defined in a YAML configuration file."
            | Cleanup _ -> "Tear down everything created by a run command."
            | Info _    -> "Display the current state of a job."
            | Auth _    -> "Configure authentication for Orca."
