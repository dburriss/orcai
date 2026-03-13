module OrcAI.Core.Tests.FileGlobTests

open System.IO
open Xunit
open OrcAI.Core.FileGlob
open Microsoft.Extensions.FileSystemGlobbing.Abstractions

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

/// Create a temporary directory, populate it with files, run a test, then clean up.
let private withTempDir (files: string list) (f: string -> unit) =
    let dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName())
    Directory.CreateDirectory(dir) |> ignore
    try
        for rel in files do
            let full = Path.Combine(dir, rel)
            let parentDir = Path.GetDirectoryName(full) |> Option.ofObj |> Option.defaultValue dir
            Directory.CreateDirectory(parentDir) |> ignore
            File.WriteAllText(full, "")
        f dir
    finally
        Directory.Delete(dir, recursive = true)

// ---------------------------------------------------------------------------
// expand — plain path
// ---------------------------------------------------------------------------

[<Fact>]
let ``expand returns single-entry list for plain file path`` () =
    withTempDir ["job.yaml"] (fun dir ->
        let path = Path.Combine(dir, "job.yaml")
        match expand dir path with
        | Error e  -> Assert.Fail($"Expected Ok but got Error: {e}")
        | Ok paths -> Assert.Equal<string list>([ path ], paths))

[<Fact>]
let ``expand returns single-entry list for plain relative file path`` () =
    withTempDir ["job.yaml"] (fun dir ->
        match expand dir "job.yaml" with
        | Error e  -> Assert.Fail($"Expected Ok but got Error: {e}")
        | Ok paths ->
            let expected = Path.GetFullPath(Path.Combine(dir, "job.yaml"))
            Assert.Equal<string list>([ expected ], paths))

[<Fact>]
let ``expand returns Error for plain path that does not exist`` () =
    withTempDir [] (fun dir ->
        match expand dir "missing.yaml" with
        | Ok _    -> Assert.Fail("Expected Error but got Ok")
        | Error e -> Assert.Contains("not found", e))

// ---------------------------------------------------------------------------
// expand — glob pattern
// ---------------------------------------------------------------------------

[<Fact>]
let ``expand returns all matching paths for a glob pattern`` () =
    withTempDir ["a.yaml"; "b.yaml"; "notes.txt"] (fun dir ->
        match expand dir "*.yaml" with
        | Error e  -> Assert.Fail($"Expected Ok but got Error: {e}")
        | Ok paths ->
            Assert.Equal(2, paths.Length)
            Assert.All(paths, fun p -> Assert.EndsWith(".yaml", p)))

[<Fact>]
let ``expand returns Error when pattern matches zero files`` () =
    withTempDir ["notes.txt"] (fun dir ->
        match expand dir "*.yaml" with
        | Ok _    -> Assert.Fail("Expected Error but got Ok")
        | Error e -> Assert.Contains("No files matched", e))

[<Fact>]
let ``expand with double star pattern matches files in subdirectories`` () =
    withTempDir ["configs/a.yaml"; "configs/b.yaml"; "root.yaml"] (fun dir ->
        match expand dir "**/*.yaml" with
        | Error e  -> Assert.Fail($"Expected Ok but got Error: {e}")
        | Ok paths ->
            // Should match files in subdirectory and root
            Assert.True(paths.Length >= 2, $"Expected at least 2 matches but got {paths.Length}"))

// ---------------------------------------------------------------------------
// expandWith — fake DirectoryInfoBase
// ---------------------------------------------------------------------------

/// Minimal in-memory DirectoryInfoBase backed by a real temp dir for the
/// Execute call, but useful for verifying the testable overload is callable.
[<Fact>]
let ``expandWith supports direct invocation with DirectoryInfoWrapper`` () =
    withTempDir ["x.yaml"; "y.yaml"] (fun dir ->
        let wrapper = DirectoryInfoWrapper(System.IO.DirectoryInfo(dir))
        match expandWith wrapper "*.yaml" with
        | Error e  -> Assert.Fail($"Expected Ok but got Error: {e}")
        | Ok paths -> Assert.Equal(2, paths.Length))

[<Fact>]
let ``expandWith returns Error when pattern matches nothing`` () =
    withTempDir ["notes.txt"] (fun dir ->
        let wrapper = DirectoryInfoWrapper(System.IO.DirectoryInfo(dir))
        match expandWith wrapper "*.yaml" with
        | Ok _    -> Assert.Fail("Expected Error but got Ok")
        | Error e -> Assert.Contains("No files matched", e))
