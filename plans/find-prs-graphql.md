# Plan: Switch FindPrsForIssueImpl to GraphQL

## Why

`FindPrsForIssueImpl` currently calls `gh pr list --repo ... --state all` and filters
in memory for PRs whose `closingIssuesReferences` contains the target issue number.
This approach has a hard cap: `gh pr list` defaults to 30 results, and even with an
explicit `--limit` the approach is wasteful — it fetches every PR in the repo to find
the one or two linked to a single issue.

A repo with hundreds of PRs (common on active projects) will silently miss linked PRs
beyond the limit, causing `orcai` to fail to detect existing work and create duplicate
issues or leave stale PRs unclosed.

The correct fix is to invert the query: ask GitHub which PRs close a given issue,
rather than asking for all PRs and filtering. GitHub's GraphQL API exposes exactly
this via `Issue.closingPullRequests`, which returns only the PRs linked to that issue —
no pagination problem, no wasted data transfer.

## Approach

Replace the `gh pr list` call in `FindPrsForIssueImpl` with a `gh api graphql` query
that queries from the issue side:

```graphql
{
  repository(owner: "OWNER", name: "REPO") {
    issue(number: ISSUE_NUMBER) {
      closingPullRequests(first: 25) {
        nodes {
          number
          url
        }
      }
    }
  }
}
```

`first: 25` is generous — no real issue has 25 linked PRs. The result set is always
small regardless of total PR count on the repo.

### Implementation steps

1. **Split `RepoName` into owner/repo parts** — `RepoName` is stored as `"owner/repo"`.
   Split on `'/'` to extract the two parts needed for the GraphQL `repository()` call.

2. **Build and run the GraphQL query** via `runGh`:
   ```fsharp
   let parts = repoStr.Split('/', 2)
   let owner, repo = parts.[0], parts.[1]
   let query =
       $"""{{ repository(owner: \\"{owner}\\", name: \\"{repo}\\") {{ issue(number: {issueN}) {{ closingPullRequests(first: 25) {{ nodes {{ number url }} }} }} }} }}"""
   match! runGh ghToken $"api graphql -f query='{query}'" with
   ```

3. **Parse the response** — the shape is:
   ```json
   { "data": { "repository": { "issue": { "closingPullRequests": { "nodes": [...] } } } } }
   ```
   Navigate `data -> repository -> issue -> closingPullRequests -> nodes` and map each
   node to a `PullRequestRef`.

4. **Handle null issue** — if the issue number doesn't exist, `repository.issue` will
   be `null`. Return `[]` in that case rather than erroring.

## Critical Files

- `src/OrcAI.GitHub/GhClient.fs` — only file that changes; rewrite `FindPrsForIssueImpl`

## Verification

1. `dotnet build` — confirm compilation
2. `dotnet test` — confirm no regressions
3. Manually run against a repo that has a PR linked to an issue and confirm the PR is
   found correctly
4. Confirm that a repo with > 30 PRs total but only one linked PR still returns that PR
