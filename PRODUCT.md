# Product: Orca CLI

## 1. Purpose

Provide a deterministic CLI workflow for coordinating bulk GitHub work across multiple repositories.

The system manages:

- Job configuration via YAML
- Bulk project creation across repositories
- Issue creation from shared templates
- GitHub Project membership
- Optional `@copilot` assignment
- Lock-file based state snapshots
- Visibility and cleanup workflows
- Authentication for local and CI usage

This product does not apply code changes inside repositories.
It orchestrates GitHub planning and execution metadata around upgrade or migration work.

---

## 2. Core Model

### Job

Declarative YAML definition of one coordinated unit of work.
Contains title, org, repositories, and issue settings.

### Organization

GitHub org or account that owns the project and target repositories.

### Repository Set

Explicit list of repositories included in a job.

### Issue Template

Markdown source used to create matching issues across repositories.

### Project

GitHub Project used to track the coordinated work created by a job.

### Issue

Repository-scoped execution record created from the shared template.

### Lock File

Persisted snapshot of managed state for idempotency, inspection, and fast re-runs.

### Auth Context

Credential source used for GitHub operations.
Supports PAT, GitHub App, `GH_TOKEN`, or ambient `gh` auth.

---

## 3. Job Lifecycle

1. Job config created.
2. Authentication resolved.
3. Target repositories loaded from YAML.
4. GitHub Project found or created.
5. Issues found or created per repository.
6. Issues added to the project.
7. `@copilot` assigned when enabled and needed.
8. Lock file written.
9. State inspected later via lock file or live fetch.
10. Managed resources removed through cleanup when the job is complete.

---

## 4. MVP Scope

MVP implements the operational loop:

- YAML job generation
- Bulk run workflow
- Idempotent project creation
- Idempotent issue creation
- Project item linking
- Optional Copilot assignment
- Lock file persistence
- State inspection
- Cleanup and teardown
- Local and CI authentication support

No direct GitHub SDK integration.
No repository code mutation.
No automatic branch or PR creation.
No cross-job dependency graph.

---

## 5. Success Criteria

- Works across one or many repositories.
- Re-running the same job is idempotent.
- Managed state can be reconstructed from lock data or live GitHub state.
- Authentication works consistently in local and CI contexts.
- Cleanup reverses managed project and issue artifacts safely.
- Operators can coordinate large upgrade efforts from one declarative config.
