module OrcAI.Core.Tests.LockFileTests

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
