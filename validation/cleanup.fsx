#!/usr/bin/env -S dotnet fsi

// =============================================================================
// Validates `orca cleanup` against the fixture YAML.
//
// Pass --dry-run to invoke `orca cleanup --dryrun` instead of a live cleanup.
//
// Assertions (live):
//   1. Exit code is 0
//   2. Stdout contains "Cleanup complete"
//
// Assertions (--dry-run):
//   1. Exit code is 0
//   2. Stdout contains "Dry run complete"
//
// Prerequisites:
//   - orcai run must have been executed first
//   - orcai binary on PATH (or ORCAI_BIN env var set)
//   - Valid GitHub credentials (orcai auth / GH_TOKEN / gh auth)
// =============================================================================

#load "config.fsx"
#load "helpers.fsx"

open Config
open Helpers

let scriptArgs = fsi.CommandLineArgs |> Array.skip 1
let isDryRun   = scriptArgs |> Array.contains "--dry-run"

section "orca cleanup"

let args =
    if isDryRun then sprintf "cleanup --dryrun \"%s\"" fixtureYaml
    else             sprintf "cleanup --force \"%s\""  fixtureYaml

let result = runCmd orcaBin args
printResult result

assertExitCode 0 "exit code is 0" result

if isDryRun then
    assertStdoutContains "Dry run complete" "stdout: Dry run complete" result
else
    assertStdoutContains "Cleanup complete" "stdout: Cleanup complete" result

printfn ""
printfn "cleanup.fsx: all assertions passed."
