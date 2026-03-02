module Orca.Core.YamlConfig

open Orca.Core.Domain

// ---------------------------------------------------------------------------
// Parses the YAML job configuration file into a JobConfig record.
// Uses YamlDotNet for deserialisation.
// ---------------------------------------------------------------------------

/// Parse a YAML job configuration from a file path.
/// Returns an error string if the file is missing or malformed.
let parseFile (path: string) : Result<JobConfig, string> =
    failwith "not implemented"

/// Compute the SHA-256 hash of the raw YAML file content.
/// Used to populate the yamlHash field in the lock file.
let computeHash (path: string) : string =
    failwith "not implemented"
