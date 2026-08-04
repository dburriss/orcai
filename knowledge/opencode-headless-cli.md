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

## 3. Direct (no-shell) process launch silently no-ops opencode

This is the one that actually cost debugging time, because `1` and `2` alone did
**not** explain the failure — even with `--auto` and `-m` set, `opencode run`
launched as a **direct child process** (no shell in between — i.e. `execve`-style,
which is how .NET's `Process.Start`, Python's `subprocess.Popen`, and most
non-shell process launchers work) with stdio redirected, **exits 0 and never runs
its write tool**. No error, no diff — a clean-looking success that changed
nothing.

Reproduced independently in plain Python (not orcai-specific — this is an
`opencode` behavior triggered by how it's spawned):

| launch shape | result |
|---|---|
| direct `execve` (`Process.Start` / `subprocess.Popen`, no shell) | exit 0, **no file written** |
| `sh -c 'exec "$0" "$@"'` (shell re-execs immediately) | **hangs** |
| shebang launcher script (`#!/bin/sh` + `exec "$@"`), itself `execve`'d | exit 0, **file written correctly** |

So the *only* shape that reliably worked was going through an actual shebang
script file that `exec`s the real command — not an inline `sh -c` (which
deadlocked in testing) and not a direct launch (which silently no-ops).

### Why this matters for orcai specifically

`orcai`'s `cmd` / `cmd-checkout` / `cmd-to-pr` action types launch the configured
`execute` command as a direct child process (`System.Diagnostics.Process.Start`,
no shell) when using the exec/list form — exactly the shape that triggers this.
**A `cmd-to-pr` job that runs `opencode run` today will report `CmdToPrNoDiff`
("no diff after cmd succeeded") even when correctly configured with `--auto` and
`-m`**, because opencode itself made no change under this launch shape.

### Why a fix was not applied

The shebang-launcher workaround only exists on Unix — there is no equivalent
concept on Windows (no `#!` interpreter line, no `exec` syscall to replace the
process image), so any change to `orcai`'s process-launch behavior built around
this workaround would either not work on Windows or need a wholly different,
unverified mechanism there. Given `orcai` targets Windows as a supported platform
(`win-x64` is a published target — see `ARCHITECTURE.md` distribution section),
this was **not** implemented as a fix in `orcai`. It stays a documented limitation
here instead.

### Workarounds available today (without changing orcai)

- Wrap the `execute` command in your own shebang script (checked into the target
  repo or referenced by absolute path) instead of invoking `opencode` directly:
  ```sh
  #!/bin/sh
  exec opencode run --auto -m github-copilot/claude-sonnet-5 "$@"
  ```
  Then point the job's `execute:` at that script. This only helps on Unix
  runners/machines, for the same reason above.
- Run `opencode` under a shell explicitly via the **string (shell) form** of
  `execute:` rather than the list/exec form, if orcai's shell-dispatch path
  (`sh -c "..."` on Unix) turns out to behave differently — **not yet verified**;
  the deadlock reproduced above was with an *inline* `-c "exec ..."` construction,
  which may or may not match how orcai's own shell dispatch invokes it. Treat as
  untested until confirmed.
- If neither works reliably, treat `opencode run` as needing an interactive-ish
  invocation (e.g. `opencode run` behind `script`/a pty allocator) rather than a
  bare redirected-stdio child process — not explored here.
