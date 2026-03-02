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
    interface IArgParserTemplate with
        member a.Usage =
            match a with
            | Yaml_File _ -> "Path to the YAML job configuration file."
            | Verbose     -> "Enable verbose output."

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
    | [<Mandatory>] App_Id of id: string
    | [<Mandatory>] Key of path: string
    interface IArgParserTemplate with
        member a.Usage =
            match a with
            | App_Id _ -> "GitHub App ID."
            | Key _    -> "Path to the GitHub App private key file."

[<CliPrefix(CliPrefix.DoubleDash)>]
type AuthArgs =
    | [<CliPrefix(CliPrefix.None); SubCommand>] Pat of ParseResults<AuthPatArgs>
    | [<CliPrefix(CliPrefix.None); SubCommand>] App of ParseResults<AuthAppArgs>
    interface IArgParserTemplate with
        member a.Usage =
            match a with
            | Pat _ -> "Authenticate with a Personal Access Token."
            | App _ -> "Authenticate with a GitHub App."

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
