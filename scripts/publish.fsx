#!/usr/bin/env -S dotnet fsi

// =================================================================================
// Publish Script for OrcAI CLI
// =================================================================================
// This script automates the release process for the OrcAI CLI binary.
//
// Normal release flow (when Unreleased changes exist):
// 1. Reads the current version from src/OrcAI.Tool/OrcAI.Tool.fsproj
// 2. Extracts the "Unreleased" section from CHANGELOG.md
// 3. Runs a preflight release build and test pass
// 4. Prompts the user to select the next version (Major, Minor, or Patch)
// 5. Updates CHANGELOG.md:
//    - Moves "Unreleased" changes to a new versioned section
//    - Creates a new empty "Unreleased" section
// 6. Updates OrcAI.Tool.fsproj via XML APIs:
//    - Increments the <Version> tag
//    - Updates <PackageReleaseNotes> with escaped text content
// 7. Runs a final release build before any git operations
// 8. Stages, commits, and tags the release in Git
// 9. Optionally pushes the changes and tag to the remote repository
//
// Retag flow (when there are no Unreleased changes):
// 1. Shows the latest commit hash and message
// 2. Asks whether to retag that commit with the current version
// 3. Prompts the user to type the current version to confirm
// 4. Runs a preflight release build and test pass
// 5. Warns that the local tag will be deleted and recreated, and the remote
//    tag will be force-pushed if the user chooses to push
// 6. Deletes the existing local tag (if present) and recreates it at HEAD
// 7. Optionally force-pushes the tag to the remote
//
// Arguments:
//   --dry-run        Simulate the process without making any changes to disk or git.
//   --allow-dirty    Allow running with a dirty working tree.
//   --require-clean  Fail if the working tree is dirty.
//
// Usage:
//   ./publish.sh
//   ./publish.sh --dry-run
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

let promptYesNo prompt =
    printf "%s" prompt
    match Console.ReadLine() with
    | null -> false
    | response -> response.Trim().ToLowerInvariant() = "y"

let requireElement (parent: XElement) (name: XName) =
    match parent.Element(name) with
    | null -> failwithf "Missing <%s> element in %s" name.LocalName parent.Name.LocalName
    | element -> element

let args = fsi.CommandLineArgs |> Array.skip 1
let isDryRun = args |> Array.contains "--dry-run"
let allowDirty =
    if args |> Array.contains "--require-clean" then false
    else true

if isDryRun then
    printfn "=== DRY RUN MODE ==="
    printfn "No changes will be written to disk."
    printfn "===================="

let rootDir = __SOURCE_DIRECTORY__ |> Directory.GetParent
let rootPath = rootDir.FullName
let fsprojPath = Path.Combine(rootPath, "src/OrcAI.Tool/OrcAI.Tool.fsproj")
let changelogPath = Path.Combine(rootPath, "CHANGELOG.md")
let solutionPath = Path.Combine(rootPath, "OrcAI.sln")

printfn "Checking files..."
if not (File.Exists fsprojPath) then failwithf "Project file not found: %s" fsprojPath
if not (File.Exists changelogPath) then failwithf "Changelog not found: %s" changelogPath
if not (File.Exists solutionPath) then failwithf "Solution file not found: %s" solutionPath

let gitStatus = runProcess "git" ["status"; "--porcelain"] rootPath
if gitStatus.Length > 0 && not allowDirty then
    failwith "Working tree is dirty. Commit or stash changes first, or rerun with --allow-dirty."

let projectDoc = XDocument.Load(fsprojPath)
let projectRoot =
    match projectDoc.Root with
    | null -> failwithf "Project XML is empty: %s" fsprojPath
    | root -> root
let ns = projectRoot.Name.Namespace
let propertyGroup =
    projectRoot.Elements(ns + "PropertyGroup")
    |> Seq.tryHead
    |> Option.defaultWith (fun () -> failwithf "No <PropertyGroup> found in %s" fsprojPath)
let versionElement = requireElement propertyGroup (ns + "Version")
let releaseNotesElement = requireElement propertyGroup (ns + "PackageReleaseNotes")

let currentVersion = Version.Parse(versionElement.Value)
printfn "Current Version: %O" currentVersion

let changelogLines = File.ReadAllLines(changelogPath) |> Array.toList

let unreleasedHeaderIdx =
    changelogLines
    |> List.tryFindIndex (fun l -> l.StartsWith("## [Unreleased]"))

if unreleasedHeaderIdx.IsNone then failwith "Could not find '## [Unreleased]' in CHANGELOG.md"

let unreleasedIdx = unreleasedHeaderIdx.Value

let nextSectionIdx =
    changelogLines
    |> List.skip (unreleasedIdx + 1)
    |> List.tryFindIndex (fun l -> l.StartsWith("## [") && not (l.Contains("Unreleased")))
    |> Option.map (fun i -> i + unreleasedIdx + 1)
    |> Option.defaultValue changelogLines.Length

let unreleasedContent =
    changelogLines
    |> List.skip (unreleasedIdx + 1)
    |> List.take (nextSectionIdx - unreleasedIdx - 1)
    |> List.filter (fun l -> not (String.IsNullOrWhiteSpace(l)))

printfn "\nUnreleased Changes:"
if unreleasedContent.IsEmpty then
    printfn "  (None)"
else
    unreleasedContent |> List.iter (fun l -> printfn "  %s" l)

printfn ""

let runReleaseBuild () =
    printfn "Running release build..."
    runProcess "dotnet" ["build"; solutionPath; "-c"; "Release"; "--nologo"] rootPath |> ignore

let runReleaseTests () =
    printfn "Running release tests..."
    runProcess "dotnet" ["test"; solutionPath; "-c"; "Release"; "--no-build"; "--nologo"] rootPath |> ignore

// ---------------------------------------------------------------------------
// Retag path — no unreleased changes
// ---------------------------------------------------------------------------

if unreleasedContent.IsEmpty then
    let headInfo = runProcess "git" ["log"; "-1"; "--pretty=format:%h %s"] rootPath
    printfn "No unreleased changes found."
    printfn "Latest commit: %s" headInfo
    printfn ""

    if not (promptYesNo (sprintf "Do you want to retag this commit as v%O? (y/n): " currentVersion)) then
        printfn "Nothing to do."
        exit 0

    printf "Type the current version to confirm (%O): " currentVersion
    let typed = Console.ReadLine()
    if typed <> string currentVersion then
        eprintfn "Version mismatch — expected '%O', got '%s'. Aborting." currentVersion typed
        exit 1

    if isDryRun then
        printfn "[Dry Run] Would run: dotnet build \"%s\" -c Release --nologo" solutionPath
        printfn "[Dry Run] Would run: dotnet test \"%s\" -c Release --no-build --nologo" solutionPath
    else
        runReleaseBuild ()
        runReleaseTests ()

    let tagName = sprintf "v%O" currentVersion
    printfn ""
    printfn "Warning: this will delete and recreate tag %s locally." tagName
    printfn "If you choose to push, it will force-push the tag to the remote,"
    printfn "overwriting any existing remote tag."
    printfn ""

    if isDryRun then
        printfn "[Dry Run] Git operations skipped. Would execute:"
        printfn "1. git tag -d %s  (if tag exists)" tagName
        printfn "2. git tag %s" tagName
        printfn "3. git push origin %s --force  (if push confirmed)" tagName
        exit 0

    let existingTag = runProcess "git" ["tag"; "-l"; tagName] rootPath
    if existingTag.Length > 0 then
        printfn "Deleting existing local tag %s..." tagName
        runProcess "git" ["tag"; "-d"; tagName] rootPath |> ignore

    printfn "Creating tag %s at HEAD..." tagName
    runProcess "git" ["tag"; tagName] rootPath |> ignore
    printfn "Tag %s created." tagName

    if promptYesNo (sprintf "Push tag %s to remote (force)? (y/n): " tagName) then
        printfn "Force-pushing tag %s..." tagName
        try
            runProcess "git" ["push"; "origin"; tagName; "--force"] rootPath |> printfn "%s"
            printfn "Done!"
        with ex ->
            printfn "Error pushing tag: %s" ex.Message
    else
        printfn "Push skipped. Run: git push origin %s --force" tagName

    exit 0

// ---------------------------------------------------------------------------
// Normal release path — unreleased changes exist
// ---------------------------------------------------------------------------

if isDryRun then
    printfn "[Dry Run] Would run: dotnet build \"%s\" -c Release --nologo" solutionPath
    printfn "[Dry Run] Would run: dotnet test \"%s\" -c Release --no-build --nologo" solutionPath
else
    runReleaseBuild ()
    runReleaseTests ()

printfn "Select increment type:"
let nextMajor = Version(currentVersion.Major + 1, 0, 0)
let nextMinor = Version(currentVersion.Major, currentVersion.Minor + 1, 0)
let nextPatch = Version(currentVersion.Major, currentVersion.Minor, currentVersion.Build + 1)

printfn "1) Major (%O -> %O)" currentVersion nextMajor
printfn "2) Minor (%O -> %O)" currentVersion nextMinor
printfn "3) Patch (%O -> %O)" currentVersion nextPatch

printf "Choice (1-3): "
let choice = Console.ReadLine()
let newVersion =
    match choice with
    | "1" -> nextMajor
    | "2" -> nextMinor
    | "3" -> nextPatch
    | _ -> failwith "Invalid selection"

printfn "New Version: %O" newVersion

let releaseNotes =
    unreleasedContent
    |> List.filter (fun l -> l.Trim().StartsWith("-") || l.Trim().StartsWith("*"))
    |> List.map (fun l -> l.Trim().TrimStart('-', '*').Trim())
    |> String.concat "\n"

printfn "Updating fsproj..."
if isDryRun then
    printfn "\n[Dry Run] Would update fsproj:"
    printfn "  Version: %O" newVersion
    printfn "  ReleaseNotes: %s" releaseNotes
else
    versionElement.Value <- string newVersion
    releaseNotesElement.Value <- releaseNotes
    projectDoc.Save(fsprojPath)

printfn "Updating CHANGELOG.md..."
let today = DateTime.Now.ToString("yyyy-MM-dd")

let preUnreleasedLines = changelogLines |> List.take unreleasedIdx
let postUnreleasedLines = changelogLines |> List.skip (unreleasedIdx + 1)

let newChangelogSection =
    [
        "## [Unreleased]"
        ""
        sprintf "## [%O] - %s" newVersion today
    ]

let newChangelogLines =
    preUnreleasedLines @
    newChangelogSection @
    postUnreleasedLines

if isDryRun then
    printfn "\n[Dry Run] Would update CHANGELOG.md with new section:"
    newChangelogSection |> List.iter (fun l -> printfn "  %s" l)
else
    File.WriteAllLines(changelogPath, newChangelogLines)

if isDryRun then
    printfn "[Dry Run] Would run: dotnet build \"%s\" -c Release --nologo" solutionPath
else
    printfn "Running final release build..."
    runReleaseBuild ()

if isDryRun then
    printfn "\n[Dry Run] Git operations skipped. Would execute:"
    printfn "1. git add \"%s\" \"%s\"" fsprojPath changelogPath
    printfn "2. git commit -m \"release: prepare v%O\"" newVersion
    printfn "3. git tag v%O" newVersion
    printfn "4. git push origin main"
    printfn "5. git push origin v%O" newVersion
    exit 0

printfn "\nFiles updated. Ready to commit."
printfn "Commands to be executed:"
printfn "1. git add \"%s\" \"%s\"" fsprojPath changelogPath
printfn "2. git commit -m \"release: prepare v%O\"" newVersion
printfn "3. git tag v%O" newVersion
printfn "4. git push origin main"
printfn "5. git push origin v%O" newVersion

if promptYesNo "Proceed with git operations? (y/n): " then
    printfn "Executing git add..."
    runProcess "git" ["add"; fsprojPath; changelogPath] rootPath |> ignore

    printfn "Executing git commit..."
    runProcess "git" ["commit"; "-m"; sprintf "release: prepare v%O" newVersion] rootPath |> ignore

    printfn "Executing git tag..."
    runProcess "git" ["tag"; sprintf "v%O" newVersion] rootPath |> ignore

    if promptYesNo "Push to remote? (y/n): " then
        printfn "Pushing main..."
        try
            runProcess "git" ["push"; "origin"; "main"] rootPath |> printfn "%s"
            printfn "Pushing tag..."
            runProcess "git" ["push"; "origin"; sprintf "v%O" newVersion] rootPath |> printfn "%s"
            printfn "Done!"
        with ex ->
            printfn "Error pushing: %s" ex.Message
    else
        printfn "Push skipped. Don't forget to push manually!"
else
    printfn "Git operations skipped."
