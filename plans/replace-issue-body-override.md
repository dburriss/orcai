# Add `{repo}.replace.md` issue-body override

## Context

OrcAI already supports per-repo issue body customization via `{repo}.prepend.md` and `{repo}.append.md` files placed next to the `issue.template` file (see `docs/cli-reference.md` "Per-repo issue body overrides"). Sometimes a repo needs a completely different base body rather than the shared template. Add a `{repo}.replace.md` file convention: when present, its content is used in place of the base template for that repo. Prepend and append still apply around it if those files are also present — `replace` only swaps out the base template piece, it doesn't disable the other overrides. This keeps the three options orthogonal and composable, matching how prepend/append already compose with the base template.

## Implementation

All changes are in `src/OrcAI.Core/YamlConfig.fs`, following the existing prepend/append pattern exactly.

1. **`composeIssueBody`** (line 311-316): resolve the "body" piece as the `replace` override if present, otherwise the base template. Prepend/append wrap around whichever body is used.

   ```fsharp
   let private composeIssueBody (fs: IFileSystem) (templateDir: string) (shortRepo: string) (baseBody: string) : string =
       let body = readOverrideFile fs templateDir shortRepo "replace" |> Option.defaultValue baseBody
       [ readOverrideFile fs templateDir shortRepo "prepend"
         Some body
         readOverrideFile fs templateDir shortRepo "append" ]
       |> List.choose id
       |> String.concat "\n\n"
   ```

2. **`findOverrideFiles`** (line 388-396): add `.replace.md` to the suffix filter so `computeTemplateHash` detects changes to replace files (needed for the hash-based "auto issue-body updates" re-run detection).

   ```fsharp
   name.EndsWith(".prepend.md") || name.EndsWith(".append.md") || name.EndsWith(".replace.md"))
   ```

3. Update the doc comments above `composeIssueBody`, `applyIssueBodyOverrides`, and `findOverrideFiles`/`computeTemplateHash` to mention `.replace.md`.

No changes needed to `Domain.fs`, `RunCommand.fs`, or any CLI flags — this is purely file-presence-driven, same as prepend/append.

## Tests

Add to `tests/OrcAI.Core.Tests/YamlConfigTests.fs`, in the "Per-repo issue body overrides" section, mirroring existing prepend/append tests:

- `parseFile replaces base template content when {repo}.replace.md exists`
- `parseFile combines replace with prepend and append when all three exist`
- `computeTemplateHash changes when a repo replace file is added`

## Docs

Update `docs/cli-reference.md` lines 713-720 ("Per-repo issue body overrides") to document `{repo}.replace.md`:

- Add a bullet: `{repo}.replace.md` — replaces the base template content; `prepend`/`append` still wrap around it if also present.

Check `README.md` (line 17 area) and `CHANGELOG.md` for existing prepend/append mentions and add `replace` alongside them if a wording update is warranted there too.

## Verification

- `dotnet test tests/OrcAI.Core.Tests` — run the new and existing `YamlConfigTests` to confirm prepend/append/replace all behave as documented and hashing picks up replace-file changes.
