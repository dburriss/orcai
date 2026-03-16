#!/usr/bin/env -S dotnet fsi

// =============================================================================
// Validates `orca info` against the fixture YAML.
//
// Assertions:
//   1. Exit code is 0
//   2. Stdout contains the project title ("Orca Validation Test")
//   3. Stdout contains the repo name ("orca-tests")
//
// Prerequisites:
//   - orcai run must have been executed first (lock file or live GitHub state)
//   - orcai binary on PATH (or ORCAI_BIN env var set)
//   - Valid GitHub credentials (orcai auth / GH_TOKEN / gh auth)
// =============================================================================

#load "config.fsx"
#load "helpers.fsx"

open Config
open Helpers

section "orca info"

let result = runCmd orcaBin (sprintf "info --json \"%s\"" fixtureYaml)
printResult result

assertExitCode       0                     "exit code is 0"                   result
assertStdoutContains "Orca Validation Test" "stdout: project title present"   result
assertStdoutContains validateRepo           "stdout: repo name present"        result

printfn ""
printfn "info.fsx: all assertions passed."
