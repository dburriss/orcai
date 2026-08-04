module OrcAI.Core.Tests.FileGlobTests

open System.IO
open Xunit
open OrcAI.Core.Domain
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

/// Create two temporary directories (source, populated with `files`; dest, empty),
/// run a test, then clean up both.
let private withSourceAndDestDir (files: string list) (f: string -> string -> unit) =
    withTempDir files (fun sourceDir ->
        let destDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName())
        Directory.CreateDirectory(destDir) |> ignore
        try
            f sourceDir destDir
        finally
            Directory.Delete(destDir, recursive = true))

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
// expand — brace expansion
// ---------------------------------------------------------------------------

[<Fact>]
let ``expand matches both extensions with brace expansion`` () =
    withTempDir ["a.yml"; "b.yaml"; "notes.txt"] (fun dir ->
        match expand dir "*.{yml,yaml}" with
        | Error e  -> Assert.Fail($"Expected Ok but got Error: {e}")
        | Ok paths ->
            Assert.Equal(2, paths.Length)
            Assert.All(paths, fun p -> Assert.True(p.EndsWith(".yml") || p.EndsWith(".yaml"))))

[<Fact>]
let ``expand with brace expansion returns Error when nothing matches`` () =
    withTempDir ["notes.txt"] (fun dir ->
        match expand dir "*.{yml,yaml}" with
        | Ok _    -> Assert.Fail("Expected Error but got Ok")
        | Error e -> Assert.Contains("No files matched", e))

[<Fact>]
let ``expand with brace expansion works in subdirectory patterns`` () =
    withTempDir ["configs/a.yml"; "configs/b.yaml"] (fun dir ->
        match expand dir "configs/*.{yml,yaml}" with
        | Error e  -> Assert.Fail($"Expected Ok but got Error: {e}")
        | Ok paths -> Assert.Equal(2, paths.Length))

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

// ---------------------------------------------------------------------------
// copyEntry / copyAll / cleanupCopies
// ---------------------------------------------------------------------------

[<Fact>]
let ``copyEntry copies single-match file to exact destination path`` () =
    withSourceAndDestDir ["script.sh"] (fun sourceDir destDir ->
        let entry = { From = "script.sh"; To = "staged.sh"; Keep = false }
        match copyEntry sourceDir destDir entry with
        | Error e -> Assert.Fail($"Expected Ok but got Error: {e}")
        | Ok paths ->
            let expected = Path.Combine(destDir, "staged.sh")
            Assert.Equal<string list>([ expected ], paths)
            Assert.True(File.Exists(expected)))

[<Fact>]
let ``copyEntry copies glob matches into destination directory preserving relative structure`` () =
    withSourceAndDestDir ["scripts/a.sh"; "scripts/nested/b.sh"] (fun sourceDir destDir ->
        let entry = { From = "scripts/**/*.sh"; To = "staged"; Keep = false }
        match copyEntry sourceDir destDir entry with
        | Error e -> Assert.Fail($"Expected Ok but got Error: {e}")
        | Ok paths ->
            Assert.Equal(2, paths.Length)
            Assert.True(File.Exists(Path.Combine(destDir, "staged", "a.sh")))
            Assert.True(File.Exists(Path.Combine(destDir, "staged", "nested", "b.sh"))))

[<Fact>]
let ``copyEntry returns Error when pattern matches zero files`` () =
    withSourceAndDestDir ["notes.txt"] (fun sourceDir destDir ->
        let entry = { From = "*.sh"; To = "staged.sh"; Keep = false }
        match copyEntry sourceDir destDir entry with
        | Ok _    -> Assert.Fail("Expected Error but got Ok")
        | Error e -> Assert.Contains("No files matched", e))

[<Fact>]
let ``copyAll copies every entry and short-circuits on first zero-match error`` () =
    withSourceAndDestDir ["a.sh"; "b.sh"] (fun sourceDir destDir ->
        let entries =
            [ { From = "a.sh"; To = "a.sh"; Keep = false }
              { From = "missing.sh"; To = "missing.sh"; Keep = false }
              { From = "b.sh"; To = "b.sh"; Keep = false } ]
        match copyAll sourceDir destDir entries with
        | Ok _    -> Assert.Fail("Expected Error but got Ok")
        | Error e -> Assert.Contains("not found", e)
        // The third entry (b.sh) must not have been copied since the second entry failed.
        Assert.False(File.Exists(Path.Combine(destDir, "b.sh"))))

[<Fact>]
let ``copyAll pairs each destination path with its entry's Keep flag`` () =
    withSourceAndDestDir ["a.sh"; "b.sh"] (fun sourceDir destDir ->
        let entries =
            [ { From = "a.sh"; To = "a.sh"; Keep = false }
              { From = "b.sh"; To = "b.sh"; Keep = true } ]
        match copyAll sourceDir destDir entries with
        | Error e -> Assert.Fail($"Expected Ok but got Error: {e}")
        | Ok written ->
            Assert.Equal<(string * bool) list>(
                [ Path.Combine(destDir, "a.sh"), false
                  Path.Combine(destDir, "b.sh"), true ],
                written))

[<Fact>]
let ``cleanupCopies removes only entries where Keep is false`` () =
    withSourceAndDestDir ["a.sh"; "b.sh"] (fun sourceDir destDir ->
        let entries =
            [ { From = "a.sh"; To = "a.sh"; Keep = false }
              { From = "b.sh"; To = "b.sh"; Keep = true } ]
        match copyAll sourceDir destDir entries with
        | Error e -> Assert.Fail($"Expected Ok but got Error: {e}")
        | Ok written ->
            cleanupCopies written
            Assert.False(File.Exists(Path.Combine(destDir, "a.sh")))
            Assert.True(File.Exists(Path.Combine(destDir, "b.sh"))))
