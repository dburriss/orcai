# Plan: Propagate the resolved auth token to checkout git/PR subprocesses

## Goal

Make `cmd-checkout` and `cmd-to-pr` work under **GitHub App** and **stored-PAT**
auth, not just ambient `GH_TOKEN` / `gh auth login`. Today the code-change half of
these actions (clone, push, `gh pr create`) silently relies on the runner's ambient
`gh` credentials and ignores the token OrcAI resolved from `IAuthContext`.

Motivating use case: an Actions workflow running an `orcai` job in one repo, using
`cmd-to-pr` to run OpenCode against **other repos in the org** and open PRs
(`writeBack: pr-to-origin`). With App-first auth (`ORCAI_APP_*`) and no ambient
`GH_TOKEN`, issue/project creation succeeds but clone/push/PR fail.

---

## Problem (verified in code)

OrcAI resolves one token from `IAuthContext` (priority: App → PAT → `GH_TOKEN` →
`gh auth token`, `Program.fs:34`). That token reaches the `gh` **API** calls only —
`GhCliClient` injects it per-call as `GH_TOKEN` (`GhClient.fs:23,30`).

The checkout path never receives it:

| Subprocess | Location | Auth today |
|---|---|---|
| `git clone --bare` | `CheckoutManager.ensureClone` (`CheckoutManager.fs:65`) | credential helper `!gh auth git-credential` → reads **ambient** `GH_TOKEN` |
| `git push` | `CheckoutManager.pushToOrigin` (`:193`) | same, ambient |
| `gh repo fork` / `gh api user` / push | `CheckoutManager.forkAndPush` (`:204`) | ambient |
| `gh pr create` | `RunCommand` `openPr` (`RunCommand.fs:671`) | ambient (`psi2` sets no env) |

`CheckoutManager.runProcess` (`:25`) sets no `GH_TOKEN`; `RunCommand` resolves the
token at `:769` purely to validate it, then discards it (`Result.map ignore`).

Net effect: under App or stored-PAT auth (which never export an ambient `GH_TOKEN`),
the credential helper's child `gh` has no token and clone/push/PR against the target
repos fail. It only "works" today when auth already came from an ambient `GH_TOKEN`
or a prior `gh auth login`.

---

## Fix

Thread the already-resolved token into every auth-requiring checkout subprocess and
set `GH_TOKEN` on that subprocess's environment — mirroring the existing per-call
injection pattern in `GhClient` (`GhClient.fs:29`). No global env mutation, no new
auth resolution.

### 1. `CheckoutManager.fs`

- Give `runProcess` an env parameter and set it on the `ProcessStartInfo`:
  ```fsharp
  let private runProcess (executable: string) (args: string list)
                         (env: (string * string) list) (workingDir: string)
                         : Async<Result<string, string>> =
      ...
      for (k, v) in env do psi.Environment[k] <- v
      ...
  ```
  Update the existing callers that need no auth (`getDefaultBranch`, `getWorktree`,
  and the `git add`/`diff` in `commitAll`) to pass `[]`.
- Add a `token: string` parameter to the three auth-requiring functions and pass
  `(if token = "" then [] else [ "GH_TOKEN", token ])`:
  - `ensureClone token checkoutRoot repo` — the `git ... clone` process. The
    `credential.helper=!gh auth git-credential` child inherits the parent git
    process env, so `GH_TOKEN` set here reaches it.
  - `pushToOrigin token worktreeDir remoteBranch` — the `git push` process.
  - `forkAndPush token repo worktreeDir branchSlug` — the `gh repo fork`,
    `gh api user`, and `git push` processes. (Unused by `pr-to-origin`; updated for
    consistency and so App-vs-PAT limitations are enforced in one place, see notes.)
- Keep the empty-token guard so an ambient-`gh`-login setup (where `GetToken`
  returned the same token via `gh auth token`, always non-empty on `Ok`) is never
  clobbered with a blank value.

### 2. `RunCommand.fs`

- **Capture** the token instead of discarding it. At `:766`:
  ```fsharp
  let tokenResult =
      match config.Provider with
      | Local  -> Ok ""
      | GitHub -> deps.AuthContext.GetToken() |> Async.RunSynchronously
  match tokenResult with
  | Error e -> Error $"Auth error: {e}"
  | Ok checkoutToken -> ...
  ```
- **Plumb** it to `processRepo` via `ProcessParams` (`:162`): add
  `CheckoutToken : string`, populate it in `resolveProcessParams` (`:178`, add a
  parameter) and at both call sites (`:810`, `:1028`).
- **Use** it in the two checkout branches:
  - `CmdCheckout` (`:518`) and `CmdToPr` (`:592`): pass `p.CheckoutToken` to
    `ensureClone`, `pushToOrigin`, `forkAndPush`.
  - `openPr` (`:671`): `psi2.Environment["GH_TOKEN"] <- p.CheckoutToken` when
    non-empty, before `Process.Start`.

### 3. Nothing changes in auth resolution

`Program.fs` already resolves App → PAT → `GH_TOKEN` → ambient in the desired order
and `RunCommand` already holds `deps.AuthContext`. This plan only stops the resolved
token from being thrown away.

---

## Tests

- **`CheckoutManagerTests.fs`** — make the env injection assertable without hitting
  real GitHub. Point `runProcess`/`ensureClone`/`pushToOrigin` at a throwaway script
  (`sh -c 'echo "$GH_TOKEN"'`, or a temp executable on Windows) and assert stdout
  contains the injected token; assert an empty token injects no `GH_TOKEN` and the
  no-auth helpers (`getWorktree`, `getDefaultBranch`) never set it.
- **`RunCommandTests.fs`** — extract the `gh pr create` `ProcessStartInfo` builder
  into a small pure helper (`buildPrCreatePsi token repo head title body`) and unit
  test that `GH_TOKEN` is present iff the token is non-empty. Keeps the assertion off
  a live `gh` invocation.
- Regression: an existing `cmd-to-pr` test path with `Provider = GitHub` and a fake
  token still round-trips (token now flows but behaviour is unchanged when the
  ambient env already had it).

---

## Docs

- `docs/app-auth.md` §8 — reword "OrcAI configures `git` to use the same token
  automatically" to state that OrcAI now **injects the resolved App/PAT token** into
  the clone/push/PR subprocesses, so no ambient `GH_TOKEN` or `gh auth login` is
  required on the runner.
- `docs/AUTH-ENV-VARS.md` — note that `cmd-checkout`/`cmd-to-pr` now work under
  `ORCAI_APP_*` or a stored PAT profile alone.
- `ARCHITECTURE.md` "Auth Resolution" — fix the stale order (it lists PAT before App;
  the implementation is App → PAT → `GH_TOKEN` → ambient).

---

## Notes / follow-ups (out of scope)

- **`fork-and-pr` + App is unsupported.** `forkAndPush` calls `gh api user`, which an
  App **installation** token cannot satisfy (no user context), and forks land under a
  personal account. Fork-based write-back requires a user PAT. Track a separate change
  to fail fast with a clear message when `writeBack: fork-and-pr` is combined with App
  auth, rather than a cryptic `gh api user` error. (Not needed for the `pr-to-origin`
  use case that motivates this plan.)
- **Installation-token TTL.** App tokens expire (~60 min). The token is resolved once
  per run and reused for every repo's checkout; a run longer than the TTL could see
  pushes fail late. Re-minting mid-run is out of scope here.
