# Add an AGENTS.md to this repository

## Role

You are a developer working inside a checked-out copy of a repository. Make the
change directly in the working tree — edit and create files. Do **not** commit,
push, or open a pull request; the orchestration tool handles that after you exit.

## Objective

Add a concise `AGENTS.md` at the repository root that tells AI assistants how to
work with this codebase: how to build it, how to test it, and any conventions.

## Instructions

1. Check whether `AGENTS.md` already exists at the repository root.
   - **If it exists, make no changes and exit.** (An empty diff is expected and fine.)
2. Otherwise, inspect the repository to determine:
   - the primary language(s) and framework(s)
   - the build command(s)
   - the test command(s)
   - the top-level project structure
3. Create `AGENTS.md` with these sections:
   - **General** — a short list of working principles for agents
   - **Tech stack** — the key technologies
   - **Build and test** — the exact commands
   - **Structure** — a brief map of the important directories
4. Keep it concise. Prefer commands and facts that are verifiable from the repo
   over prose. Avoid detail that will need frequent updating.

## Acceptance criteria

- `AGENTS.md` exists at the repository root (unless it already existed).
- Build and test commands are accurate for this repository.
- The file is concise and maintainable.
