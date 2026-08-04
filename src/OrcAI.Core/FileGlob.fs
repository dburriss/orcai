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
open OrcAI.Core.Domain

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

// ---------------------------------------------------------------------------
// copy: staging input files into a command's working directory before it runs.
// ---------------------------------------------------------------------------

/// Return the leading path segments of a pattern before its first glob character,
/// e.g. "./scripts/*.sh" -> "./scripts". Used to compute each matched file's path
/// relative to the pattern when a copy entry matches multiple files.
let private staticPrefixDir (pattern: string) : string =
    let isGlobChar c = c = '*' || c = '?' || c = '[' || c = '{'
    match pattern |> Seq.tryFindIndex isGlobChar with
    | None -> Path.GetDirectoryName(pattern) |> Option.ofObj |> Option.defaultValue "."
    | Some idx ->
        let prefix  = pattern.[.. idx - 1]
        let lastSep = prefix.LastIndexOfAny([| '/'; '\\' |])
        if lastSep < 0 then "." else prefix.Substring(0, lastSep)

/// Copy the file(s) matched by `entry.From` (resolved against sourceRoot) into
/// `destRoot`. Single match → `entry.To` is the exact destination file path.
/// Multiple matches → `entry.To` is treated as a directory; each file is copied
/// preserving its path relative to the pattern's static prefix directory.
/// Returns the destination paths written, or propagates `expand`'s zero-match error.
let copyEntry (sourceRoot: string) (destRoot: string) (entry: CopyEntry) : Result<string list, string> =
    match expand sourceRoot entry.From with
    | Error e -> Error e
    | Ok [ single ] ->
        let dest = Path.Combine(destRoot, entry.To)
        Directory.CreateDirectory(Path.GetDirectoryName(dest) |> Option.ofObj |> Option.defaultValue destRoot) |> ignore
        File.Copy(single, dest, overwrite = true)
        Ok [ dest ]
    | Ok many ->
        let prefixDir = Path.GetFullPath(Path.Combine(sourceRoot, staticPrefixDir entry.From))
        let destDir   = Path.Combine(destRoot, entry.To)
        many
        |> List.map (fun src ->
            let relative = Path.GetRelativePath(prefixDir, src)
            let dest     = Path.Combine(destDir, relative)
            Directory.CreateDirectory(Path.GetDirectoryName(dest) |> Option.ofObj |> Option.defaultValue destDir) |> ignore
            File.Copy(src, dest, overwrite = true)
            dest)
        |> Ok

/// Copy all `entries` from sourceRoot into destRoot, short-circuiting on the first
/// pattern that matches zero files. Returns each written destination path paired
/// with its entry's Keep flag, for later use by cleanupCopies.
let copyAll (sourceRoot: string) (destRoot: string) (entries: CopyEntry list) : Result<(string * bool) list, string> =
    let rec loop acc entries =
        match entries with
        | [] -> Ok(List.rev acc)
        | entry :: rest ->
            match copyEntry sourceRoot destRoot entry with
            | Error e    -> Error e
            | Ok paths ->
                let acc' = (paths |> List.map (fun p -> p, entry.Keep) |> List.rev) @ acc
                loop acc' rest
    loop [] entries

/// Delete every copied path whose Keep flag is false. Best-effort — swallows I/O
/// errors since the destination (e.g. a worktree) may already be gone.
let cleanupCopies (written: (string * bool) list) : unit =
    for (path, keep) in written do
        if not keep then
            try File.Delete(path) with _ -> ()
