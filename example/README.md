# Examples

This folder contains example migration specifications and their corresponding execution configurations.

> Note: These examples are complementary and are meant to be run in the sequence highlighted below.

## Available Examples

- **dotnet-multi-version-setup** - Sets up multi-version .NET development environment so Copilot can run `build` and `test` commands
- **add-agents-md** - Adds an AGENTS.md file to instruct agents on how to build and run the project
- **dotnet-upgrade-to-10** - Upgrades .NET projects to version 10
- **local-provider-demo** - Demonstrates `provider: local`, tracking project/issue state as files under `.orcai-local/` instead of GitHub
- **opencode-cmd-to-pr/** - Runs OpenCode against many org repos via a `cmd-to-pr` action and opens a PR in each (checkout → run agent → commit → push → PR). See its own [README](opencode-cmd-to-pr/README.md).

Each example includes:
- `.md` file - The migration specification describing what changes to make
- `.yml` file - The execution configuration for running the migration

## Recommended Workflow

1. **Ensure your agent can run correctly in the environment** - Verify all prerequisites and dependencies are available
2. **Add an AGENTS.md file** - Create instructions for the agent on how to build, test, and run the project
3. **Run the migration** - Execute the migration within a working environment so the agent can catch and fix issues automatically by running builds, tests, linters, synthesis, etc. before the PR is finalised

This order ensures the agent has proper instructions and can validate changes during the migration process.
