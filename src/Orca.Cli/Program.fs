module Orca.Cli.Program

open System
open Argu
open SimpleExec
open Spectre.Console
open Orca.Cli.Args
open Orca.Auth.PatAuth
open Orca.Auth.AppAuth
open Orca.Auth.CreateAppCommand
open Orca.Core.Deps
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
/// `getEnv` is injected so the env-var reads are testable without mutation.
let resolveAuthContextWith (getEnv: string -> string option) : Result<Orca.Core.AuthContext.IAuthContext, string> =
    // 1. Try PAT
    match loadToken () with
    | Ok _ -> Ok (PatAuthContext() :> Orca.Core.AuthContext.IAuthContext)
    | Error _ ->
        // 2. Try App
        match resolveConfig () with
        | Ok appCfg -> Ok (AppAuthContext(appCfg) :> Orca.Core.AuthContext.IAuthContext)
        | Error _ ->
            // 3. Fallback: GH_TOKEN env var
            match getEnv "GH_TOKEN" |> Option.bind (fun t -> if t.Length > 0 then Some t else None) with
            | Some t ->
                Ok ({ new Orca.Core.AuthContext.IAuthContext with
                          member _.GetToken() = async { return Ok t } })
            | None ->
                // 4. Fallback: gh CLI ambient auth (gh auth token)
                try
                    let struct(token, _) =
                        Command.ReadAsync("gh", "auth token")
                        |> Async.AwaitTask
                        |> Async.RunSynchronously
                    let token = token.Trim()
                    if token.Length > 0 then
                        printfn "Using gh CLI authentication."
                        Ok ({ new Orca.Core.AuthContext.IAuthContext with
                                  member _.GetToken() = async { return Ok token } })
                    else
                        Error "No GitHub credentials found. Run 'orca auth pat --token <tok>' or set GH_TOKEN."
                with _ ->
                    Error "No GitHub credentials found. Run 'orca auth pat --token <tok>' or set GH_TOKEN."

let private resolveAuthContext () =
    resolveAuthContextWith (Environment.GetEnvironmentVariable >> Option.ofObj)

/// Resolve auth, obtain a token, create the gh client, and invoke `f`.
/// Returns 1 on any auth failure, otherwise returns the result of `f`.
let private withClient (f: OrcaDeps -> int) : int =
    match resolveAuthContext () with
    | Error e ->
        eprintfn "Auth error: %s" e
        1
    | Ok authCtx ->
        match authCtx.GetToken() |> Async.RunSynchronously with
        | Error e ->
            eprintfn "Auth error: %s" e
            1
        | Ok ghToken ->
            let client = Orca.GitHub.GhClient.GhCliClient(ghToken)
            let deps : OrcaDeps =
                { GhClient    = client :> Orca.Core.GhClient.IGhClient
                  AuthContext = authCtx }
            f deps

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

    row "Project"   $"[link={result.Lock.Project.Url}]{orgStr} / {Markup.Escape(result.Lock.Project.Title)}[/] [dim](#{result.Lock.Project.Number})[/]"
    row "URL"       $"[dim]{result.Lock.Project.Url}[/]"
    row "Source"    sourceLabel
    row "Locked at" (result.Lock.LockedAt.ToString("u"))
    row "YAML hash" $"[dim]{result.Lock.YamlHash}[/]"
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


/// Validate a token by running `gh auth status` with it injected as GH_TOKEN.
/// Returns Ok with the status output, or Error with the error message.
let private validateToken (token: string) : Result<string, string> =
    try
        let struct(stdout, stderr) =
            Command.ReadAsync(
                "gh", "auth status",
                configureEnvironment = Action<Collections.Generic.IDictionary<string,string>>(fun env ->
                    env.["GH_TOKEN"] <- token))
            |> Async.AwaitTask
            |> Async.RunSynchronously
        if stdout.Trim().Length > 0 then Ok (stdout.Trim())
        else Ok (stderr.Trim())
    with
    | :? ExitCodeException as ex ->
        Error ex.Message
    | ex ->
        Error $"Could not run 'gh auth status': {ex.Message}"

[<EntryPoint>]
let main argv =
    let parser = ArgumentParser.Create<OrcaArgs>(programName = "orca")
    try
        let results = parser.ParseCommandLine(inputs = argv, raiseOnUsage = true)
        match results.GetSubCommand() with
        | Run args ->
            let yamlFile         = args.GetResult(RunArgs.Yaml_File)
            let verbose          = args.Contains(RunArgs.Verbose)
            let autoCreateLabels = args.Contains(RunArgs.Auto_Create_Labels)
            withClient (fun deps ->
                let input : Orca.Core.RunCommand.RunInput =
                    { YamlPath         = yamlFile
                      Verbose          = verbose
                      AutoCreateLabels = autoCreateLabels }
                match Orca.Core.RunCommand.execute deps input with
                | Error e ->
                    eprintfn "Error: %s" e
                    1
                | Ok lock ->
                    printfn "Run complete. %d issue(s) processed across %d repo(s)."
                        lock.Issues.Length lock.Repos.Length
                    printfn "Lock file written."
                    0)
        | Cleanup args ->
            let yamlFile = args.GetResult(CleanupArgs.Yaml_File)
            let dryRun   = args.Contains(CleanupArgs.Dryrun)
            withClient (fun deps ->
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
                    0)
        | Info args ->
            let yamlFile = args.GetResult(InfoArgs.Yaml_File)
            let noLock   = args.Contains(InfoArgs.No_Lock)
            let saveLock = args.Contains(InfoArgs.Save_Lock)
            withClient (fun deps ->
                let input  = { YamlPath = yamlFile; NoLock = noLock; SaveLock = saveLock }
                match Orca.Core.InfoCommand.execute deps input with
                | Error e ->
                    eprintfn "Error: %s" e
                    1
                | Ok result ->
                    printInfoResult result
                    0)
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
            | Create_App createArgs ->
                let appName = createArgs.TryGetResult(AuthCreateAppArgs.App_Name) |> Option.defaultValue "orca"
                let org     = createArgs.TryGetResult(AuthCreateAppArgs.Org)
                let port    = createArgs.TryGetResult(AuthCreateAppArgs.Port) |> Option.defaultValue 9876
                let input   = { AppName = appName; Org = org; Port = port }
                match Orca.Auth.CreateAppCommand.execute input |> Async.RunSynchronously with
                | Error e ->
                    eprintfn "Error: %s" e
                    1
                | Ok created ->
                    printfn "GitHub App '%s' created (ID: %s)." created.Name created.Id
                    printfn "Private key saved to: %s" created.PemPath
                    printfn ""
                    // Prompt for installation ID so we can complete the auth config.
                    let isInteractive =
                        try not Console.IsInputRedirected
                        with _ -> false
                    if isInteractive then
                        printf "Enter the installation ID (press Enter to skip): "
                        let line = Console.ReadLine() |> Option.ofObj |> Option.defaultValue ""
                        if not (String.IsNullOrWhiteSpace(line)) then
                            let installId = line.Trim()
                            let cfg = { AppId = created.Id; PrivateKeyPath = created.PemPath; InstallationId = installId }
                            match storeConfig cfg with
                            | Error e ->
                                eprintfn "Warning: could not save installation ID: %s" e
                            | Ok () ->
                                // Validate the full config.
                                let tokenResult =
                                    (AppAuthContext(cfg) :> Orca.Core.AuthContext.IAuthContext)
                                        .GetToken()
                                    |> Async.RunSynchronously
                                match tokenResult with
                                | Error e ->
                                    eprintfn "Installation ID saved but token exchange failed: %s" e
                                | Ok tok ->
                                    match validateToken tok with
                                    | Ok _  -> printfn "App credentials saved and validated."
                                    | Error e -> eprintfn "Credentials saved but validation failed: %s" e
                        else
                            printfn "Skipped. Run the following after installing the app:"
                            printfn "  orca auth app --app-id %s --key \"%s\" --installation-id <id>" created.Id created.PemPath
                    else
                        printfn "Install the app at: https://github.com/apps/%s" (Uri.EscapeDataString(created.Name))
                        printfn "Then run:"
                        printfn "  orca auth app --app-id %s --key \"%s\" --installation-id <id>" created.Id created.PemPath
                    0
    with
    | :? ArguParseException as ex ->
        eprintfn "%s" ex.Message
        1
    | ex ->
        eprintfn "Error: %s" ex.Message
        1
