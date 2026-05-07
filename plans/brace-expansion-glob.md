# Plan: Brace Expansion in FileGlob

## Context

Users running glob patterns like `"jobs/**/*.yml"` cannot simultaneously match `.yaml` files without running the command twice. The fix is to support standard `{yml,yaml}` brace expansion syntax in `FileGlob`, so `"jobs/**/*.{yml,yaml}"` expands to two patterns internally and merges the results. The change is fully self-contained in `FileGlob.fs` — no CLI, Args, or command layer changes required.

---

## Files to change

| File | Change |
|---|---|
| `src/OrcAI.Core/FileGlob.fs` | Add brace expansion |
| `tests/OrcAI.Core.Tests/FileGlobTests.fs` | Add brace expansion tests |

---

## Implementation

### `src/OrcAI.Core/FileGlob.fs`

**1. Update `isGlobPattern` to detect `{`**

```fsharp
let private isGlobPattern (s: string) =
    s.Contains('*') || s.Contains('?') || s.Contains('[') || s.Contains('{')
```

**2. Add private `expandBraces` function**

Recursively expands the first `{a,b,...}` group found in the pattern, producing one string per alternative. Handles nested braces via recursion.

```fsharp
let private expandBraces (pattern: string) : string list =
    let openIdx = pattern.IndexOf('{')
    if openIdx < 0 then
        [ pattern ]
    else
        let closeIdx = pattern.IndexOf('}', openIdx)
        if closeIdx < 0 then
            [ pattern ]
        else
            let prefix       = pattern.[..openIdx - 1]
            let suffix       = pattern.[closeIdx + 1..]
            let alternatives = pattern.[openIdx + 1..closeIdx - 1].Split(',') |> Array.toList
            alternatives |> List.collect (fun alt -> expandBraces (prefix + alt + suffix))
```

**3. Update `expandWith` to expand braces before matching**

Run each expanded pattern through a separate `Matcher`, collect all results, deduplicate, sort.

```fsharp
let expandWith (dir: DirectoryInfoBase) (pattern: string) : Result<string list, string> =
    let patterns = expandBraces pattern
    let files =
        patterns
        |> List.collect (fun p ->
            let matcher = Matcher()
            matcher.AddInclude(p) |> ignore
            matcher.Execute(dir).Files
            |> Seq.map (fun f -> Path.GetFullPath(Path.Combine(dir.FullName, f.Path)))
            |> Seq.toList)
        |> List.distinct
        |> List.sort
    if files.IsEmpty then
        Error $"No files matched pattern: {pattern}"
    else
        Ok files
```

`expand` itself needs no changes — the `isGlobPattern` update routes brace patterns through `expandWith` automatically.

---

## Tests to add in `tests/OrcAI.Core.Tests/FileGlobTests.fs`

Add to the existing glob-pattern section:

```fsharp
[<Fact>]
let ``expand matches both extensions with brace expansion`` () =
    withTempDir ["a.yml"; "b.yaml"; "notes.txt"] (fun dir ->
        match expand dir "*.{yml,yaml}" with
        | Error e -> Assert.Fail(e)
        | Ok paths ->
            Assert.Equal(2, paths.Length)
            Assert.All(paths, fun p -> Assert.True(p.EndsWith(".yml") || p.EndsWith(".yaml"))))

[<Fact>]
let ``expand with brace expansion returns Error when nothing matches`` () =
    withTempDir ["notes.txt"] (fun dir ->
        match expand dir "*.{yml,yaml}" with
        | Ok _    -> Assert.Fail("Expected Error")
        | Error e -> Assert.Contains("No files matched", e))

[<Fact>]
let ``expand with brace expansion works in subdirectory patterns`` () =
    withTempDir ["configs/a.yml"; "configs/b.yaml"] (fun dir ->
        match expand dir "configs/*.{yml,yaml}" with
        | Error e -> Assert.Fail(e)
        | Ok paths -> Assert.Equal(2, paths.Length))
```

---

## Verification

```bash
# Run the test suite
dotnet test tests/OrcAI.Core.Tests

# Manual smoke test (both extensions resolve)
dotnet run --project src/OrcAI.Tool -- validate "tests/**/*.{yml,yaml}"
```
