#!/usr/bin/env -S dotnet fsi

// =================================================================================
// Install-Alpha Script for OrcAI CLI
// =================================================================================
// Packs the current source as OrcAI.Tool with a patch-bumped alpha version and
// installs it as a global dotnet tool — useful for dog-fooding in-progress work.
//
// Version logic: reads <Version> from OrcAI.Tool.fsproj, strips any existing suffix,
// increments the patch segment, then scans the nupkg dir to find the next alpha counter
// (e.g. first run: 0.7.6-alpha.1, second run: 0.7.6-alpha.2, ...).
//
// Arguments:
//   --dry-run    Print what would happen without building or installing.
//
// Usage:
//   ./install-alpha.sh
//   ./install-alpha.sh --dry-run
// =================================================================================

open System
open System.IO
open System.Diagnostics
open System.Xml.Linq

let runProcess executable (arguments: string list) workingDirectory =
    let psi = ProcessStartInfo(executable)
    for arg in arguments do psi.ArgumentList.Add(arg)
    psi.RedirectStandardOutput <- true
    psi.RedirectStandardError <- true
    psi.UseShellExecute <- false
    psi.WorkingDirectory <- workingDirectory

    use p = Process.Start(psi)
    let output = p.StandardOutput.ReadToEnd()
    let error = p.StandardError.ReadToEnd()
    p.WaitForExit()

    if p.ExitCode <> 0 then
        failwithf "%s %s failed with exit code %d\nstdout:\n%s\nstderr:\n%s"
            executable (arguments |> String.concat " ") p.ExitCode output error

    output.Trim()

let tryRunProcess executable (arguments: string list) workingDirectory =
    try runProcess executable arguments workingDirectory |> ignore; true
    with ex ->
        printfn "  (ignored: %s)" ex.Message
        false

let args = fsi.CommandLineArgs |> Array.skip 1
let isDryRun = args |> Array.contains "--dry-run"

if isDryRun then
    printfn "=== DRY RUN MODE ==="
    printfn "No changes will be made."
    printfn "===================="

let rootDir = __SOURCE_DIRECTORY__ |> Directory.GetParent
let rootPath = rootDir.FullName
let fsprojPath = Path.Combine(rootPath, "src/OrcAI.Tool/OrcAI.Tool.fsproj")

let projectDoc = XDocument.Load(fsprojPath)
let projectRoot =
    match projectDoc.Root with
    | null -> failwith "Project XML is empty"
    | r -> r
let ns = projectRoot.Name.Namespace
let propertyGroup =
    projectRoot.Elements(ns + "PropertyGroup")
    |> Seq.tryHead
    |> Option.defaultWith (fun () -> failwith "No <PropertyGroup> found in fsproj")
let versionElement =
    match propertyGroup.Element(ns + "Version") with
    | null -> failwith "No <Version> element found in fsproj"
    | el -> el

let versionString = versionElement.Value
let numericPart =
    match versionString.IndexOf('-') with
    | -1 -> versionString
    | idx -> versionString.[..idx-1]

let currentVersion = Version.Parse(numericPart)
let baseVersion = Version(currentVersion.Major, currentVersion.Minor, currentVersion.Build + 1)

let nupkgDir = Path.Combine(rootPath, "src/OrcAI.Tool/nupkg")
// nupkg filename format: orcai.tool.0.8.2-alpha.1.nupkg
let alphaInfix = sprintf "%O-alpha." baseVersion
let existingAlphaNumbers =
    if Directory.Exists(nupkgDir) then
        Directory.GetFiles(nupkgDir, "*.nupkg")
        |> Array.choose (fun f ->
            let name = Path.GetFileNameWithoutExtension(f).ToLower()
            let needle = alphaInfix.ToLower()
            match name.IndexOf(needle) with
            | -1 -> None
            | idx ->
                let rest = name.[idx + needle.Length ..]
                match Int32.TryParse(rest) with
                | true, n -> Some n
                | _ -> None)
    else [||]

let nextAlpha =
    if existingAlphaNumbers.Length = 0 then 1
    else Array.max existingAlphaNumbers + 1

let alphaVersion = sprintf "%O-alpha.%d" baseVersion nextAlpha

printfn "fsproj version : %s" versionString
printfn "alpha version  : %s" alphaVersion
printfn ""

// Pack
if isDryRun then
    printfn "[Dry Run] Would pack: dotnet pack src/OrcAI.Tool/OrcAI.Tool.fsproj -c Release -p:Version=%s -o %s" alphaVersion nupkgDir
else
    printfn "Packing OrcAI.Tool %s..." alphaVersion
    runProcess "dotnet"
        [ "pack"; "src/OrcAI.Tool/OrcAI.Tool.fsproj"
          "-c"; "Release"
          (sprintf "-p:Version=%s" alphaVersion)
          "-o"; nupkgDir
          "--nologo" ]
        rootPath
    |> ignore
    printfn "Pack complete."

printfn ""

// Uninstall existing (best-effort — not installed is fine)
if isDryRun then
    printfn "[Dry Run] Would uninstall: dotnet tool uninstall --global orcai.tool"
else
    printfn "Uninstalling existing orcai.tool (if present)..."
    tryRunProcess "dotnet" ["tool"; "uninstall"; "--global"; "orcai.tool"] rootPath |> ignore

// Install from local source
if isDryRun then
    printfn "[Dry Run] Would install: dotnet tool install --global --add-source %s orcai.tool --version %s" nupkgDir alphaVersion
else
    printfn "Installing orcai.tool %s..." alphaVersion
    runProcess "dotnet"
        [ "tool"; "install"; "--global"
          "--add-source"; nupkgDir
          "orcai.tool"
          "--version"; alphaVersion ]
        rootPath
    |> ignore
    printfn ""
    printfn "Done! Installed orcai.tool %s" alphaVersion
