module OrcAI.Core.FileGlob

// ---------------------------------------------------------------------------
// Glob-pattern expansion over the file system.
//
// Primary entry point:
//   expand searchDir pattern
//     — resolves a glob or plain path against searchDir.
//     — uses Microsoft.Extensions.FileSystemGlobbing for full ** support.
//
// Testable overload:
//   expandWith dir pattern
//     — accepts a DirectoryInfoBase so tests can inject a fake directory tree.
// ---------------------------------------------------------------------------

open System.IO
open Microsoft.Extensions.FileSystemGlobbing
open Microsoft.Extensions.FileSystemGlobbing.Abstractions

/// Glob characters that indicate a pattern (not a plain path).
let private isGlobPattern (s: string) =
    s.Contains('*') || s.Contains('?') || s.Contains('[')

/// Expand a glob pattern against the given DirectoryInfoBase.
/// Returns Ok of a sorted list of full paths, or Error if nothing matched.
let expandWith (dir: DirectoryInfoBase) (pattern: string) : Result<string list, string> =
    let matcher = Matcher()
    matcher.AddInclude(pattern) |> ignore
    let results = matcher.Execute(dir)
    let files =
        results.Files
        |> Seq.map (fun f -> Path.GetFullPath(Path.Combine(dir.FullName, f.Path)))
        |> Seq.sort
        |> Seq.toList
    if files.IsEmpty then
        Error $"No files matched pattern: {pattern}"
    else
        Ok files

/// Expand a glob pattern or plain file path against searchDir (an absolute directory path).
///
/// - If pattern contains no glob characters, check the file exists directly.
/// - Otherwise use FileSystemGlobbing to expand the pattern within searchDir.
///
/// Returns Ok of a non-empty sorted path list, or Error with a descriptive message.
let expand (searchDir: string) (pattern: string) : Result<string list, string> =
    if isGlobPattern pattern then
        let dir = DirectoryInfoWrapper(DirectoryInfo(searchDir))
        expandWith dir pattern
    else
        // Plain path: resolve relative to searchDir if not already absolute.
        let fullPath =
            if Path.IsPathRooted(pattern) then pattern
            else Path.GetFullPath(Path.Combine(searchDir, pattern))
        if File.Exists(fullPath) then
            Ok [ fullPath ]
        else
            Error $"File not found: {fullPath}"
