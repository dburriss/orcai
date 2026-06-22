module OrcAI.Core.CheckoutManager

open System
open System.Diagnostics
open System.IO
open System.Text.RegularExpressions
open OrcAI.Core.Domain

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

/// Normalise a string to a valid git branch / directory segment.
/// Lowercase, replace non-alphanumeric runs with a single dash, trim
/// leading/trailing dashes, truncate to 100 characters.
let slugify (s: string) : string =
    let lower   = s.ToLowerInvariant()
    let dashed  = Regex.Replace(lower, "[^a-z0-9]+", "-")
    let trimmed = dashed.Trim('-')
    if trimmed.Length > 100 then trimmed.[..99] else trimmed

/// Run a process, collecting stdout and stderr concurrently to avoid deadlocks
/// when either buffer fills. Args are passed via ArgumentList so no shell
/// escaping is needed — each element is passed verbatim to the OS.
let private runProcess (executable: string) (args: string list) (workingDir: string) : Async<Result<string, string>> =
    async {
        let psi = ProcessStartInfo(executable)
        psi.WorkingDirectory       <- workingDir
        psi.RedirectStandardOutput <- true
        psi.RedirectStandardError  <- true
        psi.UseShellExecute        <- false
        for arg in args do psi.ArgumentList.Add(arg)
        use proc = Process.Start(psi)
        // Read stdout and stderr concurrently before waiting for exit to avoid
        // deadlock when both output streams fill their OS buffers simultaneously.
        let stdoutTask = proc.StandardOutput.ReadToEndAsync()
        let stderrTask = proc.StandardError.ReadToEndAsync()
        do! proc.WaitForExitAsync() |> Async.AwaitTask
        let! stdout = stdoutTask |> Async.AwaitTask
        let! stderr = stderrTask |> Async.AwaitTask
        if proc.ExitCode = 0 then
            return Ok stdout
        else
            return Error $"Exit {proc.ExitCode}: {stderr.Trim()}"
    }

/// Build the HTTPS URL for a repo, relying on the gh credential helper for auth.
let private repoUrl (RepoName repo) = $"https://github.com/{repo}.git"

/// Path for the bare base clone of a repo.
let basePath (checkoutRoot: string) (RepoName repo) : string =
    Path.Combine(checkoutRoot, repo.Replace('/', Path.DirectorySeparatorChar), "base.git")

/// Path for a worktree checked out for a specific branch slug.
let worktreePath (checkoutRoot: string) (RepoName repo) (branchSlug: string) : string =
    Path.Combine(checkoutRoot, repo.Replace('/', Path.DirectorySeparatorChar), branchSlug)

// ---------------------------------------------------------------------------
// Core operations
// ---------------------------------------------------------------------------

/// Ensure a bare shallow clone of `repo` exists at the base path.
/// If the directory already exists, assumes a valid clone and skips.
/// Returns the absolute base path on success.
let ensureClone (checkoutRoot: string) (repo: RepoName) : Async<Result<string, string>> =
    async {
        let base' = basePath checkoutRoot repo
        if Directory.Exists(base') then
            return Ok base'
        else
            try
                Directory.CreateDirectory(base') |> ignore
                let url     = repoUrl repo
                let parent  = Path.GetDirectoryName(base')
                let dirName = Path.GetFileName(base')
                // Configure the gh credential helper inline so we don't mutate global git config.
                let cloneArgs = ["-c"; "credential.helper=!gh auth git-credential"; "clone"; "--bare"; "--depth"; "1"; url; dirName]
                let! result = runProcess "git" cloneArgs parent
                match result with
                | Ok _ -> return Ok base'
                | Error e ->
                    try Directory.Delete(base', true) with _ -> ()
                    return Error $"Failed to clone {repo}: {e}"
            with ex ->
                try Directory.Delete(base', true) with _ -> ()
                return Error $"Failed to clone {repo}: {ex.Message}"
    }

/// Read the default branch name from an existing bare clone.
/// Uses `git symbolic-ref HEAD` which reliably reflects the upstream default.
let getDefaultBranch (checkoutRoot: string) (repo: RepoName) : Async<Result<string, string>> =
    async {
        let base' = basePath checkoutRoot repo
        let! result = runProcess "git" ["symbolic-ref"; "HEAD"] base'
        match result with
        | Error e -> return Error $"Could not read default branch: {e}"
        | Ok symref ->
            let branch = symref.Trim().Replace("refs/heads/", "")
            return Ok branch
    }

/// Get or create a worktree for `branchSlug` under `base'`.
/// Prunes stale worktree registrations before adding to avoid "already registered" errors.
/// Creates a fresh branch from HEAD; if the worktree path already exists returns it as-is.
/// Returns the absolute worktree path on success.
let getWorktree (checkoutRoot: string) (repo: RepoName) (branchSlug: string) : Async<Result<string, string>> =
    async {
        let wt    = worktreePath checkoutRoot repo branchSlug
        let base' = basePath     checkoutRoot repo
        if Directory.Exists(wt) then
            return Ok wt
        else
            // Prune stale registrations so a re-added branch slug doesn't fail.
            let! _ = runProcess "git" ["worktree"; "prune"] base'
            let! result = runProcess "git" ["worktree"; "add"; wt; "-b"; branchSlug] base'
            match result with
            | Ok _    -> return Ok wt
            | Error e -> return Error $"Failed to create worktree for {branchSlug}: {e}"
    }

/// Remove the worktree at `branchSlug`.
let cleanup (checkoutRoot: string) (repo: RepoName) (branchSlug: string) : unit =
    let wt    = worktreePath checkoutRoot repo branchSlug
    let base' = basePath     checkoutRoot repo
    if Directory.Exists(wt) then
        try
            let psi = ProcessStartInfo("git")
            psi.WorkingDirectory       <- base'
            psi.RedirectStandardOutput <- true
            psi.RedirectStandardError  <- true
            psi.UseShellExecute        <- false
            psi.ArgumentList.Add("worktree")
            psi.ArgumentList.Add("remove")
            psi.ArgumentList.Add(wt)
            psi.ArgumentList.Add("--force")
            use proc = Process.Start(psi)
            proc.WaitForExit()
        with _ -> ()
        try if Directory.Exists(wt) then Directory.Delete(wt, true) with _ -> ()

/// Remove the entire checkout root directory for a repo (base clone + all worktrees).
let cleanupAll (checkoutRoot: string) (repo: RepoName) : unit =
    let (RepoName r) = repo
    let repoDir = Path.Combine(checkoutRoot, r.Replace('/', Path.DirectorySeparatorChar))
    try if Directory.Exists(repoDir) then Directory.Delete(repoDir, true) with _ -> ()

// ---------------------------------------------------------------------------
// Git operations used by cmd-to-pr write-back
// ---------------------------------------------------------------------------

/// Stage all changes and commit in the given worktree directory.
/// Injects a fallback git identity for CI runners that have none configured.
let commitAll (worktreeDir: string) (message: string) : Async<Result<unit, string>> =
    async {
        let! addResult = runProcess "git" ["add"; "-A"] worktreeDir
        match addResult with
        | Error e -> return Error $"git add failed: {e}"
        | Ok _ ->
            // Exit 0 from diff --quiet means no staged changes; non-zero means changes exist.
            let! diffResult = runProcess "git" ["diff"; "--cached"; "--quiet"] worktreeDir
            match diffResult with
            | Ok _ ->
                return Error "no-diff"
            | Error _ ->
                let psi = ProcessStartInfo("git")
                psi.WorkingDirectory       <- worktreeDir
                psi.RedirectStandardOutput <- true
                psi.RedirectStandardError  <- true
                psi.UseShellExecute        <- false
                psi.ArgumentList.Add("commit")
                psi.ArgumentList.Add("-m")
                psi.ArgumentList.Add(message)
                // Provide a fallback identity for CI runners with no git config.
                psi.Environment["GIT_AUTHOR_NAME"]     <- "orcai"
                psi.Environment["GIT_AUTHOR_EMAIL"]    <- "orcai@users.noreply.github.com"
                psi.Environment["GIT_COMMITTER_NAME"]  <- "orcai"
                psi.Environment["GIT_COMMITTER_EMAIL"] <- "orcai@users.noreply.github.com"
                use proc = Process.Start(psi)
                let stdoutTask = proc.StandardOutput.ReadToEndAsync()
                let stderrTask = proc.StandardError.ReadToEndAsync()
                do! proc.WaitForExitAsync() |> Async.AwaitTask
                let! _ = stdoutTask |> Async.AwaitTask
                let! stderr = stderrTask |> Async.AwaitTask
                if proc.ExitCode = 0 then
                    return Ok ()
                else
                    return Error $"git commit failed: Exit {proc.ExitCode}: {stderr.Trim()}"
    }

/// Push the worktree's current HEAD to a named remote branch with force-with-lease.
/// Uses HEAD:refs/heads/<remoteBranch> so the local branch name is irrelevant —
/// only the current HEAD commit and the desired remote branch name matter.
let pushToOrigin (_basePath: string) (worktreeDir: string) (remoteBranch: string) : Async<Result<unit, string>> =
    async {
        let pushArgs = ["-c"; "credential.helper=!gh auth git-credential"; "push"; "--force-with-lease"; "origin"; $"HEAD:refs/heads/{remoteBranch}"]
        let! result = runProcess "git" pushArgs worktreeDir
        match result with
        | Ok _    -> return Ok ()
        | Error e -> return Error $"git push failed: {e}"
    }

/// Fork the repo and push the branch to the fork.
/// Returns the fork's owner/repo string on success.
let forkAndPush (repo: RepoName) (worktreeDir: string) (branchSlug: string) : Async<Result<string, string>> =
    async {
        let (RepoName repoStr) = repo
        // Fork (idempotent — gh fork returns existing fork if already forked).
        let! forkResult = runProcess "gh" ["repo"; "fork"; repoStr; "--clone=false"] worktreeDir
        match forkResult with
        | Error e -> return Error $"gh repo fork failed: {e}"
        | Ok _ ->
            // Determine the authenticated user's login to construct the fork name.
            // gh repo view (no --repo) would return the *origin* repo, not the fork.
            let! userResult = runProcess "gh" ["api"; "user"; "-q"; ".login"] worktreeDir
            match userResult with
            | Error e -> return Error $"Failed to resolve authenticated user: {e}"
            | Ok userLogin ->
                let login    = userLogin.Trim()
                let repoName = repoStr.Split('/') |> Array.last
                let forkRepo = $"{login}/{repoName}"
                let forkUrl  = $"https://github.com/{forkRepo}.git"
                let! _ = runProcess "git" ["remote"; "add"; "fork"; forkUrl] worktreeDir
                // Ignore error if remote already exists.
                let pushArgs = ["-c"; "credential.helper=!gh auth git-credential"; "push"; "--force-with-lease"; "fork"; $"HEAD:refs/heads/{branchSlug}"]
                let! pushResult = runProcess "git" pushArgs worktreeDir
                match pushResult with
                | Error e -> return Error $"Push to fork failed: {e}"
                | Ok _    -> return Ok forkRepo
    }
