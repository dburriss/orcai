module Orca.Cli.Program

open Argu
open Orca.Cli.Args

// ---------------------------------------------------------------------------
// Entry point — parses CLI arguments and dispatches to the appropriate
// command module in Orca.Core.
// ---------------------------------------------------------------------------

[<EntryPoint>]
let main argv =
    let parser = ArgumentParser.Create<OrcaArgs>(programName = "orca")
    try
        let results = parser.ParseCommandLine(inputs = argv, raiseOnUsage = true)
        match results.GetSubCommand() with
        | Run args ->
            let yamlFile = args.GetResult(RunArgs.Yaml_File)
            let verbose  = args.Contains(RunArgs.Verbose)
            failwith "not implemented: run command"
        | Cleanup args ->
            let yamlFile = args.GetResult(CleanupArgs.Yaml_File)
            let dryRun   = args.Contains(CleanupArgs.Dryrun)
            failwith "not implemented: cleanup command"
        | Info args ->
            let yamlFile  = args.GetResult(InfoArgs.Yaml_File)
            let noLock    = args.Contains(InfoArgs.No_Lock)
            let saveLock  = args.Contains(InfoArgs.Save_Lock)
            failwith "not implemented: info command"
        | Auth args ->
            match args.GetSubCommand() with
            | Pat patArgs ->
                let token = patArgs.GetResult(AuthPatArgs.Token)
                failwith "not implemented: auth pat"
            | App appArgs ->
                let appId = appArgs.GetResult(AuthAppArgs.App_Id)
                let key   = appArgs.GetResult(AuthAppArgs.Key)
                failwith "not implemented: auth app"
        0
    with
    | :? ArguParseException as ex ->
        eprintfn "%s" ex.Message
        1
    | ex ->
        eprintfn "Error: %s" ex.Message
        1
