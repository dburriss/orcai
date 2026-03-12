# OrcAI

OrcAI is a CLI tool for coordinating bulk upgrade or migration work across multiple GitHub repositories.

## The Problem

When the same change needs to happen across many repositories — a dependency upgrade, a security fix, a policy rollout — the coordination overhead is significant. Creating issues, tracking progress, and assigning work across dozens of repos is tedious and error-prone when done manually.

## What It Does

OrcAI takes a declarative YAML job config and automates the GitHub planning layer:

- Creates a GitHub Project to track the coordinated work
- Creates matching issues in each target repository from a shared template
- Links all issues to the project
- Optionally assigns `@copilot` to each issue
- Writes a lock file for idempotency and state inspection
- Provides `info` and `cleanup` commands to inspect and teardown managed resources

It does **not** modify repository code, create branches, or open pull requests.

## Why

The goal is to reduce the overhead of large-scale coordination to a single command. A team can define a job once, run it against any number of repositories, re-run it safely (idempotent), and clean it up when done — all from one config file and one CLI.
