# Running `opencode run` headlessly (e.g. from `orcai` `cmd`/`cmd-to-pr`)

Findings from manually testing `action: { type: cmd-to-pr }` driving `opencode run`
against a real repo (see `orcai-tests/manual-tests/opencode/`). Two are ordinary
CLI-flag requirements; the third is a deeper process-launch quirk worth knowing
about before relying on `opencode run` from any non-shell launcher (orcai's `cmd`
action types, a custom script, CI systems that spawn processes directly, etc.).

## 1. `--auto` is required

Headless `opencode run` has no TTY to approve its edit/write tools. Without
`--auto`, it answers the prompt but makes no changes and exits `0` — a silent
no-op that looks like success.

```
opencode run --auto "<prompt>"
```

## 2. A model must be pinned explicitly

With no default model configured (`~/.config/opencode/opencode.jsonc` empty aside
from `$schema`), headless `run` resolves no model and — like the missing-`--auto`
case — exits `0` having done nothing. Pin one:

```
opencode run --auto -m github-copilot/claude-sonnet-5 "<prompt>"
```

List available models (including Copilot-backed ones, if that's your provider)
with `opencode models`.

## 3. Direct (no-shell) process launch could hang or silently no-op opencode — fixed

This is the one that actually cost debugging time, because `1` and `2` alone did
**not** explain the failure — even with `--auto` and `-m` set, `opencode run`
launched as a **direct child process** (no shell in between — i.e. `execve`-style,
which is how .NET's `Process.Start`, Python's `subprocess.Popen`, and most
non-shell process launchers work) with stdio redirected could hang, or exit 0
having never run its write tool. No error, no diff — a clean-looking success
that changed nothing.

**Root cause (confirmed from opencode's source):** `packages/opencode/src/cli/cmd/run.ts`
does `process.stdin.isTTY ? undefined : await Bun.stdin.text()` before doing
anything else — whenever stdin isn't a TTY (true for any non-shell/headless
launch), it eagerly reads stdin **to EOF** first. If the child inherits an
ambiguous stdin handle that never closes, this blocks forever. If stdin instead
reaches EOF immediately, it proceeds normally. There's a matching unresolved
upstream report of the same symptom class in CI/subprocess contexts:
[sst/opencode#13851](https://github.com/sst/opencode/issues/13851) — treat this
fix as addressing the one confirmed mechanism, not as a guaranteed cure for
every headless-invocation failure mode.

### The fix (implemented in orcai)

`src/OrcAI.Core/RunCommand.fs` now explicitly sets `RedirectStandardInput <- true`
and immediately calls `proc.StandardInput.Close()` right after `Process.Start`,
via a shared `runExecCommand` helper used by all three of `cmd` / `cmd-checkout` /
`cmd-to-pr`. This guarantees the child observes an instant EOF on stdin instead
of inheriting orcai's own ambiguous stdin handle — no shell wrapping, no shebang
script, and no Windows-vs-Unix branching required; `RedirectStandardInput` +
`StandardInput.Close()` is a plain, cross-platform .NET `Process` API pattern.

The previously-considered shebang-script/`exec` workaround (Unix-only, no
Windows equivalent — no `#!` interpreter line, no `exec` syscall to replace the
process image) is **no longer needed** and was not implemented.

### Verification status

- A regression unit test (`runExecCommand closes child stdin so a process
  reading stdin to EOF exits immediately instead of hanging`,
  `tests/OrcAI.Core.Tests/RunCommandTests.fs`) exercises the general mechanism
  using `cat`/`more` as a stand-in for "a process that blocks reading stdin to
  EOF" — no opencode/API-key dependency required to run it in CI.
  **Caveat:** whether this test can actually demonstrate the bug pre-fix depends
  on the *ambient* stdin of whatever process runs the test suite. In sandboxed
  environments (and likely most CI runners, which redirect step stdin from
  something already closed/`/dev/null`), the test passes even *before* the fix
  is applied, because the ambient stdin is already at EOF regardless. It only
  reliably reproduces the hang when run from a shell with a real,
  open/non-closing stdin handle.
- An A/B repro against the real, locally installed `opencode` CLI, launched
  exactly like orcai's `ProcessStartInfo`, succeeded identically with and
  without this specific fix, for the same ambient-stdin reason above — it did
  **not** reproduce a hang in that environment either way. This fix stays in
  place because it's correct and harmless regardless, but it turned out **not**
  to be the cause of the `CmdToPrNoDiff` failure actually observed in
  production — see §4 below for that.

## 4. Child resolves its working directory from inherited `PWD`, not the real cwd

This is the actual cause of a real `cmd-to-pr` run against `opencode run`
reporting `CmdToPrNoDiff` ("no diff after cmd succeeded") in production, even
with `--auto`, `-m`, and the stdin fix from §3 all in place.

**Root cause:** .NET's `ProcessStartInfo.WorkingDirectory` changes the child's
OS-level working directory (a `chdir` before exec) but does **not** touch the
inherited `PWD` environment variable — the child gets whatever `PWD` was in
*orcai's own* environment (typically whatever directory the user's shell was
in when they launched `orcai`). `opencode run` — a Bun/Node CLI — resolves its
own project root from that inherited `PWD` rather than the real cwd, so it
silently operates on (and writes into) wherever the invoking shell happened to
be, ignoring `WorkingDirectory`/the checkout worktree entirely. Confirmed by
direct repro: launching `opencode run` with a worktree's path as
`WorkingDirectory` but an unrelated `PWD` inherited from the parent shell wrote
the requested file into that unrelated directory instead — reproduced both as
"one level up" from an ephemeral checkout and, separately, into a completely
different real repo on disk that happened to match the invoking shell's `PWD`.
Because `commitAll`/`git diff` only look inside the actual worktree, this shows
up as "no diff after cmd succeeded", not an error.

### The fix (implemented in orcai)

`buildExecPsi` in `src/OrcAI.Core/RunCommand.fs` now explicitly sets
`psi.Environment["PWD"] <- workingDir`, keeping the env var in sync with
`WorkingDirectory` for every child process launched via `runExecCommand`
(`cmd`/`cmd-checkout`/`cmd-to-pr`). Confirmed by re-running the same direct
repro with this override in place: the file landed inside the intended
worktree and `git status` picked it up correctly.

A unit test (`buildExecPsi sets PWD to match workingDir so children that trust
inherited PWD over the real cwd aren't misdirected`,
`tests/OrcAI.Core.Tests/RunCommandTests.fs`) asserts this deterministically —
no opencode dependency, since it only inspects the constructed `ProcessStartInfo`.

`errorIfNoDiff: true` remains a good safety net for `cmd-to-pr` jobs driving
opencode regardless, given [sst/opencode#13851](https://github.com/sst/opencode/issues/13851)
suggests other headless-invocation failure modes may still exist upstream.
