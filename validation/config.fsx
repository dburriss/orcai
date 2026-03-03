// =============================================================================
// Validation configuration — edit this file before running the scripts.
// =============================================================================

open System

/// Path to the orca binary. Defaults to "orca" (must be on PATH).
/// Override by setting the ORCA_BIN environment variable.
let orcaBin =
    match Environment.GetEnvironmentVariable("ORCA_BIN") with
    | null | "" -> "orca"
    | v         -> v

/// GitHub org that owns the validation repo.
let validateOrg = "dburriss"

/// Repository used for validation (issues will be created and deleted here).
let validateRepo = "orca-tests"

/// Absolute path to the fixture YAML used by all validation scripts.
let fixtureYaml = __SOURCE_DIRECTORY__ + "/fixture.yml"

/// Derived: path to the lock file orca writes alongside the YAML.
let fixtureLock = System.IO.Path.ChangeExtension(fixtureYaml, ".lock.json")
