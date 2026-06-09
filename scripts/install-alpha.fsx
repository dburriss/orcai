#!/usr/bin/env -S dotnet fsi

// =================================================================================
// Install-Alpha Script for OrcAI CLI
// =================================================================================
// Packs the current source as OrcAI.Tool with a patch-bumped alpha version and
// installs it as a global dotnet tool — useful for dog-fooding in-progress work.
//
// Version logic: reads <Version> from OrcAI.Tool.fsproj, strips any existing suffix,
// increments the patch segment, and appends "-alpha"  (e.g. 0.7.5 -> 0.7.6-alpha).
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
let nextPatch = Version(currentVersion.Major, currentVersion.Minor, currentVersion.Build + 1)
let alphaVersion = sprintf "%O-alpha" nextPatch

printfn "fsproj version : %s" versionString
printfn "alpha version  : %s" alphaVersion
printfn ""

let nupkgDir = Path.Combine(rootPath, "src/OrcAI.Tool/nupkg")

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
