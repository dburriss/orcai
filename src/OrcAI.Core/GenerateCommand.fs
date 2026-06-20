module OrcAI.Core.GenerateCommand

open System
open System.IO
open OrcAI.Core.Deps

// ---------------------------------------------------------------------------
// Generates a YAML job config file (and a stub issue template .md) from
// a name, org, and optional list of repo short-names.
// ---------------------------------------------------------------------------

type GenerateInput =
    { Name       : string
      Org        : string
      /// Short repo names (no owner prefix). Empty list emits a placeholder.
      Repos      : string list
      /// Resolved output path for the YAML file (e.g. "/cwd/my-job.yml").
      OutputPath : string
      Noop       : bool }

/// Convert a job name into a filesystem-friendly slug.
/// "Add AGENTS.md" -> "add-agents-md"
let slugify (name: string) : string =
    name.ToLowerInvariant()
    |> Seq.map (fun c -> if Char.IsLetterOrDigit(c) then c else '-')
    |> Seq.toArray
    |> String
    |> fun s ->
        // Collapse consecutive dashes and trim trailing ones
        let mutable prev = '-'
        s |> Seq.filter (fun c ->
            let keep = not (c = '-' && prev = '-')
            prev <- c
            keep)
        |> Seq.toArray
        |> String
    |> fun s -> s.Trim('-')

/// Build the YAML text for the job config.
let private buildYaml (name: string) (org: string) (repos: string list) (slug: string) (noop: bool) : string =
    let reposSection =
        if repos.IsEmpty then
            "  # TODO: add repo short-names (without the org/ prefix)\n  # - my-repo"
        else
            repos |> List.map (fun r -> $"  - \"{r}\"") |> String.concat "\n"

    let actionBlock =
        if noop then
            "action:\n  type: noop\n  # No assignment — remove this block to assign @copilot\n"
        else
            "# action:\n#   type: assign-copilot  # default; omit this block to assign @copilot\n#   comment: \"\"  # optional trigger comment\n"

    $"""job:
  title: "{name}"
  org: "{org}"

repos:
{reposSection}
  # TODO: add more repos if needed

issue:
  template: "./{slug}.md"
  labels: []
  # TODO: add label names, e.g. ["automated", "migration"]

{actionBlock}
# nudge:
#   mode: reassign       # reassign | comment-only | comment-and-reassign
#   comment: ""          # nudge comment body; supports {{assignee}} placeholder
"""

/// Build the stub Markdown issue template.
let private buildMarkdown (name: string) : string =
    $"""# {name}

<!-- TODO: describe the task for Copilot here -->
"""

/// Fetch org repos from GitHub (short names only).
/// Exposed so Program.fs can call it before the interactive TUI.
let listOrgRepos (deps: OrcAIDeps) (org: string) : Async<Result<string list, string>> =
    deps.GhClient.ListRepos(OrcAI.Core.Domain.OrgName org)

/// Execute the generate command.
/// All interactive input (name, org, repo selection) must be resolved
/// before calling this function — Program.fs handles the TUI.
let execute (deps: OrcAIDeps) (input: GenerateInput) : Result<string * string, string> =
    if String.IsNullOrWhiteSpace(input.Name) then
        Error "--name is required."
    elif String.IsNullOrWhiteSpace(input.Org) then
        Error "--org is required."
    else
        let slug     = slugify input.Name
        let yamlPath = input.OutputPath
        let mdPath   = Path.Combine(Path.GetDirectoryName(yamlPath) |> Option.ofObj |> Option.defaultValue ".", $"{slug}.md")

        let yaml = buildYaml input.Name input.Org input.Repos slug input.Noop
        let md   = buildMarkdown input.Name

        try
            deps.FileSystem.File.WriteAllText(yamlPath, yaml)
            // Only write the markdown stub if it doesn't already exist.
            if not (deps.FileSystem.File.Exists(mdPath)) then
                deps.FileSystem.File.WriteAllText(mdPath, md)
            Ok (yamlPath, mdPath)
        with ex ->
            Error $"Failed to write output files: {ex.Message}"
