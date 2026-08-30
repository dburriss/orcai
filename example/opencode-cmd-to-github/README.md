# OpenCode `cmd-to-github` example

Run [OpenCode](https://opencode.ai) against **many repos in an org** and open a
pull request in each — all from one orcai job. The job runs in an *orchestration*
repo and acts on the *target* repos listed in the config.

This example adds an `AGENTS.md` to each target repo, but the pattern works for any
change OpenCode can make: dependency bumps, codemods, config rollouts, etc.

## Files

| File | Purpose |
|---|---|
| `add-agents-md.yml` | The orcai job — a `cmd-to-github` action that invokes OpenCode |
| `add-agents-md.md` | The issue template; OpenCode receives its rendered text as the task prompt |
| `orcai-bulk-pr.yml` | Example GitHub Actions workflow (goes in the orchestration repo's `.github/workflows/`) |

## What happens per repo

For each repo in `repos:`, `orcai run` does:

1. Create/find the GitHub Project and open a templated issue in the repo.
2. **Shallow-clone** the repo into a scratch dir and make a `git worktree` on a
   fresh branch (`orcai/add-agents-md-via-opencode`).
3. Run `opencode run "<issue body>"` **inside the checkout**. OpenCode edits the
   working tree.
4. `git add -A` + commit. If OpenCode made no change (e.g. `AGENTS.md` already
   exists), the empty diff is skipped silently — no PR, no failure.
5. Push the branch (`--force-with-lease`) and `gh pr create` against the default
   branch. The PR body's `Closes #<n>` links it to the issue and the Project board.
6. Clean up the checkout.

Outcomes (`pr-opened`, `no-changes`, `cmd-failed`, `push-failed`, …) are recorded
in `add-agents-md.lock.json`, so re-runs skip repos already done.

## The prompt

`add-agents-md.md` is rendered and passed to OpenCode via `{{issue_text}}`. The job
uses the **exec (list) form** of `execute:`:

```yaml
execute:
  - "opencode"
  - "run"
  - "--auto"
  - "-m"
  - "github-copilot/claude-sonnet-5"
  - "{{issue_text}}"
```

No shell is involved, so the multi-line Markdown body — quotes, backticks and all —
is delivered to OpenCode as a single argument. Prefer this over the string form for
anything but the simplest one-line commands.

Two flags are essential for headless `opencode run`, or it exits 0 having changed
nothing (which `cmd-to-github` then records as "no changes"):

- **`--auto`** — there's no TTY to approve OpenCode's edit/write tools.
- **`-m <provider/model>`** — with no default model in your OpenCode config, headless
  `run` resolves no model and silently no-ops. Pin one (e.g. a `github-copilot/…`
  model if you use Copilot as your provider; run `opencode models` to list).

The template instructs the agent to edit the working tree only (not commit/push);
orcai owns the git and PR steps.

## Prerequisites

- **.NET 10** and the `orcai` tool (`dotnet tool install --global OrcAI.Tool`).
- **OpenCode** on `PATH` (`npm install -g opencode-ai`, or the install script).
- **`gh` CLI** on `PATH`.
- A **model provider key** for OpenCode (e.g. `ANTHROPIC_API_KEY`) in the environment.

## Auth: reaching the *other* repos

The job runs in one repo but clones, pushes, and opens PRs in others. The default
Actions `GITHUB_TOKEN` is scoped to the current repo only and **cannot** do that, so
you must supply a cross-repo credential with:

- **Contents: Read & write** (clone + push)
- **Pull requests: Read & write** (open PRs)

Pick one:

- **GitHub App (recommended for orgs)** — install an App on the org with the two
  permissions above. In CI, mint an installation token
  (`actions/create-github-app-token`) and export it as `GH_TOKEN`, as the workflow
  does. Locally, configure App auth with `orcai auth app`.
- **PAT** — a fine-grained token (Contents + PR R/W on the targets) or a classic
  `repo`-scoped token, exported as `GH_TOKEN` or stored via `orcai auth pat`.

> **Why export as `GH_TOKEN`?** orcai's clone/push/`gh pr create` currently use the
> runner's ambient `gh` credentials via the `!gh auth git-credential` helper.
> Exporting the cross-repo token as `GH_TOKEN` makes the gh API, git, and PR steps
> all use the same token. Setting `ORCAI_APP_*`/`ORCAI_PAT` *alone* covers the
> issue/project API calls but **not** the checkout steps — until the fix in
> [`plans/checkout-auth-token-propagation.md`](../../plans/checkout-auth-token-propagation.md)
> lands. This example uses the `GH_TOKEN` export so it works today.

## Run it

In CI: trigger the `Bulk AGENTS.md via OpenCode` workflow (`workflow_dispatch`).
Set the `ORCAI_APP_ID`, `ORCAI_APP_PRIVATE_KEY`, and `ANTHROPIC_API_KEY` secrets first.

Locally (with a `gh` already authenticated to a cross-repo token, or `GH_TOKEN` set):

```bash
export ANTHROPIC_API_KEY=sk-...
orcai run example/opencode-cmd-to-github/add-agents-md.yml --continue-on-error --json
```

Add `--max-concurrency 2` to limit parallel checkouts, and use `orcai validate` first
to confirm the config parses and every repo is reachable.

## Adapting it

- Point `repos:` at your own org and repositories.
- Change `job.org` and the App/token `owner` to match.
- Swap `add-agents-md.md` for your own task prompt.
- Change the OpenCode model with a flag, e.g. add `"--model"`, `"anthropic/<model>"`
  to the `execute:` list.

## Cleanup

`orcai cleanup example/opencode-cmd-to-github/add-agents-md.yml` tears down the issues and
Project created by the run. It does **not** close or revert the PRs — review, merge,
or close those on GitHub as usual.
