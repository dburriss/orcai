module OrcAI.Core.Tests.OrcAIConfigTests

open Xunit
open Testably.Abstractions.Testing
open OrcAI.Core.OrcAIConfig

// ---------------------------------------------------------------------------
// merge tests
// ---------------------------------------------------------------------------

[<Fact>]
let ``merge: local Some value wins over global Some value`` () =
    let g = { empty with SkipCopilot = Some false; MaxConcurrency = Some 2 }
    let l = { empty with SkipCopilot = Some true;  MaxConcurrency = Some 8 }
    let m = merge g l
    Assert.Equal(Some true, m.SkipCopilot)
    Assert.Equal(Some 8,    m.MaxConcurrency)

[<Fact>]
let ``merge: global Some value used when local is None`` () =
    let g = { empty with SkipCopilot = Some true; DefaultOrg = Some "myorg" }
    let l = empty
    let m = merge g l
    Assert.Equal(Some true,    m.SkipCopilot)
    Assert.Equal(Some "myorg", m.DefaultOrg)

[<Fact>]
let ``merge: both None yields None`` () =
    let m = merge empty empty
    Assert.Equal(None, m.SkipCopilot)
    Assert.Equal(None, m.DefaultOrg)
    Assert.Equal(None, m.MaxConcurrency)
    Assert.Equal(None, m.ContinueOnError)
    Assert.Equal(None, m.AutoCreateLabels)
    Assert.Equal(None, m.DefaultLabels)

// ---------------------------------------------------------------------------
// resolve tests
// ---------------------------------------------------------------------------

[<Fact>]
let ``resolve: returns empty config when neither file exists`` () =
    let fs = MockFileSystem()
    let result = resolve (fs :> System.IO.Abstractions.IFileSystem) "/home/user" "/work"
    Assert.Equal(empty, result)

[<Fact>]
let ``resolve: reads and applies global config when only global exists`` () =
    let fs = MockFileSystem()
    fs.Directory.CreateDirectory("/home/user/.config/orcai") |> ignore
    fs.File.WriteAllText("/home/user/.config/orcai/config.json", """{"skipCopilot": true, "maxConcurrency": 8}""")
    let result = resolve (fs :> System.IO.Abstractions.IFileSystem) "/home/user" "/work"
    Assert.Equal(Some true, result.SkipCopilot)
    Assert.Equal(Some 8,    result.MaxConcurrency)

[<Fact>]
let ``resolve: local overrides global on overlapping keys`` () =
    let fs = MockFileSystem()
    fs.Directory.CreateDirectory("/home/user/.config/orcai") |> ignore
    fs.File.WriteAllText("/home/user/.config/orcai/config.json", """{"skipCopilot": true, "maxConcurrency": 2}""")
    fs.Directory.CreateDirectory("/work/.orcai") |> ignore
    fs.File.WriteAllText("/work/.orcai/config.json", """{"skipCopilot": false, "maxConcurrency": 8}""")
    let result = resolve (fs :> System.IO.Abstractions.IFileSystem) "/home/user" "/work"
    Assert.Equal(Some false, result.SkipCopilot)
    Assert.Equal(Some 8,     result.MaxConcurrency)

// ---------------------------------------------------------------------------
// readFile tests
// ---------------------------------------------------------------------------

[<Fact>]
let ``readFile: returns Ok empty for a missing file`` () =
    let fs = MockFileSystem()
    match readFile (fs :> System.IO.Abstractions.IFileSystem) "/nonexistent/config.json" with
    | Ok cfg -> Assert.Equal(empty, cfg)
    | Error e -> Assert.Fail($"Expected Ok empty but got Error: {e}")

[<Fact>]
let ``readFile: returns Error for malformed JSON`` () =
    let fs = MockFileSystem()
    fs.Directory.CreateDirectory("/work") |> ignore
    fs.File.WriteAllText("/work/config.json", "not valid json {{{")
    match readFile (fs :> System.IO.Abstractions.IFileSystem) "/work/config.json" with
    | Error _ -> ()
    | Ok _    -> Assert.Fail("Expected Error for malformed JSON but got Ok")

[<Fact>]
let ``readFile: parses all supported fields correctly`` () =
    let fs = MockFileSystem()
    let json = """
{
  "skipCopilot": true,
  "defaultLabels": ["bug", "enhancement"],
  "autoCreateLabels": true,
  "maxConcurrency": 6,
  "continueOnError": true,
  "defaultOrg": "acme"
}"""
    fs.Directory.CreateDirectory("/work") |> ignore
    fs.File.WriteAllText("/work/config.json", json)
    match readFile (fs :> System.IO.Abstractions.IFileSystem) "/work/config.json" with
    | Error e -> Assert.Fail($"Expected Ok but got Error: {e}")
    | Ok cfg ->
        Assert.Equal(Some true,                         cfg.SkipCopilot)
        Assert.Equal(Some ["bug"; "enhancement"],       cfg.DefaultLabels)
        Assert.Equal(Some true,                         cfg.AutoCreateLabels)
        Assert.Equal(Some 6,                            cfg.MaxConcurrency)
        Assert.Equal(Some true,                         cfg.ContinueOnError)
        Assert.Equal(Some "acme",                       cfg.DefaultOrg)
