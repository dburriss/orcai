module OrcAI.Tool.Program

open System
open System.Text.Json
open Argu
open SimpleExec
open Spectre.Console
open OrcAI.Tool.Args
open OrcAI.Auth.PatAuth
open OrcAI.Auth.AppAuth
open OrcAI.Auth.AuthConfig
open OrcAI.Auth.CreateAppCommand
open OrcAI.Core.Deps
open OrcAI.Core.OrcAIConfig
open OrcAI.Core.InfoCommand
open OrcAI.Core.Domain
open OrcAI.Core.GenerateCommand
open System.IO.Abstractions
open Testably.Abstractions

// ---------------------------------------------------------------------------
// Entry point — parses CLI arguments and dispatches to the appropriate
// command module in OrcAI.Core.
// ---------------------------------------------------------------------------

/// Resolve the active GH_TOKEN using the following priority order:
///   1. Stored PAT config  (ORCAI_PAT env var or ~/.config/orcai/auth.json type=pat)
///   2. Stored App config  (env vars or ~/.config/orcai/auth.json type=app)
///   3. GH_TOKEN environment variable
///   4. gh CLI ambient auth  (gh auth token)
/// `getEnv` is injected so the env-var reads are testable without mutation.
let resolveAuthContextWith (getEnv: string -> string option) : Result<OrcAI.Core.AuthContext.IAuthContext, string> =
    // 1. Try PAT
    match loadToken () with
    | Ok _ -> Ok (PatAuthContext() :> OrcAI.Core.AuthContext.IAuthContext)
    | Error _ ->
        // 2. Try App
        match resolveConfig () with
        | Ok appCfg -> Ok (AppAuthContext(appCfg) :> OrcAI.Core.AuthContext.IAuthContext)
        | Error _ ->
            // 3. Fallback: GH_TOKEN env var
            match getEnv "GH_TOKEN" |> Option.bind (fun t -> if t.Length > 0 then Some t else None) with
            | Some t ->
                Ok ({ new OrcAI.Core.AuthContext.IAuthContext with
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
                        Ok ({ new OrcAI.Core.AuthContext.IAuthContext with
                                  member _.GetToken() = async { return Ok token } })
                    else
                        Error "No GitHub credentials found. Run 'orcai auth pat --token <tok>' or set GH_TOKEN."
                with _ ->
                    Error "No GitHub credentials found. Run 'orcai auth pat --token <tok>' or set GH_TOKEN."

let private resolveAuthContext () =
    resolveAuthContextWith (Environment.GetEnvironmentVariable >> Option.ofObj)

/// Resolve auth, obtain a token, create the gh client, and invoke `f`.
/// Returns 1 on any auth failure, otherwise returns the result of `f`.
let private withClient (f: OrcAIDeps -> int) : int =
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
            let client = OrcAI.GitHub.GhClient.GhCliClient(ghToken)
            let fs     = RealFileSystem() :> IFileSystem
            let home   = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile)
            let cwd    = System.Environment.CurrentDirectory
            let cfg    = OrcAI.Core.OrcAIConfig.resolve fs home cwd
            let deps : OrcAIDeps =
                { GhClient    = client :> OrcAI.Core.GhClient.IGhClient
                  AuthContext = authCtx
                  FileSystem  = fs
                  Config      = cfg }
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


/// Shared JSON serializer options — camelCase, indented, no trailing nulls.
let private jsonOptions =
    JsonSerializerOptions(WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase)

/// Emit JSON for `orcai info --json`.
let private printInfoJson (result: InfoResult) =
    let lock = result.Lock
    let source =
        match result.Source with
        | FromLockFile -> "lock file"
        | FromGitHub   -> "github"
    let issues =
        lock.Issues |> List.map (fun issue ->
            let (RepoName r)    = issue.Repo
            let (IssueNumber n) = issue.Number
            let prs =
                lock.PullRequests
                |> List.filter (fun pr -> pr.ClosesIssue = issue.Number && pr.Repo = issue.Repo)
                |> List.map (fun pr -> let (PrNumber pn) = pr.Number in pn)
            {| repo      = r
               issueNumber = n
               prNumbers  = prs
               assignees  = issue.Assignees |})
    let (OrgName orgStr) = lock.Project.Org
    let doc =
        {| project    = $"{orgStr} / {lock.Project.Title}"
           url        = lock.Project.Url
           source     = source
           lockedAt   = lock.LockedAt.ToString("o")
           yamlHash   = lock.YamlHash
           repoCount  = List.length lock.Repos
           issueCount = List.length lock.Issues
           prCount    = List.length lock.PullRequests
           issues     = issues |}
    printfn "%s" (JsonSerializer.Serialize(doc, jsonOptions))

/// Emit JSON for `orcai run --json` — always a filename-keyed dictionary.
let private printRunJsonMulti (results: Map<string, Result<OrcAI.Core.RunCommand.RunResult, string>>) =
    let entries =
        results
        |> Map.toSeq
        |> Seq.map (fun (path, r) ->
            let value =
                match r with
                | Error e ->
                    box {| error = e |}
                | Ok result ->
                    let created  = result.Results |> List.filter (fun r -> r.Outcome = OrcAI.Core.RunCommand.Created)       |> List.length
                    let existing = result.Results |> List.filter (fun r -> r.Outcome = OrcAI.Core.RunCommand.AlreadyExisted) |> List.length
                    let issues =
                        result.Results |> List.map (fun r ->
                            let (RepoName repo)   = r.Issue.Repo
                            let (IssueNumber num) = r.Issue.Number
                            let status =
                                match r.Outcome with
                                | OrcAI.Core.RunCommand.Created        -> "created"
                                | OrcAI.Core.RunCommand.AlreadyExisted -> "alreadyExisted"
                            {| repo = repo; issueNumber = num; status = status |})
                    box {| created = created; alreadyExisted = existing; repos = issues |}
            path, value)
        |> dict
    printfn "%s" (JsonSerializer.Serialize(entries, jsonOptions))

/// Emit JSON for `orcai cleanup --json`.
let private printCleanupJson (result: OrcAI.Core.CleanupCommand.CleanupResult) =
    let resources =
        result.Resources
        |> List.choose (fun r ->
            match r with
            | OrcAI.Core.CleanupCommand.CleanedPr(repo, prN) ->
                Some {| ``type`` = "pr"; repo = Some repo; number = prN; org = None; name = None |}
            | OrcAI.Core.CleanupCommand.CleanedIssue(repo, issueN) ->
                Some {| ``type`` = "issue"; repo = Some repo; number = issueN; org = None; name = None |}
            | OrcAI.Core.CleanupCommand.CleanedProject(org, name, num) ->
                Some {| ``type`` = "project"; repo = None; number = num; org = Some org; name = Some name |}
            | OrcAI.Core.CleanupCommand.RemovedLockFile ->
                None)
    let doc =
        {| dryRun    = result.DryRun
           resources = resources |}
    printfn "%s" (JsonSerializer.Serialize(doc, jsonOptions))

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
    let parser = ArgumentParser.Create<OrcAIArgs>(programName = "orcai")
    try
        let results = parser.ParseCommandLine(inputs = argv, raiseOnUsage = true)
        match results.GetSubCommand() with
        | Run args ->
            let pattern         = args.GetResult(RunArgs.Yaml_File)
            let verbose         = args.Contains(RunArgs.Verbose)
            let autoCreateLabels= args.Contains(RunArgs.Auto_Create_Labels)
            let skipCopilot     = args.Contains(RunArgs.Skip_Copilot)
            let skipLock        = args.Contains(RunArgs.Skip_Lock)
            let maxConcurrency  = args.TryGetResult(RunArgs.Max_Concurrency) |> Option.defaultValue 4
            let noParallel      = args.Contains(RunArgs.No_Parallel)
            let continueOnError = args.Contains(RunArgs.Continue_On_Error)
            let json            = args.Contains(RunArgs.Json)
            let cwd             = System.Environment.CurrentDirectory
            match OrcAI.Core.FileGlob.expand cwd pattern with
            | Error e ->
                eprintfn "Error: %s" e
                1
            | Ok paths ->
            withClient (fun deps ->
                // Apply config fallbacks — CLI flags win, then config, then built-in defaults.
                let effectiveSkipCopilot     = skipCopilot     || (deps.Config.SkipCopilot      |> Option.defaultValue false)
                let effectiveAutoCreateLabels = autoCreateLabels || (deps.Config.AutoCreateLabels |> Option.defaultValue false)
                let effectiveContinueOnError  = continueOnError  || (deps.Config.ContinueOnError  |> Option.defaultValue false)
                let effectiveMaxConcurrency   =
                    match args.TryGetResult(RunArgs.Max_Concurrency) with
                    | Some v -> v
                    | None   -> deps.Config.MaxConcurrency |> Option.defaultValue 4
                let input : OrcAI.Core.RunCommand.RunInput =
                    { YamlPath         = ""   // overridden per-file in execute
                      Verbose          = verbose
                      AutoCreateLabels = effectiveAutoCreateLabels
                      SkipCopilot      = effectiveSkipCopilot
                      SkipLock         = skipLock
                      MaxConcurrency   = effectiveMaxConcurrency
                      NoParallel       = noParallel
                      ContinueOnError  = effectiveContinueOnError
                      DefaultLabels    = deps.Config.DefaultLabels |> Option.defaultValue [] }
                let results =
                    OrcAI.Core.RunCommand.execute deps paths input
                    |> Async.RunSynchronously
                if json then
                    printRunJsonMulti results
                else
                    for KeyValue(path, r) in results do
                        printfn "--- %s ---" path
                        match r with
                        | Error e ->
                            eprintfn "  Error: %s" e
                        | Ok result ->
                            match result.Source with
                            | OrcAI.Core.RunCommand.FromLockFile ->
                                printfn "  Nothing to do — lock file is up to date."
                            | OrcAI.Core.RunCommand.FullRun ->
                                let created  = result.Results |> List.filter (fun r -> r.Outcome = OrcAI.Core.RunCommand.Created)      |> List.length
                                let existing = result.Results |> List.filter (fun r -> r.Outcome = OrcAI.Core.RunCommand.AlreadyExisted) |> List.length
                                printfn "  Run complete. %d issue(s) created, %d already existed across %d repo(s). Lock file written."
                                    created existing result.Lock.Repos.Length
                            if verbose && not result.Results.IsEmpty then
                                AnsiConsole.WriteLine()
                                let table = Table()
                                table.Border <- TableBorder.Rounded
                                table.AddColumn(TableColumn("[bold]Repo[/]"))             |> ignore
                                table.AddColumn(TableColumn("[bold]Issue[/]").Centered()) |> ignore
                                table.AddColumn(TableColumn("[bold]Status[/]"))           |> ignore
                                for r in result.Results do
                                    let (RepoName repo)    = r.Issue.Repo
                                    let (IssueNumber num)  = r.Issue.Number
                                    let repoUrl            = $"https://github.com/{repo}"
                                    let statusMarkup =
                                        match r.Outcome with
                                        | OrcAI.Core.RunCommand.Created       -> "[green]created[/]"
                                        | OrcAI.Core.RunCommand.AlreadyExisted -> "[grey]already existed[/]"
                                    table.AddRow(
                                        [| Markup($"[cyan][link={repoUrl}]{Markup.Escape(repo)}[/][/]") :> Rendering.IRenderable
                                           Markup($"[yellow]#{num}[/]")
                                           Markup(statusMarkup) |]) |> ignore
                                AnsiConsole.Write(table)
                let anyError = results |> Map.exists (fun _ r -> match r with Error _ -> true | Ok _ -> false)
                if anyError then 1 else 0)
        | Cleanup args ->
            let yamlFile = args.GetResult(CleanupArgs.Yaml_File)
            let dryRun   = args.Contains(CleanupArgs.Dryrun)
            let force    = args.Contains(CleanupArgs.Force)
            let json     = args.Contains(CleanupArgs.Json)
            // Require confirmation unless --force or --dryrun is given (or stdin is redirected).
            let confirmed =
                dryRun || force ||
                (try Console.IsInputRedirected with _ -> true) ||
                AnsiConsole.Confirm("[yellow]This will permanently delete the project, issues, and PRs. Proceed?[/]", defaultValue = false)
            if not confirmed then
                printfn "Aborted."
                0
            else
            withClient (fun deps ->
                let input : OrcAI.Core.CleanupCommand.CleanupInput = { YamlPath = yamlFile; DryRun = dryRun }
                match OrcAI.Core.CleanupCommand.execute deps input with
                | Error e ->
                    eprintfn "Error: %s" e
                    1
                | Ok result ->
                    if json then
                        printCleanupJson result
                    else
                        // Print per-resource progress lines (mirrors old inline behaviour)
                        for resource in result.Resources do
                            match resource with
                            | OrcAI.Core.CleanupCommand.CleanedPr(repo, prN) ->
                                if dryRun then printfn "DRY RUN: Would close PR #%d in %s" prN repo
                                else printfn "Closed PR #%d in %s" prN repo
                            | OrcAI.Core.CleanupCommand.CleanedIssue(repo, issueN) ->
                                if dryRun then printfn "DRY RUN: Would delete issue #%d in %s" issueN repo
                                else printfn "Deleted issue #%d in %s" issueN repo
                            | OrcAI.Core.CleanupCommand.CleanedProject(org, name, num) ->
                                if dryRun then printfn "DRY RUN: Would delete project '%s' (#%d) from '%s'" name num org
                                else printfn "Deleted project '%s' (#%d) from '%s'" name num org
                            | OrcAI.Core.CleanupCommand.RemovedLockFile ->
                                printfn "Removed lock file."
                        if dryRun then
                            printfn "Dry run complete. No changes were made."
                        else
                            printfn "Cleanup complete."
                    0)
        | Info args ->
            let yamlFile  = args.GetResult(InfoArgs.Yaml_File)
            let skipLock  = args.Contains(InfoArgs.Skip_Lock)
            let saveLock  = args.Contains(InfoArgs.Save_Lock)
            let json      = args.Contains(InfoArgs.Json)
            withClient (fun deps ->
                let input  = { YamlPath = yamlFile; SkipLock = skipLock; SaveLock = saveLock }
                match OrcAI.Core.InfoCommand.execute deps input with
                | Error e ->
                    eprintfn "Error: %s" e
                    1
                | Ok result ->
                    if json then printInfoJson result
                    else printInfoResult result
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
                // Profile name is derived from the App ID.
                let profileName    = appId
                match storeConfig profileName config with
                | Error e ->
                    eprintfn "Error saving App config: %s" e
                    1
                | Ok () ->
                    // Exchange for an installation token and validate it.
                    let result =
                        (AppAuthContext(config) :> OrcAI.Core.AuthContext.IAuthContext)
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
                let org     = createArgs.TryGetResult(AuthCreateAppArgs.Org)
                let appName =
                    match createArgs.TryGetResult(AuthCreateAppArgs.App_Name), org with
                    | Some name, _      -> Some name
                    | None, Some orgVal -> Some $"orca-{orgVal}-gh-app"
                    | None, None        -> None
                let port    = createArgs.TryGetResult(AuthCreateAppArgs.Port) |> Option.defaultValue 9876
                match appName with
                | None ->
                    eprintfn "Error: either --org or --app-name must be supplied."
                    1
                | Some name ->
                let input   = { AppName = name; Org = org; Port = port }
                match OrcAI.Auth.CreateAppCommand.execute input |> Async.RunSynchronously with
                | Error e ->
                    eprintfn "Error: %s" e
                    1
                | Ok created ->
                    printfn "GitHub App '%s' created (ID: %s)." created.Name created.Id
                    printfn "Private key saved to: %s" created.PemPath
                    printfn ""
                    // Helper to print how to find the installation ID.
                    let printInstallInstructions () =
                        let appSlug = Uri.EscapeDataString(created.Slug)
                        let orgSeg  = org |> Option.map Uri.EscapeDataString |> Option.defaultValue "<org>"
                        printfn "NEXT STEP: Install the app and find the installation ID"
                        printfn "---------------------------------------------------------"
                        printfn "  1. Open the app installation page in your browser:"
                        printfn "       https://github.com/apps/%s" appSlug
                        printfn "  2. Click 'Install' (or 'Configure' if already installed) next to your org."
                        printfn "  3. Select the repositories OrcAI should access and confirm."
                        printfn "  4. After installation GitHub redirects you to a URL like:"
                        printfn "       https://github.com/organizations/%s/settings/installations/<ID>" orgSeg
                        printfn "     The number at the end is the installation ID."
                        printfn "  5. Alternatively, visit:"
                        printfn "       https://github.com/organizations/%s/settings/installations" orgSeg
                        printfn "     and click 'Configure' next to '%s' — the ID is in the URL." created.Name
                        printfn ""
                    // Prompt for installation ID so we can complete the auth config.
                    let isInteractive =
                        try not Console.IsInputRedirected
                        with _ -> false
                    if isInteractive then
                        printInstallInstructions ()
                        printf "Enter the installation ID (press Enter to skip): "
                        let line = Console.ReadLine() |> Option.ofObj |> Option.defaultValue ""
                        if not (String.IsNullOrWhiteSpace(line)) then
                            let installId = line.Trim()
                            let cfg = { AppId = created.Id; PrivateKeyPath = created.PemPath; InstallationId = installId }
                            match storeConfig name cfg with
                            | Error e ->
                                eprintfn "Warning: could not save installation ID: %s" e
                            | Ok () ->
                                // Validate the full config.
                                let tokenResult =
                                    (AppAuthContext(cfg) :> OrcAI.Core.AuthContext.IAuthContext)
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
                            printfn "Skipped. Once you have the installation ID, run:"
                            printfn "  orcai auth app --app-id %s --key \"%s\" --installation-id <id>" created.Id created.PemPath
                    else
                        printInstallInstructions ()
                        printfn "Then complete the config by running:"
                        printfn "  orcai auth app --app-id %s --key \"%s\" --installation-id <id>" created.Id created.PemPath
                    0
            | Switch switchArgs ->
                let profileName = switchArgs.GetResult(AuthSwitchArgs.Profile)
                match readConfig () with
                | Error e ->
                    eprintfn "Error reading auth config: %s" e
                    1
                | Ok cfg ->
                    match switchActive profileName cfg with
                    | Error e ->
                        eprintfn "Error: %s" e
                        1
                    | Ok updated ->
                        match writeConfig updated with
                        | Error e ->
                            eprintfn "Error writing auth config: %s" e
                            1
                        | Ok () ->
                            printfn "Active profile: %s" profileName
                            0
        | Generate args ->
            let interactive  = args.Contains(GenerateArgs.Interactive)
            let skipCopilot  = args.Contains(GenerateArgs.Skip_Copilot)
            let explicitRepos = args.GetResults(GenerateArgs.Repo)

            withClient (fun deps ->
                // Apply config fallbacks for generate.
                let effectiveSkipCopilot =
                    skipCopilot || (deps.Config.SkipCopilot |> Option.defaultValue false)

                // --- Resolve name ---
                let nameResult =
                    match args.TryGetResult(GenerateArgs.Name), interactive with
                    | Some n, _    -> Ok n
                    | None, true   -> Ok (AnsiConsole.Ask<string>("Job [bold]name[/]:"))
                    | None, false  -> Error "--name is required (or use --interactive)."

                // --- Resolve org (CLI > config > interactive) ---
                let orgResult =
                    match nameResult with
                    | Error e -> Error e
                    | Ok _ ->
                        match args.TryGetResult(GenerateArgs.Org), deps.Config.DefaultOrg, interactive with
                        | Some o, _, _     -> Ok o
                        | None, Some o, _  -> Ok o
                        | None, None, true -> Ok (AnsiConsole.Ask<string>("GitHub [bold]org[/]:"))
                        | None, None, false -> Error "--org is required (or use --interactive)."

                match nameResult, orgResult with
                | Error e, _ | _, Error e ->
                    eprintfn "Error: %s" e
                    1
                | Ok name, Ok org ->
                    // --- Resolve repos ---
                    let reposResult : Result<string list, string> =
                        if not explicitRepos.IsEmpty then
                            // Strip any accidental "org/" prefix so we store short names only.
                            Ok (explicitRepos |> List.map (fun r ->
                                let slash = r.IndexOf('/')
                                if slash >= 0 then r.[slash + 1..] else r))
                        elif interactive then
                            // Fetch org repos and let the user multi-select.
                            match listOrgRepos deps org |> Async.RunSynchronously with
                            | Error e ->
                                eprintfn "Warning: could not fetch repos for org '%s': %s" org e
                                Ok []
                             | Ok allRepos ->
                                if (allRepos : string list).IsEmpty then
                                    Ok []
                                else
                                    let prompt =
                                        Spectre.Console.MultiSelectionPrompt<string>()
                                            .Title($"Select repos from [bold]{org}[/]:")
                                            .PageSize(20)
                                            .InstructionsText("[grey](Press [blue]<space>[/] to toggle, [green]<enter>[/] to confirm)[/]")
                                    for r in allRepos do prompt.AddChoice(r) |> ignore
                                    Ok (AnsiConsole.Prompt(prompt) |> Seq.toList)
                        else
                            Ok []

                    match reposResult with
                    | Error e ->
                        eprintfn "Error: %s" e
                        1
                    | Ok repos ->
                        // --- Resolve output path ---
                        let slug       = slugify name
                        let outputPath =
                            match args.TryGetResult(GenerateArgs.Output) with
                            | Some p -> p
                            | None   -> System.IO.Path.Combine(System.Environment.CurrentDirectory, $"{slug}.yml")

                        let input : GenerateInput =
                            { Name        = name
                              Org         = org
                              Repos       = repos
                              OutputPath  = outputPath
                              SkipCopilot = effectiveSkipCopilot }

                        match execute deps input with
                        | Error e ->
                            eprintfn "Error: %s" e
                            1
                        | Ok (yamlPath, mdPath) ->
                            printfn "Generated:"
                            printfn "  %s" yamlPath
                            printfn "  %s" mdPath
                            0)
        | Validate args ->
            let pattern         = args.GetResult(ValidateArgs.Yaml_File)
            let noParallel      = args.Contains(ValidateArgs.No_Parallel)
            let maxConcurrency  = args.TryGetResult(ValidateArgs.Max_Concurrency) |> Option.defaultValue 4
            let continueOnError = args.Contains(ValidateArgs.Continue_On_Error)
            let json            = args.Contains(ValidateArgs.Json)
            let cwd             = System.Environment.CurrentDirectory
            match OrcAI.Core.FileGlob.expand cwd pattern with
            | Error e ->
                eprintfn "Error: %s" e
                1
            | Ok paths ->
            withClient (fun deps ->
                // Apply config fallbacks for validate.
                let effectiveContinueOnError =
                    continueOnError || (deps.Config.ContinueOnError |> Option.defaultValue false)
                let effectiveMaxConcurrency =
                    match args.TryGetResult(ValidateArgs.Max_Concurrency) with
                    | Some v -> v
                    | None   -> deps.Config.MaxConcurrency |> Option.defaultValue 4
                let input : OrcAI.Core.ValidateCommand.ValidateInput =
                    { YamlPath        = ""  // overridden per-file in execute
                      NoParallel      = noParallel
                      MaxConcurrency  = effectiveMaxConcurrency
                      ContinueOnError = effectiveContinueOnError }
                let results =
                    OrcAI.Core.ValidateCommand.execute deps paths input
                    |> Async.RunSynchronously
                if json then
                    let doc =
                        results
                        |> List.map (fun (path, r) ->
                            path,
                            {| valid        = r.IsValid
                               configErrors = r.ConfigErrors
                               repoErrors   =
                                   r.RepoErrors
                                   |> List.map (fun (RepoName rn, e) -> {| repo = rn; error = e |}) |})
                        |> dict
                    printfn "%s" (JsonSerializer.Serialize(doc, jsonOptions))
                else
                    for (path, r) in results do
                        printfn "--- %s ---" path
                        for e in r.ConfigErrors do
                            AnsiConsole.MarkupLine($"  [red]{Markup.Escape(e)}[/]")
                        for (RepoName rn, e) in r.RepoErrors do
                            AnsiConsole.MarkupLine($"  [red]{Markup.Escape(rn)}: {Markup.Escape(e)}[/]")
                        if r.IsValid then
                            AnsiConsole.MarkupLine("  [green]Valid[/]")
                let allValid = results |> List.forall (fun (_, r) -> r.IsValid)
                if allValid then
                    AnsiConsole.MarkupLine("[green]Validation passed.[/]")
                    0
                else
                    1)
    with
    | :? ArguParseException as ex ->
        eprintfn "%s" ex.Message
        1
    | ex ->
        eprintfn "Error: %s" ex.Message
        1
