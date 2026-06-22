module OrcAI.Core.Tests.CheckoutManagerTests

open System.IO
open System.Diagnostics
open Xunit
open OrcAI.Core.CheckoutManager
open OrcAI.Core.Domain

// ---------------------------------------------------------------------------
// slugify — pure, no I/O
// ---------------------------------------------------------------------------

[<Fact>]
let ``slugify lowercases input`` () =
    Assert.Equal("hello-world", slugify "Hello World")

[<Fact>]
let ``slugify replaces spaces and punctuation with dashes`` () =
    Assert.Equal("upgrade-to-net-10", slugify "Upgrade to .NET 10")

[<Fact>]
let ``slugify collapses consecutive non-alphanumeric runs`` () =
    Assert.Equal("foo-bar", slugify "foo  --  bar")

[<Fact>]
let ``slugify trims leading and trailing dashes`` () =
    Assert.Equal("foo", slugify "  foo  ")

[<Fact>]
let ``slugify truncates to 100 characters`` () =
    let long = System.String('a', 200)
    Assert.Equal(100, (slugify long).Length)

[<Fact>]
let ``slugify handles empty string`` () =
    Assert.Equal("", slugify "")

[<Fact>]
let ``slugify preserves digits`` () =
    Assert.Equal("dotnet-10", slugify "dotnet 10")

// ---------------------------------------------------------------------------
// Path helpers — pure
// ---------------------------------------------------------------------------

[<Fact>]
let ``basePath builds correct path`` () =
    let repo = RepoName "my-org/my-repo"
    let result = basePath "/tmp/orcai" repo
    Assert.Contains("my-org", result)
    Assert.Contains("my-repo", result)
    Assert.Contains("base.git", result)

[<Fact>]
let ``worktreePath builds correct path`` () =
    let repo = RepoName "my-org/my-repo"
    let result = worktreePath "/tmp/orcai" repo "upgrade-to-net-10"
    Assert.Contains("my-org", result)
    Assert.Contains("my-repo", result)
    Assert.Contains("upgrade-to-net-10", result)

[<Fact>]
let ``cwd combined with worktreePath is a subdirectory of worktreePath`` () =
    // Verifies the invariant that cwd is relative to the checkout root.
    // Path.Combine is the mechanism used in RunCommand CmdCheckout/CmdToPr branches.
    let repo = RepoName "my-org/my-repo"
    let wt   = worktreePath "/tmp/orcai" repo "branch"
    let resolved = System.IO.Path.Combine(wt, "./subdir")
    Assert.StartsWith(wt, resolved)
    Assert.True(resolved.Length > wt.Length)

[<Fact>]
let ``cwd defaults to worktreePath when None`` () =
    let repo = RepoName "my-org/my-repo"
    let wt   = worktreePath "/tmp/orcai" repo "branch"
    let resolved = None |> Option.map (fun c -> System.IO.Path.Combine(wt, c)) |> Option.defaultValue wt
    Assert.Equal(wt, resolved)

// ---------------------------------------------------------------------------
// pushToOrigin — local branch name vs remote branch name
// ---------------------------------------------------------------------------

[<Fact>]
let ``pushToOrigin can push HEAD to a remote branch whose name differs from the local branch`` () =
    // Reproduces the cmd-to-pr push bug:
    // getWorktree creates local branch "test-slug" but pushToOrigin is called
    // with "orcai/test-slug" (the rendered branch name, e.g. "orcai/{{job_title_slug}}"),
    // causing `git push origin orcai/test-slug` to fail with
    // "src refspec does not match any".
    let guid      = System.Guid.NewGuid().ToString("N")
    let seedDir   = Path.Combine(Path.GetTempPath(), $"orcai-seed-{guid}")  // non-bare origin with a commit
    let remoteDir = Path.Combine(Path.GetTempPath(), $"orcai-r-{guid}")     // bare clone used as "origin" for push
    let baseDir   = Path.Combine(Path.GetTempPath(), $"orcai-b-{guid}")     // bare clone used as "base" (ensureClone)
    let wtDir     = Path.Combine(Path.GetTempPath(), $"orcai-w-{guid}")     // worktree
    let run (exe: string) (args: string list) (wd: string) =
        let psi = ProcessStartInfo(exe)
        psi.WorkingDirectory       <- wd
        psi.RedirectStandardOutput <- true
        psi.RedirectStandardError  <- true
        psi.UseShellExecute        <- false
        for a in args do psi.ArgumentList.Add(a)
        use p = Process.Start(psi)
        p.WaitForExit()
        p.ExitCode = 0
    try
        // 1. Create a non-bare seed repo with one commit, then bare-clone it as "origin".
        //    This gives us a bare remote whose HEAD has a valid commit to branch from.
        Directory.CreateDirectory(seedDir) |> ignore
        Assert.True(run "git" ["-c"; "init.defaultBranch=main"; "init"; seedDir] (Path.GetTempPath()), "git init seed")
        File.WriteAllText(Path.Combine(seedDir, "README.md"), "init")
        Assert.True(run "git" ["add"; "README.md"] seedDir, "git add in seed")
        Assert.True(run "git" ["-c"; "user.email=t@t.com"; "-c"; "user.name=t"; "commit"; "-m"; "init"] seedDir, "git commit in seed")
        Assert.True(run "git" ["clone"; "--bare"; seedDir; remoteDir] (Path.GetTempPath()), "git clone bare as remote")

        // 2. Bare clone (simulating ensureClone against remoteDir as the push target)
        Assert.True(run "git" ["clone"; "--bare"; remoteDir; baseDir] (Path.GetTempPath()), "git clone bare as base")

        // 3. Worktree on branch "test-slug" (simulating getWorktree with branchSlug only)
        Assert.True(run "git" ["worktree"; "add"; wtDir; "-b"; "test-slug"] baseDir, "git worktree add")

        // 4. Make a diff-producing change and commit in the worktree
        File.WriteAllText(Path.Combine(wtDir, "touch.txt"), "hello")
        Assert.True(run "git" ["add"; "-A"] wtDir, "git add in wt")
        Assert.True(run "git" ["-c"; "user.email=t@t.com"; "-c"; "user.name=t"; "commit"; "-m"; "test"] wtDir, "git commit in wt")

        // 5. pushToOrigin called with "orcai/test-slug" (rendered branch name).
        //    Local branch is "test-slug" — the names differ.
        //    Before fix: fails with "src refspec orcai/test-slug does not match any".
        //    After fix:  succeeds via HEAD:refs/heads/orcai/test-slug.
        let result =
            pushToOrigin baseDir wtDir "orcai/test-slug"
            |> Async.RunSynchronously
        Assert.True((result = Ok ()), $"Expected push to succeed but got: {result}")
    finally
        try Directory.Delete(seedDir,   true) with _ -> ()
        try Directory.Delete(remoteDir, true) with _ -> ()
        try Directory.Delete(baseDir,   true) with _ -> ()
        try if Directory.Exists(wtDir) then Directory.Delete(wtDir, true) with _ -> ()

// ---------------------------------------------------------------------------
// LockFile failure category round-trip — all RepoFailureCategory values must
// survive mergeFailures (which calls categoryToString when sorting) without
// throwing a MatchFailureException.
// ---------------------------------------------------------------------------

[<Fact>]
let ``mergeFailures handles all checkout RepoFailureCategory values without crash`` () =
    // Pass all 5 checkout categories in one call so the sort must invoke categoryToString
    // for each of them — the single-element short-circuit in List.sortBy would skip it.
    let now  = System.DateTimeOffset.UtcNow
    let repos =
        [ RepoName "org/r1", CmdCheckoutFailed
          RepoName "org/r2", CmdToPrCheckoutFailed
          RepoName "org/r3", CmdToPrNoDiff
          RepoName "org/r4", CmdToPrPushFailed
          RepoName "org/r5", CmdToPrOpenPrFailed ]
    let attempted = repos |> List.map (fun (repo, cat) -> repo, cat, Error "test error")
    let result = OrcAI.Core.LockFile.mergeFailures [] attempted now
    Assert.Equal(5, result.Length)

// ---------------------------------------------------------------------------
// Shell execute form — cross-platform shell dispatch
// ---------------------------------------------------------------------------

[<Fact>]
let ``Shell execute form dispatches via platform shell so redirection works`` () =
    let guid    = System.Guid.NewGuid().ToString("N")
    let outFile = Path.Combine(Path.GetTempPath(), $"orcai-shell-{guid}.txt")
    try
        let cmd = $"echo orcai > {outFile}"
        let exe, args =
            if System.OperatingSystem.IsWindows() then "cmd", ["/C"; cmd]
            else "sh", ["-c"; cmd]
        let psi = ProcessStartInfo(exe)
        psi.UseShellExecute <- false
        for a in args do psi.ArgumentList.Add(a)
        use proc = Process.Start(psi)
        proc.WaitForExit()
        Assert.True(File.Exists(outFile), "Shell redirect should have created the output file")
    finally
        if File.Exists(outFile) then File.Delete(outFile)
