# Notes

- [x] flag to create labels if they don't exist
- [x] review codebase for duplication and refactor into shared utilities
- [x] refactor for testability and add unit tests using pure functions (calculations vs actions with side effects)
- [x] add validation scripts to check the commands are working as expected (https://github.com/dburriss/orca-tests)
- [x] add a `auth create-app` command to use a manifest file to create a GitHub App and store credentials (https://docs.github.com/en/apps/sharing-github-apps/registering-a-github-app-from-a-manifest)
- [x] auto create labels if they don't exist when adding to issues if `--auto-create-labels` is used
- [ ] allow disabling assigning copilot to issues
- [ ] add a `--skip-lock` flag to `info` to bypass the lock file and fetch live state from GitHub
- [ ] add a `--save-lock` flag to `info` to persist a new lock file
- [ ] add a `generate` command to create a YAML config from a list of repos or orgs
- [ ] `info` command run in parallel for each repo for speed