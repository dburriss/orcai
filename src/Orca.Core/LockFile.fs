module Orca.Core.LockFile

open System.IO
open Orca.Core.Domain

// ---------------------------------------------------------------------------
// Reads and writes the JSON lock file that sits alongside the YAML config.
// Lock file path convention: <yaml-basename>.lock.json
// ---------------------------------------------------------------------------

/// Derive the lock file path from the YAML config file path.
let lockFilePath (yamlPath: string) : string =
    let dir  = Path.GetDirectoryName(yamlPath)
    let stem = Path.GetFileNameWithoutExtension(yamlPath)
    Path.Combine(dir, $"{stem}.lock.json")

/// Read and deserialise a lock file.
/// Returns None if the file does not exist.
let tryRead (yamlPath: string) : LockFile option =
    failwith "not implemented"

/// Serialise and write a lock file to disk.
let write (yamlPath: string) (lock: LockFile) : unit =
    failwith "not implemented"
