module Orca.Core.Tests.RunCommandTests

open Xunit
open Orca.Core.RunCommand

// ---------------------------------------------------------------------------
// Unit tests for the pure labelsToCreate helper.
// ---------------------------------------------------------------------------

[<Fact>]
let ``labelsToCreate returns empty when no labels requested`` () =
    Assert.Empty(labelsToCreate ["bug"; "documentation"] [])

[<Fact>]
let ``labelsToCreate returns empty when all requested labels already exist`` () =
    Assert.Empty(labelsToCreate ["bug"; "documentation"] ["bug"; "documentation"])

[<Fact>]
let ``labelsToCreate returns missing labels`` () =
    Assert.Equal<string list>(["new-label"], labelsToCreate ["bug"] ["bug"; "new-label"])

[<Fact>]
let ``labelsToCreate returns all labels when none exist`` () =
    Assert.Equal<string list>(["alpha"; "beta"], labelsToCreate [] ["alpha"; "beta"])

[<Fact>]
let ``labelsToCreate is case-insensitive for existing labels`` () =
    Assert.Empty(labelsToCreate ["BUG"; "Documentation"] ["bug"; "documentation"])

[<Fact>]
let ``labelsToCreate is case-insensitive for requested labels`` () =
    Assert.Empty(labelsToCreate ["bug"; "documentation"] ["BUG"; "Documentation"])

[<Fact>]
let ``labelsToCreate preserves original casing of missing labels`` () =
    Assert.Equal<string list>(["My-Label"; "Another Label"], labelsToCreate [] ["My-Label"; "Another Label"])

[<Fact>]
let ``labelsToCreate returns empty when existing labels list is empty and no labels requested`` () =
    Assert.Empty(labelsToCreate [] [])
