# BACKLOG

- [x] flag to create labels if they don't exist
- [x] review codebase for duplication and refactor into shared utilities
- [x] refactor for testability and add unit tests using pure functions (calculations vs actions with side effects)
- [x] add validation scripts to check the commands are working as expected (https://github.com/dburriss/orca-tests)
- [x] add a `auth create-app` command to use a manifest file to create a GitHub App and store credentials (https://docs.github.com/en/apps/sharing-github-apps/registering-a-github-app-from-a-manifest)
- [x] auto create labels if they don't exist when adding to issues if `--auto-create-labels` is used
- [x] allow disabling assigning copilot to issues
- [x] `info` command run in parallel for each repo for speed
- [x] add a `--skip-lock` flag to `info` to bypass the lock file and fetch live state from GitHub
- [x] add a `--save-lock` flag to `info` to persist a new lock file
- [x] add a `generate` command to create a YAML config from a list of repos or orgs
- [x] fix nullibility warnings on info command
- [x] update `run` command to use the lock file and add a `--skip-lock` flag to bypass the lock file and fetch live state from GitHub. Report on actual changes vs. already existing state.
- [x] add `--json` flag to `info` to emit machine readable output
- [x] add `json` flag to `cleanup` to emit list of cleaned up resources. If `--dryrun` is also set, emit list of resources that would be cleaned up. Indicate with a boolean whether it is a dry run or not.
- [x] add `json` to `run` output to emit list of created resources
- [x] add a `--force` flag to `cleanup` to skip confirmation prompt
