module OrcAI.Core.Codeowners

open System.IO.Abstractions

/// Return the owners on the catch-all `*` line, or None if absent.
let parseCatchAll (content: string) : string option =
    content.Split('\n')
    |> Array.tryPick (fun line ->
        let t = line.Trim()
        if t.StartsWith("#") || t = "" then None
        elif t.StartsWith("* ") || t.StartsWith("*\t") then
            let owners = t.Substring(1).Trim()
            if owners = "" then None else Some owners
        else None)

/// Try to find and parse a CODEOWNERS file from the local filesystem.
/// Checks CODEOWNERS, .github/CODEOWNERS, docs/CODEOWNERS in order.
/// Returns the catch-all owners string, or None if not found or no `*` rule.
let tryReadLocal (fs: IFileSystem) (dir: string) : string option =
    [ "CODEOWNERS"; ".github/CODEOWNERS"; "docs/CODEOWNERS" ]
    |> List.tryPick (fun rel ->
        let path = fs.Path.Combine(dir, rel)
        if fs.File.Exists(path) then Some (fs.File.ReadAllText(path))
        else None)
    |> Option.bind parseCatchAll
