#!/usr/bin/env -S dotnet fsi

// =============================================================================
// End-to-end validation runner for the orca CLI.
//
// Runs in sequence:
//   1. run.fsx     — creates project + issue, writes lock file
//   2. info.fsx    — reads state from lock file and prints it
//   3. cleanup.fsx — deletes issue, project, and removes lock file
//
// Stops on first failure. Deletes the lock file on success so reruns are clean.
//
// Usage:
//   dotnet fsi validation/validate.fsx
//
// Prerequisites:
//   - orca binary on PATH (or ORCA_BIN env var set)
//   - Valid GitHub credentials (orca auth / GH_TOKEN / gh auth)
// =============================================================================

#load "config.fsx"
#load "helpers.fsx"

open System
open System.Diagnostics
open Config
open Helpers

// ---------------------------------------------------------------------------
// Run a child fsx script via dotnet fsi and stream its output.
// Returns the exit code.
// ---------------------------------------------------------------------------
let runScript (scriptPath: string) (extraArgs: string) : int =
    let args = sprintf "\"%s\" %s" scriptPath extraArgs
    let psi  = ProcessStartInfo("dotnet", Arguments = sprintf "fsi %s" args)
    psi.UseShellExecute        <- false
    psi.RedirectStandardOutput <- false   // let child write directly to console
    psi.RedirectStandardError  <- false
    let p = Process.Start(psi)
    p.WaitForExit()
    p.ExitCode

let scriptDir = __SOURCE_DIRECTORY__

// ---------------------------------------------------------------------------
// Run each script in sequence, stopping on first failure
// ---------------------------------------------------------------------------

printfn "============================================="
printfn "  orca validation suite"
printfn "  binary : %s" orcaBin
printfn "  org    : %s" validateOrg
printfn "  repo   : %s" validateRepo
printfn "  yaml   : %s" fixtureYaml
printfn "============================================="

let steps =
    [ "run",     scriptDir + "/run.fsx",     ""
      "info",    scriptDir + "/info.fsx",    ""
      "cleanup", scriptDir + "/cleanup.fsx", "" ]

let mutable failed = false

for (name, script, args) in steps do
    if not failed then
        printfn ""
        printfn ">>> %s" name
        let code = runScript script args
        if code <> 0 then
            printfn ""
            printfn "[FAIL] %s exited with code %d — stopping." name code
            failed <- true

// ---------------------------------------------------------------------------
// Tidy up the lock file so reruns start fresh
// ---------------------------------------------------------------------------
if not failed && IO.File.Exists(fixtureLock) then
    IO.File.Delete(fixtureLock)
    printfn ""
    printfn "Lock file removed: %s" fixtureLock

// ---------------------------------------------------------------------------
// Summary
// ---------------------------------------------------------------------------
printfn ""
printfn "============================================="
if failed then
    printfn "  RESULT: FAILED"
    printfn "============================================="
    Environment.Exit(1)
else
    printfn "  RESULT: ALL PASSED"
    printfn "============================================="
