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
    s.Contains('*') || s.Contains('?') || s.Contains('[') || s.Contains('{')

/// Expand the first {a,b,...} brace group in a pattern into multiple patterns.
let rec private expandBraces (pattern: string) : string list =
    let openIdx = pattern.IndexOf('{')
    if openIdx < 0 then
        [ pattern ]
    else
        let closeIdx = pattern.IndexOf('}', openIdx)
        if closeIdx < 0 then
            [ pattern ]
        else
            let prefix       = pattern.[..openIdx - 1]
            let suffix       = pattern.[closeIdx + 1..]
            let alternatives = pattern.[openIdx + 1..closeIdx - 1].Split(',') |> Array.toList
            alternatives |> List.collect (fun alt -> expandBraces (prefix + alt + suffix))

/// Expand a glob pattern against the given DirectoryInfoBase.
/// Returns Ok of a sorted list of full paths, or Error if nothing matched.
let expandWith (dir: DirectoryInfoBase) (pattern: string) : Result<string list, string> =
    let files =
        expandBraces pattern
        |> List.collect (fun p ->
            let matcher = Matcher()
            matcher.AddInclude(p) |> ignore
            matcher.Execute(dir).Files
            |> Seq.map (fun f -> Path.GetFullPath(Path.Combine(dir.FullName, f.Path)))
            |> Seq.toList)
        |> List.distinct
        |> List.sort
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
