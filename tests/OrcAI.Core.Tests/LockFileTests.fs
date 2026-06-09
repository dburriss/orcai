module OrcAI.Core.Tests.LockFileTests

open System
open Xunit
open Testably.Abstractions.Testing
open OrcAI.Core.LockFile
open OrcAI.Core.Domain
open OrcAI.Core.Tests.TestData

[<Fact>]
let ``lockFilePath derives correct path from yaml path`` () =
    Assert.Equal("/projects/myjob.lock.json", lockFilePath "/projects/myjob.yml")

[<Fact>]
let ``lockFilePath handles yaml file without directory`` () =
    Assert.Equal("job.lock.json", lockFilePath "job.yml")

[<Fact>]
let ``tryRead returns None when lock file does not exist`` () =
    let fs = MockFileSystem()
    Assert.True((tryRead fs "/work/job.yml").IsNone)

[<Fact>]
let ``write then tryRead round-trips the lock file`` () =
    let fs       = MockFileSystem()
    let original = A.LockFile.defaults ()
    write fs "/work/job.yml" original
    match tryRead fs "/work/job.yml" with
    | None      -> Assert.Fail("Expected Some but got None")
    | Some read ->
        Assert.Equal(original.YamlHash,        read.YamlHash)
        Assert.Equal(original.Project.Number,  read.Project.Number)
        Assert.Equal(original.Project.Title,   read.Project.Title)
        Assert.Equal(original.Project.Org,     read.Project.Org)
        Assert.Equal(original.Repos.Length,    read.Repos.Length)
        Assert.Equal(original.Issues.Length,   read.Issues.Length)
        Assert.Equal(original.PullRequests.Length, read.PullRequests.Length)

[<Fact>]
let ``write creates a JSON file at the expected path`` () =
    let fs = MockFileSystem()
    write fs "/work/myjob.yml" (A.LockFile.defaults ())
    Assert.True(fs.File.Exists("/work/myjob.lock.json"), "Expected lock file at /work/myjob.lock.json")

[<Fact>]
let ``write preserves lockedAt timestamp`` () =
    let fs   = MockFileSystem()
    let lock = A.LockFile.defaults ()
    write fs "/work/job.yml" lock
    match tryRead fs "/work/job.yml" with
    | None      -> Assert.Fail("Expected Some but got None")
    | Some read -> Assert.Equal(lock.LockedAt.ToUnixTimeSeconds(), read.LockedAt.ToUnixTimeSeconds())

[<Fact>]
let ``write round-trips issue assignees`` () =
    let fs = MockFileSystem()
    write fs "/work/job.yml" (A.LockFile.defaults ())
    match tryRead fs "/work/job.yml" with
    | None      -> Assert.Fail("Expected Some but got None")
    | Some read -> Assert.Contains("copilot", (List.head read.Issues).Assignees)

[<Fact>]
let ``round-trip preserves all repo names`` () =
    let fs   = MockFileSystem()
    let lock = A.LockFile.defaults ()
    write fs "/work/job.yml" lock
    match tryRead fs "/work/job.yml" with
    | None      -> Assert.Fail("Expected Some")
    | Some read ->
        Assert.Equal<string list>(
            lock.Repos |> List.map (fun (RepoName r) -> r),
            read.Repos |> List.map (fun (RepoName r) -> r))

[<Fact>]
let ``round-trip preserves issue number and URL`` () =
    let fs = MockFileSystem()
    write fs "/work/job.yml" (A.LockFile.defaults ())
    match tryRead fs "/work/job.yml" with
    | None      -> Assert.Fail("Expected Some")
    | Some read ->
        let issue = List.head read.Issues
        Assert.Equal(IssueNumber 7, issue.Number)
        Assert.Equal("https://github.com/myorg/repo-a/issues/7", issue.Url)

[<Fact>]
let ``round-trip preserves PR number, URL, and closesIssue`` () =
    let fs = MockFileSystem()
    write fs "/work/job.yml" (A.LockFile.defaults ())
    match tryRead fs "/work/job.yml" with
    | None      -> Assert.Fail("Expected Some")
    | Some read ->
        let pr = List.head read.PullRequests
        Assert.Equal(PrNumber 3,    pr.Number)
        Assert.Equal("https://github.com/myorg/repo-a/pull/3", pr.Url)
        Assert.Equal(IssueNumber 7, pr.ClosesIssue)

[<Fact>]
let ``round-trip preserves project org, title, number, and URL`` () =
    let fs   = MockFileSystem()
    let lock = A.LockFile.defaults ()
    write fs "/work/job.yml" lock
    match tryRead fs "/work/job.yml" with
    | None      -> Assert.Fail("Expected Some")
    | Some read ->
        Assert.Equal(lock.Project.Org,    read.Project.Org)
        Assert.Equal(lock.Project.Number, read.Project.Number)
        Assert.Equal(lock.Project.Title,  read.Project.Title)
        Assert.Equal(lock.Project.Url,    read.Project.Url)

[<Fact>]
let ``round-trip preserves yaml hash`` () =
    let fs   = MockFileSystem()
    let lock = A.LockFile.defaults () |> A.LockFile.withHash "deadbeefcafe1234"
    write fs "/work/job.yml" lock
    match tryRead fs "/work/job.yml" with
    | None      -> Assert.Fail("Expected Some")
    | Some read -> Assert.Equal("deadbeefcafe1234", read.YamlHash)

[<Fact>]
let ``round-trip preserves skippedRepos list`` () =
    let fs   = MockFileSystem()
    let lock =
        { A.LockFile.defaults () with
            SkippedRepos = [ RepoName "myorg/archived-a"; RepoName "myorg/archived-b" ] }
    write fs "/work/job.yml" lock
    match tryRead fs "/work/job.yml" with
    | None      -> Assert.Fail("Expected Some")
    | Some read ->
        Assert.Equal<string list>(
            [ "myorg/archived-a"; "myorg/archived-b" ],
            read.SkippedRepos |> List.map (fun (RepoName r) -> r))

[<Fact>]
let ``tryRead deserialises lock files written before skippedRepos field was added`` () =
    let fs   = MockFileSystem()
    let path = "/work/job.lock.json"
    (fs :> System.IO.Abstractions.IFileSystem).Directory.CreateDirectory("/work") |> ignore
    let legacyJson =
        """{
          "lockedAt":     "2026-03-02T10:00:00+00:00",
          "yamlHash":     "abc123",
          "templateHash": "def456",
          "project":      { "org": "myorg", "number": 1, "title": "P", "url": "https://github.com/orgs/myorg/projects/1" },
          "repos":        ["myorg/repo-a"],
          "issues":       [],
          "pullRequests": []
        }"""
    (fs :> System.IO.Abstractions.IFileSystem).File.WriteAllText(path, legacyJson)
    match tryRead fs "/work/job.yml" with
    | None      -> Assert.Fail("Expected Some")
    | Some read -> Assert.Empty(read.SkippedRepos)

[<Fact>]
let ``tryRead deserialises lock files written before failures field was added`` () =
    let fs   = MockFileSystem()
    let path = "/work/job.lock.json"
    (fs :> System.IO.Abstractions.IFileSystem).Directory.CreateDirectory("/work") |> ignore
    let legacyJson =
        """{
          "lockedAt":     "2026-03-02T10:00:00+00:00",
          "yamlHash":     "abc123",
          "templateHash": "def456",
          "project":      { "org": "myorg", "number": 1, "title": "P", "url": "https://github.com/orgs/myorg/projects/1" },
          "repos":        ["myorg/repo-a"],
          "issues":       [],
          "pullRequests": [],
          "skippedRepos": []
        }"""
    (fs :> System.IO.Abstractions.IFileSystem).File.WriteAllText(path, legacyJson)
    match tryRead fs "/work/job.yml" with
    | None      -> Assert.Fail("Expected Some")
    | Some read -> Assert.Empty(read.Failures)

[<Fact>]
let ``round-trip preserves failures list`` () =
    let fs   = MockFileSystem()
    let at   = DateTimeOffset(2026, 5, 20, 10, 0, 0, TimeSpan.Zero)
    let lock =
        { A.LockFile.defaults () with
            Failures =
                [ { Repo          = RepoName "myorg/repo-a"
                    Category      = AssignIssue
                    Cause         = UserError
                    Attempts      = 2
                    FirstFailedAt = at
                    LastFailedAt  = at.AddDays(1.0)
                    LastMessage   = "could not resolve user 'typo'" } ] }
    write fs "/work/job.yml" lock
    match tryRead fs "/work/job.yml" with
    | None      -> Assert.Fail("Expected Some")
    | Some read ->
        Assert.Single(read.Failures) |> ignore
        let f = List.head read.Failures
        Assert.Equal(RepoName "myorg/repo-a", f.Repo)
        Assert.Equal(AssignIssue, f.Category)
        Assert.Equal(UserError, f.Cause)
        Assert.Equal(2, f.Attempts)
        Assert.Equal("could not resolve user 'typo'", f.LastMessage)

// ---------------------------------------------------------------------------
// classifyCause
// ---------------------------------------------------------------------------

[<Theory>]
[<InlineData("API rate limit exceeded")>]
[<InlineData("You have exceeded a secondary rate limit")>]
[<InlineData("abuse detection mechanism triggered")>]
[<InlineData("Issues are being submitted too quickly")>]
let ``classifyCause maps rate-limit messages to RateLimit`` (msg: string) =
    Assert.Equal(RateLimit, classifyCause msg)

[<Theory>]
[<InlineData("Could not resolve user 'dburris-typo'")>]
[<InlineData("no such user: someone")>]
[<InlineData("Invalid login")>]
[<InlineData("invalid user provided")>]
let ``classifyCause maps user-error messages to UserError`` (msg: string) =
    Assert.Equal(UserError, classifyCause msg)

[<Theory>]
[<InlineData("Could not resolve to an Issue or Pull Request")>]
[<InlineData("HTTP 404 Not Found")>]
[<InlineData("not found")>]
let ``classifyCause maps not-found messages to NotFound`` (msg: string) =
    Assert.Equal(NotFound, classifyCause msg)

[<Theory>]
[<InlineData("HTTP 403 Forbidden")>]
[<InlineData("permission denied")>]
[<InlineData("forbidden resource")>]
let ``classifyCause maps permission messages to Permission`` (msg: string) =
    Assert.Equal(Permission, classifyCause msg)

[<Theory>]
[<InlineData("connection refused")>]
[<InlineData("connection reset by peer")>]
[<InlineData("tls handshake failed")>]
[<InlineData("network unreachable")>]
[<InlineData("request timeout")>]
let ``classifyCause maps transient network messages to NetworkTransient`` (msg: string) =
    Assert.Equal(NetworkTransient, classifyCause msg)

[<Fact>]
let ``classifyCause falls back to Unknown for unfamiliar messages`` () =
    Assert.Equal(Unknown, classifyCause "Something completely unexpected happened")

[<Fact>]
let ``classifyCause handles null safely`` () =
    Assert.Equal(Unknown, classifyCause (Unchecked.defaultof<string>))

[<Fact>]
let ``classifyCause maps 'could not resolve user' to UserError, not NotFound`` () =
    // UserError must beat NotFound — "could not resolve" appears in both patterns.
    Assert.Equal(UserError, classifyCause "Could not resolve user 'foo'")

// ---------------------------------------------------------------------------
// mergeFailures
// ---------------------------------------------------------------------------

let private repoA = RepoName "myorg/repo-a"
let private repoB = RepoName "myorg/repo-b"
let private now0  = DateTimeOffset(2026, 5, 20, 10, 0, 0, TimeSpan.Zero)
let private now1  = DateTimeOffset(2026, 5, 21, 10, 0, 0, TimeSpan.Zero)

let private mkFailure repo cat cause attempts msg =
    { Repo          = repo
      Category      = cat
      Cause         = cause
      Attempts      = attempts
      FirstFailedAt = now0
      LastFailedAt  = now0
      LastMessage   = msg }

[<Fact>]
let ``mergeFailures adds a fresh failure when none existed for that key`` () =
    let merged =
        mergeFailures
            []
            [ repoA, AssignIssue, Error "could not resolve user 'foo'" ]
            now1
    Assert.Single(merged) |> ignore
    let f = List.head merged
    Assert.Equal(repoA,        f.Repo)
    Assert.Equal(AssignIssue,  f.Category)
    Assert.Equal(UserError,    f.Cause)
    Assert.Equal(1,            f.Attempts)
    Assert.Equal(now1,         f.FirstFailedAt)
    Assert.Equal(now1,         f.LastFailedAt)

[<Fact>]
let ``mergeFailures increments attempts and preserves firstFailedAt on repeat failure`` () =
    let prior = [ mkFailure repoA AssignIssue UserError 1 "first msg" ]
    let merged =
        mergeFailures
            prior
            [ repoA, AssignIssue, Error "second msg" ]
            now1
    let f = List.head merged
    Assert.Equal(2,    f.Attempts)
    Assert.Equal(now0, f.FirstFailedAt)
    Assert.Equal(now1, f.LastFailedAt)
    Assert.Equal("second msg", f.LastMessage)

[<Fact>]
let ``mergeFailures removes entry when matching attempt succeeds`` () =
    let prior = [ mkFailure repoA AssignIssue UserError 2 "old" ]
    let merged =
        mergeFailures
            prior
            [ repoA, AssignIssue, Ok () ]
            now1
    Assert.Empty(merged)

[<Fact>]
let ``mergeFailures preserves unrelated failures that were not attempted this run`` () =
    let prior =
        [ mkFailure repoA AssignIssue  UserError    2 "assign err"
          mkFailure repoB AddToProject NetworkTransient 1 "net err" ]
    let merged =
        mergeFailures
            prior
            [ repoA, AssignIssue, Ok () ]
            now1
    Assert.Single(merged) |> ignore
    let f = List.head merged
    Assert.Equal(repoB,        f.Repo)
    Assert.Equal(AddToProject, f.Category)

[<Fact>]
let ``mergeFailures keys by (repo, category) — different categories are independent`` () =
    let prior = [ mkFailure repoA AssignIssue UserError 1 "assign" ]
    let merged =
        mergeFailures
            prior
            [ repoA, AddToProject, Error "rate limit exceeded" ]
            now1
    Assert.Equal(2, List.length merged)
    let assignF  = merged |> List.find (fun f -> f.Category = AssignIssue)
    let projectF = merged |> List.find (fun f -> f.Category = AddToProject)
    Assert.Equal(1,         assignF.Attempts)
    Assert.Equal(1,         projectF.Attempts)
    Assert.Equal(RateLimit, projectF.Cause)

[<Fact>]
let ``mergeFailures updates Cause from the latest error message`` () =
    let prior = [ mkFailure repoA UpdateBody NetworkTransient 1 "transient" ]
    let merged =
        mergeFailures
            prior
            [ repoA, UpdateBody, Error "API rate limit exceeded" ]
            now1
    let f = List.head merged
    Assert.Equal(RateLimit, f.Cause)
    Assert.Equal(2,         f.Attempts)
