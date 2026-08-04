module OrcAI.Core.MigrateCommand

// ---------------------------------------------------------------------------
// Implements `orcai migrate` — upgrades a job YAML and its sibling
// <basename>.lock.json in place, across one or more schema versions.
//
// Purely local (file-system only): the lock file step never calls GitHub —
// that's the whole point of migrating it rather than deleting it, which
// would force every repo in the job back onto the live, rate-limited lookup
// path just to recover from a field rename.
//
// Each format (YAML, lock) has its own ordered list of steps. A step is a
// plain `string -> Result<string option * string list, string>` function —
// None means "didn't apply / already at this version", Some means "migrated,
// here's the new text". The orchestrator below just threads text through the
// list in order; adding a v2->v3 step later is purely additive (a new step
// module + one more list entry), with no change to this file's structure.
// ---------------------------------------------------------------------------

open System.IO
open System.IO.Abstractions
open OrcAI.Core.Migrations

type MigrationStep =
    { FromVersion : int
      ToVersion   : int
      Description : string
      Apply       : string -> Result<string option * string list, string> }

/// Ordered oldest-first. Add future hops (v2->v3, ...) by appending here.
let private yamlSteps : MigrationStep list =
    [ { FromVersion = 1
        ToVersion   = 2
        Description = "assign:/job.skipCopilot -> action:; onClosedIssue default preserved"
        Apply       = YamlStep1To2.apply } ]

/// Ordered oldest-first. Add future hops (v2->v3, ...) by appending here.
let private lockSteps : MigrationStep list =
    [ { FromVersion = 1
        ToVersion   = 2
        Description = "int issue/project ids -> opaque string ids; PR state added"
        Apply       = LockStep1To2.apply } ]

type MigrateInput =
    { YamlPath : string
      DryRun   : bool }

type MigrateFileReport =
    { Path        : string
      Changed     : bool
      FromVersion : int option
      ToVersion   : int option
      Warnings    : string list
      /// Set only when a change was actually written (never set for --dryrun).
      BackupPath  : string option }

type MigrateResult =
    { Yaml : MigrateFileReport
      /// None when no <basename>.lock.json exists yet — not an error.
      Lock : MigrateFileReport option }

/// Fold `steps` over `text`. A step returning None leaves the text and the
/// version range untouched; Some newText advances both. Any step erroring
/// (malformed input, or a conflict/unknown value it can't resolve) halts the
/// whole chain — no partial migration is ever produced.
let private runSteps (steps: MigrationStep list) (text: string) : Result<string * (int * int) list * string list, string> =
    steps
    |> List.fold (fun acc step ->
        match acc with
        | Error e -> Error e
        | Ok (currentText, ranges, warnings) ->
            match step.Apply currentText with
            | Error e              -> Error e
            | Ok (None, w)         -> Ok (currentText, ranges, warnings @ w)
            | Ok (Some newText, w) -> Ok (newText, ranges @ [ step.FromVersion, step.ToVersion ], warnings @ w))
        (Ok (text, [], []))

let private migrateFile (fs: IFileSystem) (dryRun: bool) (steps: MigrationStep list) (path: string) : Result<MigrateFileReport, string> =
    let original = fs.File.ReadAllText(path)
    match runSteps steps original with
    | Error e -> Error e
    | Ok (finalText, ranges, warnings) ->
        let changed = not ranges.IsEmpty
        let backupPath = path + ".bak"
        if changed && not dryRun then
            fs.File.WriteAllText(backupPath, original)
            fs.File.WriteAllText(path, finalText)
        Ok { Path        = path
             Changed     = changed
             FromVersion = ranges |> List.tryHead |> Option.map fst
             ToVersion   = ranges |> List.tryLast |> Option.map snd
             Warnings    = warnings
             BackupPath  = if changed && not dryRun then Some backupPath else None }

/// Migrate `input.YamlPath` and, if present, its sibling lock file.
/// A missing lock file is not an error — plenty of jobs are migrated before
/// their first run. A hard error in either step aborts before anything is
/// written for that file (never a half-migrated file on disk).
let execute (fs: IFileSystem) (input: MigrateInput) : Result<MigrateResult, string> =
    let yamlPath = Path.GetFullPath(input.YamlPath)
    if not (fs.File.Exists(yamlPath)) then
        Error $"YAML config file not found: {yamlPath}"
    else
        match migrateFile fs input.DryRun yamlSteps yamlPath with
        | Error e -> Error e
        | Ok yamlReport ->
            let lockPath = LockFile.lockFilePath yamlPath
            if not (fs.File.Exists(lockPath)) then
                Ok { Yaml = yamlReport; Lock = None }
            else
                match migrateFile fs input.DryRun lockSteps lockPath with
                | Error e       -> Error e
                | Ok lockReport -> Ok { Yaml = yamlReport; Lock = Some lockReport }
