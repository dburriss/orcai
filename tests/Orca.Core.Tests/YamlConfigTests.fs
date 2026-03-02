module Orca.Core.Tests.YamlConfigTests

open Xunit
open Orca.Core.YamlConfig

// ---------------------------------------------------------------------------
// Unit tests for YAML config parsing and hash computation.
// ---------------------------------------------------------------------------

[<Fact>]
let ``parseFile returns error for missing file`` () =
    let result = parseFile "/nonexistent/path/job.yml"
    Assert.True(Result.isError result)
