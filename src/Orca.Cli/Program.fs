module Orca.Cli.Program

open System
open System.Diagnostics
open Argu
open Spectre.Console
open Orca.Cli.Args
open Orca.Auth.PatAuth
open Orca.Auth.AppAuth
open Orca.Core.InfoCommand
open Orca.Core.Domain

// ---------------------------------------------------------------------------
// Entry point — parses CLI arguments and dispatches to the appropriate
// command module in Orca.Core.
// ---------------------------------------------------------------------------

/// Resolve the active GH_TOKEN using the following priority order:
///   1. Stored PAT config  (ORCA_PAT env var or ~/.config/orca/auth.json type=pat)
///   2. Stored App config  (env vars or ~/.config/orca/auth.json type=app)
///   3. GH_TOKEN environment variable
///   4. gh CLI ambient auth  (gh auth token)
let private resolveAuthContext () : Result<Orca.Core.AuthContext.IAuthContext, string> =
    // 1. Try PAT
    match loadToken () with
    | Ok _ -> Ok (PatAuthContext() :> Orca.Core.AuthContext.IAuthContext)
    | Error _ ->
        // 2. Try App
        match resolveConfig () with
        | Ok appCfg -> Ok (AppAuthContext(appCfg) :> Orca.Core.AuthContext.IAuthContext)
        | Error _ ->
            // 3. Fallback: GH_TOKEN env var
            match Environment.GetEnvironmentVariable("GH_TOKEN") |> Option.ofObj with
            | Some t when t.Length > 0 ->
                Ok ({ new Orca.Core.AuthContext.IAuthContext with
                          member _.GetToken() = async { return Ok t } })
            | _ ->
                // 4. Fallback: gh CLI ambient auth (gh auth token)
                try
                    let psi = ProcessStartInfo("gh", "auth token")
                    psi.RedirectStandardOutput <- true
                    psi.RedirectStandardError  <- true
                    psi.UseShellExecute        <- false
                    match Process.Start(psi) |> Option.ofObj with
                    | None -> Error "No GitHub credentials found. Run 'orca auth pat --token <tok>' or set GH_TOKEN."
                    | Some proc ->
                        let token = proc.StandardOutput.ReadToEnd().Trim()
                        proc.WaitForExit()
                        if proc.ExitCode = 0 && token.Length > 0 then
                            printfn "Using gh CLI authentication."
                            Ok ({ new Orca.Core.AuthContext.IAuthContext with
                                      member _.GetToken() = async { return Ok token } })
                        else
                            Error "No GitHub credentials found. Run 'orca auth pat --token <tok>' or set GH_TOKEN."
                with _ ->
                    Error "No GitHub credentials found. Run 'orca auth pat --token <tok>' or set GH_TOKEN."

/// Format an InfoResult for console output.
let private printInfoResult (result: InfoResult) =
    let (OrgName orgStr) = result.Lock.Project.Org
    let sourceLabel =
        match result.Source with
        | FromLockFile -> "[grey]lock file[/]"
        | FromGitHub   -> "[green]GitHub (live)[/]"

    // --- Metadata grid ---
    let grid = Grid()
    grid.AddColumn(GridColumn().PadRight(2)) |> ignore
    grid.AddColumn(GridColumn()) |> ignore

    let row (label: string) (value: string) =
        grid.AddRow([| Markup($"[bold]{label}[/]") :> Rendering.IRenderable; Markup(value) |]) |> ignore

    row "Source"    sourceLabel
    row "Locked at" (result.Lock.LockedAt.ToString("u"))
    row "YAML hash" $"[dim]{result.Lock.YamlHash}[/]"
    row "Project"   $"[link={result.Lock.Project.Url}]{orgStr} / {Markup.Escape(result.Lock.Project.Title)}[/] [dim](#{result.Lock.Project.Number})[/]"
    row "URL"       $"[dim]{result.Lock.Project.Url}[/]"
    row "Repos"     (string (List.length result.Lock.Repos))
    row "Issues"    (string (List.length result.Lock.Issues))
    row "PRs"       (string (List.length result.Lock.PullRequests))

    AnsiConsole.Write(grid)

    // --- Issues table ---
    if result.Lock.Issues.Length > 0 then
        AnsiConsole.WriteLine()
        let table = Table()
        table.Border <- TableBorder.Rounded
        table.AddColumn(TableColumn("[bold]Repo[/]"))              |> ignore
        table.AddColumn(TableColumn("[bold]Issue[/]").Centered())  |> ignore
        table.AddColumn(TableColumn("[bold]PR[/]").Centered())     |> ignore
        table.AddColumn(TableColumn("[bold]Assignees[/]"))         |> ignore

        for issue in result.Lock.Issues do
            let (RepoName r)    = issue.Repo
            let (IssueNumber n) = issue.Number
            let repoUrl         = $"https://github.com/{r}"
            let assignees =
                match issue.Assignees with
                | [] -> "[dim](unassigned)[/]"
                | xs -> String.concat ", " xs
            let prs =
                result.Lock.PullRequests
                |> List.filter (fun pr -> pr.ClosesIssue = issue.Number && pr.Repo = issue.Repo)
            let prText =
                match prs with
                | [] -> "[dim]-[/]"
                | ps ->
                    ps
                    |> List.map (fun pr -> let (PrNumber pn) = pr.Number in $"#{pn}")
                    |> String.concat ", "
            table.AddRow(
                [| Markup($"[cyan][link={repoUrl}]{Markup.Escape(r)}[/][/]") :> Rendering.IRenderable
                   Markup($"[yellow]#{n}[/]")
                   Markup(prText)
                   Markup(assignees) |]) |> ignore

        AnsiConsole.Write(table)


/// Returns Ok with the status output, or Error with the error message.
let private validateToken (token: string) : Result<string, string> =
    try
        let psi = ProcessStartInfo("gh", "auth status")
        psi.Environment.["GH_TOKEN"] <- token
        psi.RedirectStandardOutput <- true
        psi.RedirectStandardError  <- true
        psi.UseShellExecute        <- false
        match Process.Start(psi) |> Option.ofObj with
        | None ->
            Error "Failed to start 'gh' process."
        | Some proc ->
            let stdout = proc.StandardOutput.ReadToEnd()
            let stderr = proc.StandardError.ReadToEnd()
            proc.WaitForExit()
            if proc.ExitCode = 0 then
                Ok (stdout.Trim())
            else
                Error (stderr.Trim())
    with ex ->
        Error $"Could not run 'gh auth status': {ex.Message}"

[<EntryPoint>]
let main argv =
    let parser = ArgumentParser.Create<OrcaArgs>(programName = "orca")
    try
        let results = parser.ParseCommandLine(inputs = argv, raiseOnUsage = true)
        match results.GetSubCommand() with
        | Run args ->
            let yamlFile = args.GetResult(RunArgs.Yaml_File)
            let verbose  = args.Contains(RunArgs.Verbose)
            match resolveAuthContext () with
            | Error e ->
                eprintfn "Auth error: %s" e
                1
            | Ok authCtx ->
                let token =
                    authCtx.GetToken()
                    |> Async.RunSynchronously
                match token with
                | Error e ->
                    eprintfn "Auth error: %s" e
                    1
                | Ok ghToken ->
                    let client = Orca.GitHub.GhClient.GhCliClient(ghToken)
                    let deps : Orca.Core.RunCommand.RunDeps =
                                   { GhClient    = client :> Orca.Core.GhClient.IGhClient
                                     AuthContext = authCtx }
                    let input : Orca.Core.RunCommand.RunInput = { YamlPath = yamlFile; Verbose = verbose }
                    match Orca.Core.RunCommand.execute deps input with
                    | Error e ->
                        eprintfn "Error: %s" e
                        1
                    | Ok lock ->
                        printfn "Run complete. %d issue(s) processed across %d repo(s)."
                            lock.Issues.Length lock.Repos.Length
                        printfn "Lock file written."
                        0
        | Cleanup args ->
            let yamlFile = args.GetResult(CleanupArgs.Yaml_File)
            let dryRun   = args.Contains(CleanupArgs.Dryrun)
            match resolveAuthContext () with
            | Error e ->
                eprintfn "Auth error: %s" e
                1
            | Ok authCtx ->
                let token =
                    authCtx.GetToken()
                    |> Async.RunSynchronously
                match token with
                | Error e ->
                    eprintfn "Auth error: %s" e
                    1
                | Ok ghToken ->
                    let client = Orca.GitHub.GhClient.GhCliClient(ghToken)
                    let deps : Orca.Core.CleanupCommand.CleanupDeps =
                                   { GhClient    = client :> Orca.Core.GhClient.IGhClient
                                     AuthContext = authCtx }
                    let input : Orca.Core.CleanupCommand.CleanupInput = { YamlPath = yamlFile; DryRun = dryRun }
                    match Orca.Core.CleanupCommand.execute deps input with
                    | Error e ->
                        eprintfn "Error: %s" e
                        1
                    | Ok () ->
                        if dryRun then
                            printfn "Dry run complete. No changes were made."
                        else
                            printfn "Cleanup complete."
                        0
        | Info args ->
            let yamlFile = args.GetResult(InfoArgs.Yaml_File)
            let noLock   = args.Contains(InfoArgs.No_Lock)
            let saveLock = args.Contains(InfoArgs.Save_Lock)
            match resolveAuthContext () with
            | Error e ->
                eprintfn "Auth error: %s" e
                1
            | Ok authCtx ->
                let token =
                    authCtx.GetToken()
                    |> Async.RunSynchronously
                match token with
                | Error e ->
                    eprintfn "Auth error: %s" e
                    1
                | Ok ghToken ->
                    let client = Orca.GitHub.GhClient.GhCliClient(ghToken)
                    let deps   = { GhClient    = client :> Orca.Core.GhClient.IGhClient
                                   AuthContext = authCtx }
                    let input  = { YamlPath = yamlFile; NoLock = noLock; SaveLock = saveLock }
                    match execute deps input with
                    | Error e ->
                        eprintfn "Error: %s" e
                        1
                    | Ok result ->
                        printInfoResult result
                        0
        | Auth args ->
            match args.GetSubCommand() with
            | Pat patArgs ->
                let token = patArgs.GetResult(AuthPatArgs.Token)
                match storeToken token with
                | Error e ->
                    eprintfn "Error saving PAT: %s" e
                    1
                | Ok () ->
                    match validateToken token with
                    | Ok status ->
                        printfn "PAT saved and validated."
                        if status.Length > 0 then printfn "%s" status
                        0
                    | Error e ->
                        eprintfn "PAT saved but validation failed: %s" e
                        eprintfn "Ensure the token has the required scopes (project, repo)."
                        1
            | App appArgs ->
                let appId          = appArgs.GetResult(AuthAppArgs.App_Id)
                let key            = appArgs.GetResult(AuthAppArgs.Key)
                let installationId = appArgs.GetResult(AuthAppArgs.Installation_Id)
                let config         = { AppId = appId; PrivateKeyPath = key; InstallationId = installationId }
                match storeConfig config with
                | Error e ->
                    eprintfn "Error saving App config: %s" e
                    1
                | Ok () ->
                    // Exchange for an installation token and validate it.
                    let result =
                        (AppAuthContext(config) :> Orca.Core.AuthContext.IAuthContext)
                            .GetToken()
                        |> Async.RunSynchronously
                    match result with
                    | Error e ->
                        eprintfn "App config saved but token exchange failed: %s" e
                        1
                    | Ok installToken ->
                        match validateToken installToken with
                        | Ok status ->
                            printfn "GitHub App config saved and validated."
                            if status.Length > 0 then printfn "%s" status
                            0
                        | Error e ->
                            eprintfn "App config saved but validation failed: %s" e
                            1
    with
    | :? ArguParseException as ex ->
        eprintfn "%s" ex.Message
        1
    | ex ->
        eprintfn "Error: %s" ex.Message
        1
