#!/usr/bin/env -S dotnet fsi

// =============================================================================
// Validates `orca run` against the fixture YAML.
//
// Assertions:
//   1. Exit code is 0
//   2. Stdout contains "Run complete"
//   3. Lock file is created alongside fixture.yml
//
// Prerequisites:
//   - orca binary on PATH (or ORCA_BIN env var set)
//   - Valid GitHub credentials (orca auth / GH_TOKEN / gh auth)
// =============================================================================

#load "config.fsx"
#load "helpers.fsx"

open Config
open Helpers

section "orca run"

let result = runCmd orcaBin (sprintf "run \"%s\"" fixtureYaml)
printResult result

assertExitCode       0              "exit code is 0"          result
assertStdoutContains "Run complete" "stdout: Run complete"    result
assertFileExists     fixtureLock    "lock file is created"

printfn ""
printfn "run.fsx: all assertions passed."
