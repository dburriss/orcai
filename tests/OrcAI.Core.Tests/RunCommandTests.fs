module OrcAI.Core.Tests.RunCommandTests

open System.Collections.Concurrent
open Xunit
open Testably.Abstractions.Testing
open OrcAI.Core.Domain
open OrcAI.Core.RunCommand
open OrcAI.Core.Tests.TestData

// ---------------------------------------------------------------------------
// labelsToCreate — pure helper
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

// ---------------------------------------------------------------------------
// Multi-file execute tests
// ---------------------------------------------------------------------------

[<Fact>]
let ``execute processes all files in a resolved path list`` () =
    let fs    = MockFileSystem()
    let path1 = Given.namedYamlFile fs "job1.yml"
    let path2 = Given.namedYamlFile fs "job2.yml"
    let deps  = Given.deps fs (FakeGhClient.from FakeGhClient.defaults)

    let results = execute deps [path1; path2] (A.RunInput.defaults ()) |> Async.RunSynchronously

    Assert.Equal(2, results.Count)
    Assert.True(results |> Map.forall (fun _ r -> match r with Ok _ -> true | Error _ -> false))

[<Fact>]
let ``execute result is always a filename-keyed dictionary`` () =
    let fs   = MockFileSystem()
    let path = Given.namedYamlFile fs "job.yml"
    let deps = Given.deps fs (FakeGhClient.from FakeGhClient.defaults)

    let results = execute deps [path] (A.RunInput.defaults ()) |> Async.RunSynchronously

    Assert.True(results.ContainsKey(path))

[<Fact>]
let ``execute stops on first failure without ContinueOnError (sequential)`` () =
    let fs    = MockFileSystem()
    let path1 = Given.namedYamlFile fs "job1.yml"
    let path2 = Given.namedYamlFile fs "job2.yml"
    (fs :> System.IO.Abstractions.IFileSystem).File.WriteAllText(path1, "not: valid: yaml: at: all\n!!!")
    let deps  = Given.deps fs (FakeGhClient.from FakeGhClient.defaults)
    let input = A.RunInput.defaults () |> A.RunInput.withNoParallel true

    let results = execute deps [path1; path2] input |> Async.RunSynchronously

    Assert.True(results.ContainsKey(path1))
    match results.[path1] with
    | Error _ -> ()
    | Ok _    -> Assert.Fail("Expected path1 to fail")
    Assert.False(results.ContainsKey(path2), "path2 should not have been processed")

[<Fact>]
let ``execute continues past failures with ContinueOnError`` () =
    let fs    = MockFileSystem()
    let path1 = Given.namedYamlFile fs "job1.yml"
    let path2 = Given.namedYamlFile fs "job2.yml"
    (fs :> System.IO.Abstractions.IFileSystem).File.WriteAllText(path1, "not: valid: yaml: at: all\n!!!")
    let deps  = Given.deps fs (FakeGhClient.from FakeGhClient.defaults)
    let input =
        A.RunInput.defaults ()
        |> A.RunInput.withNoParallel true
        |> A.RunInput.withContinueOnError true

    let results = execute deps [path1; path2] input |> Async.RunSynchronously

    Assert.True(results.ContainsKey(path1))
    Assert.True(results.ContainsKey(path2))
    match results.[path1] with
    | Error _ -> ()
    | Ok _    -> Assert.Fail("Expected path1 to fail")
    match results.[path2] with
    | Ok _    -> ()
    | Error e -> Assert.Fail($"Expected path2 to succeed but got: {e}")

[<Fact>]
let ``execute with NoParallel=true processes files sequentially and returns correct results`` () =
    let fs    = MockFileSystem()
    let path1 = Given.namedYamlFile fs "job1.yml"
    let path2 = Given.namedYamlFile fs "job2.yml"
    let deps  = Given.deps fs (FakeGhClient.from FakeGhClient.defaults)
    let input =
        A.RunInput.defaults ()
        |> A.RunInput.withNoParallel true
        |> A.RunInput.withContinueOnError true

    let results = execute deps [path1; path2] input |> Async.RunSynchronously

    Assert.Equal(2, results.Count)
    Assert.True(results |> Map.forall (fun _ r -> match r with Ok _ -> true | Error _ -> false))

// ---------------------------------------------------------------------------
// Copilot assignment / mixed-auth tests
// ---------------------------------------------------------------------------

[<Fact>]
let ``processRepo uses CopilotClient when Some for AssignIssue`` () =
    let fs          = MockFileSystem()
    let path        = Given.namedYamlFile fs "job.yml"
    let assignCalls = ConcurrentBag<string>()
    let primary     = FakeGhClient.from { FakeGhClient.defaults with AssignIssue = FakeGhClient.trackingAssign "primary" assignCalls }
    let copilot     = FakeGhClient.from { FakeGhClient.defaults with AssignIssue = FakeGhClient.trackingAssign "copilot"  assignCalls }
    let deps        = { Given.deps fs primary with CopilotClient = Some copilot }
    let input       = A.RunInput.defaults () |> A.RunInput.withSkipCopilot false |> A.RunInput.withIsPrimaryAuthApp true

    let results = execute deps [path] input |> Async.RunSynchronously

    Assert.True(results |> Map.forall (fun _ r -> match r with Ok _ -> true | Error _ -> false))
    Assert.Contains("copilot", assignCalls)
    Assert.DoesNotContain("primary", assignCalls)

[<Fact>]
let ``processRepo uses primary client for AssignIssue when CopilotClient is None and IsPrimaryAuthApp=false`` () =
    let fs          = MockFileSystem()
    let path        = Given.namedYamlFile fs "job.yml"
    let assignCalls = ConcurrentBag<string>()
    let primary     = FakeGhClient.from { FakeGhClient.defaults with AssignIssue = FakeGhClient.trackingAssign "primary" assignCalls }
    let deps        = Given.deps fs primary
    let input       = A.RunInput.defaults () |> A.RunInput.withSkipCopilot false

    let results = execute deps [path] input |> Async.RunSynchronously

    Assert.True(results |> Map.forall (fun _ r -> match r with Ok _ -> true | Error _ -> false))
    Assert.Contains("primary", assignCalls)

[<Fact>]
let ``processRepo skips assignment and does not call AssignIssue when CopilotClient=None and IsPrimaryAuthApp=true`` () =
    let fs          = MockFileSystem()
    let path        = Given.namedYamlFile fs "job.yml"
    let assignCalls = ConcurrentBag<string>()
    let primary     = FakeGhClient.from { FakeGhClient.defaults with AssignIssue = FakeGhClient.trackingAssign "primary" assignCalls }
    let deps        = Given.deps fs primary
    let input       = A.RunInput.defaults () |> A.RunInput.withSkipCopilot false |> A.RunInput.withIsPrimaryAuthApp true

    let results = execute deps [path] input |> Async.RunSynchronously

    Assert.True(results |> Map.forall (fun _ r -> match r with Ok _ -> true | Error _ -> false))
    Assert.Empty(assignCalls)

[<Fact>]
let ``processRepo skips assignment entirely when skipCopilot=true regardless of CopilotClient`` () =
    let fs          = MockFileSystem()
    let path        = Given.namedYamlFile fs "job.yml"
    let assignCalls = ConcurrentBag<string>()
    let primary     = FakeGhClient.from { FakeGhClient.defaults with AssignIssue = FakeGhClient.trackingAssign "primary" assignCalls }
    let copilot     = FakeGhClient.from { FakeGhClient.defaults with AssignIssue = FakeGhClient.trackingAssign "copilot"  assignCalls }
    let deps        = { Given.deps fs primary with CopilotClient = Some copilot }
    let input       = A.RunInput.defaults () |> A.RunInput.withIsPrimaryAuthApp true

    let results = execute deps [path] input |> Async.RunSynchronously

    Assert.True(results |> Map.forall (fun _ r -> match r with Ok _ -> true | Error _ -> false))
    Assert.Empty(assignCalls)

// ---------------------------------------------------------------------------
// ClosedIssueAction tests
// ---------------------------------------------------------------------------

let private closedIssueClient () =
    FakeGhClient.from
        { FakeGhClient.defaults with
            FindClosedIssue = fun repo _ -> async { return Some (FakeGhClient.issueFor repo 7) }
            ReopenIssue     = fun repo _ -> async { return Ok (FakeGhClient.issueFor repo 7) }
            CreateIssue     = fun _ _ _ _ -> async { return failwith "CreateIssue should not be called" } }

[<Fact>]
let ``reopen action reopens closed issue and returns Reopened outcome`` () =
    let fs    = MockFileSystem()
    let path  = Given.namedYamlFile fs "job.yml"
    let deps  = Given.deps fs (closedIssueClient ())
    let input = A.RunInput.defaults () |> A.RunInput.withOnClosedIssue (Some Reopen)

    let results = execute deps [path] input |> Async.RunSynchronously

    Assert.True(results.ContainsKey(path))
    match results.[path] with
    | Error e -> Assert.Fail($"Expected Ok but got Error: {e}")
    | Ok result ->
        Assert.True(result.Results |> List.forall (fun r -> r.Outcome = Reopened))

[<Fact>]
let ``reopen action does not call CreateIssue when closed issue exists`` () =
    let fs    = MockFileSystem()
    let path  = Given.namedYamlFile fs "job.yml"
    let deps  = Given.deps fs (closedIssueClient ())
    let input = A.RunInput.defaults () |> A.RunInput.withOnClosedIssue (Some Reopen)

    let results = execute deps [path] input |> Async.RunSynchronously

    Assert.True(results |> Map.forall (fun _ r -> match r with Ok _ -> true | Error _ -> false))

[<Fact>]
let ``skip action returns Skipped outcome without creating or reopening`` () =
    let fs    = MockFileSystem()
    let path  = Given.namedYamlFile fs "job.yml"
    let pc    = ConcurrentBag<unit>()
    let ac    = ConcurrentBag<unit>()
    let client =
        FakeGhClient.from
            { FakeGhClient.defaults with
                FindClosedIssue   = fun repo _ -> async { return Some (FakeGhClient.issueFor repo 7) }
                CreateIssue       = fun _ _ _ _ -> async { return failwith "CreateIssue not expected" }
                AddIssueToProject = FakeGhClient.trackingAddIssue pc
                AssignIssue       = FakeGhClient.trackingAssignUnit ac }
    let deps  = Given.deps fs client
    let input = A.RunInput.defaults () |> A.RunInput.withOnClosedIssue (Some Skip)

    let results = execute deps [path] input |> Async.RunSynchronously

    Assert.True(results.ContainsKey(path))
    match results.[path] with
    | Error e -> Assert.Fail($"Expected Ok but got Error: {e}")
    | Ok result ->
        Assert.True(result.Results |> List.forall (fun r -> r.Outcome = Skipped))

[<Fact>]
let ``skip action does not add issue to project or assign copilot`` () =
    let fs    = MockFileSystem()
    let path  = Given.namedYamlFile fs "job.yml"
    let pc    = ConcurrentBag<unit>()
    let ac    = ConcurrentBag<unit>()
    let client =
        FakeGhClient.from
            { FakeGhClient.defaults with
                FindClosedIssue   = fun repo _ -> async { return Some (FakeGhClient.issueFor repo 7) }
                CreateIssue       = fun _ _ _ _ -> async { return failwith "CreateIssue not expected" }
                AddIssueToProject = FakeGhClient.trackingAddIssue pc
                AssignIssue       = FakeGhClient.trackingAssignUnit ac }
    let deps  = Given.deps fs client
    let input =
        A.RunInput.defaults ()
        |> A.RunInput.withOnClosedIssue (Some Skip)
        |> A.RunInput.withSkipCopilot false

    execute deps [path] input |> Async.RunSynchronously |> ignore

    Assert.Empty(pc)
    Assert.Empty(ac)

[<Fact>]
let ``fail action returns error and does not create or reopen`` () =
    let fs    = MockFileSystem()
    let path  = Given.namedYamlFile fs "job.yml"
    let client =
        FakeGhClient.from
            { FakeGhClient.defaults with
                FindClosedIssue   = fun repo _ -> async { return Some (FakeGhClient.issueFor repo 7) }
                CreateIssue       = fun _ _ _ _ -> async { return failwith "CreateIssue not expected" }
                AddIssueToProject = fun _ _     -> async { return failwith "AddIssueToProject not expected" }
                AssignIssue       = fun _ _ _   -> async { return failwith "AssignIssue not expected" } }
    let deps  = Given.deps fs client
    let input = A.RunInput.defaults () |> A.RunInput.withOnClosedIssue (Some Fail)

    let results = execute deps [path] input |> Async.RunSynchronously

    Assert.True(results.ContainsKey(path))
    match results.[path] with
    | Ok result -> Assert.Empty(result.Results)
    | Error _   -> ()

[<Fact>]
let ``create action (default) creates new issue even when closed issue exists`` () =
    let fs              = MockFileSystem()
    let path            = Given.namedYamlFile fs "job.yml"
    let createCallCount = ConcurrentBag<unit>()
    let client =
        FakeGhClient.from
            { FakeGhClient.defaults with
                FindClosedIssue = fun repo _ -> async { return Some (FakeGhClient.issueFor repo 7) }
                CreateIssue     = fun repo _ _ _ ->
                    createCallCount.Add(())
                    async { return Ok (FakeGhClient.issueFor repo 99) } }
    let deps  = Given.deps fs client
    let input = A.RunInput.defaults () |> A.RunInput.withOnClosedIssue (Some Create)

    let results = execute deps [path] input |> Async.RunSynchronously

    Assert.True(results |> Map.forall (fun _ r -> match r with Ok _ -> true | Error _ -> false))
    Assert.NotEmpty(createCallCount)
    match results.[path] with
    | Error e -> Assert.Fail($"Expected Ok but got Error: {e}")
    | Ok result ->
        Assert.True(result.Results |> List.forall (fun r -> r.Outcome = Created))
