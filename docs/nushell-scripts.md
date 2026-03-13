# Nushell Scripts

This repository also contains Nushell scripts for managing GitHub projects and issues in bulk. These are the original scripted predecessors to the `orcai` CLI.

## Prerequisites

- **Nushell**: Install from [nushell.sh](https://www.nushell.sh/)
- **GitHub CLI**: Install from [cli.github.com](https://cli.github.com/)

## Authentication

### Local environment

Run `gh auth login` to authenticate with the GitHub CLI.

### CI environment

Set the `GH_TOKEN` environment variable with a GitHub Personal Access Token that has the necessary permissions (project and issue management).

## Scripts

### orca.nu

Creates a GitHub project and adds issues to it across multiple repositories.

**Usage:**

```bash
./orca.nu [--verbose] <yaml_file>
```

**Options:**

- `--verbose`: Enable verbose output
- `yaml_file`: Path to YAML configuration file

**YAML configuration structure:**

```yaml
job:
  title: "Project Title"
  org: "organization-name"
repos:
  - "repo1"
  - "repo2"
issue:
  template: "path/to/issue-template.md"
  labels:
    - "label1"
    - "label2"
```

### cleanup.nu

Deletes a GitHub project and cleans up associated issues and PRs.

**Usage:**

```bash
./cleanup.nu [--dryrun] <yaml_file>
```

**Options:**

- `--dryrun`: Preview what would be deleted without actually deleting
- `yaml_file`: Path to YAML configuration file

**YAML configuration structure:**

```yaml
job:
  title: "Project Title"
  org: "organization-name"
```
