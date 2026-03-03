// =============================================================================
// Shared helpers for orca validation scripts.
// =============================================================================

open System
open System.Diagnostics

// ---------------------------------------------------------------------------
// Process runner
// ---------------------------------------------------------------------------

type RunResult =
    { ExitCode : int
      Stdout   : string
      Stderr   : string }

/// Run a command with the given arguments and return its output.
let runCmd (bin: string) (args: string) : RunResult =
    let psi = ProcessStartInfo(bin, Arguments = args)
    psi.RedirectStandardOutput <- true
    psi.RedirectStandardError  <- true
    psi.UseShellExecute        <- false
    let p = Process.Start(psi)
    let stdout = p.StandardOutput.ReadToEnd()
    let stderr = p.StandardError.ReadToEnd()
    p.WaitForExit()
    { ExitCode = p.ExitCode; Stdout = stdout; Stderr = stderr }

// ---------------------------------------------------------------------------
// Assertions
// ---------------------------------------------------------------------------

let private pass (label: string) =
    printfn "  [PASS] %s" label

let private fail (label: string) (detail: string) =
    printfn "  [FAIL] %s" label
    printfn "         %s" detail
    failwithf "Assertion failed: %s — %s" label detail

/// Assert that the exit code matches the expected value.
let assertExitCode (expected: int) (label: string) (result: RunResult) =
    if result.ExitCode = expected then
        pass label
    else
        fail label (sprintf "expected exit code %d but got %d\nstdout: %s\nstderr: %s"
                        expected result.ExitCode result.Stdout result.Stderr)

/// Assert that stdout contains the given substring (case-insensitive).
let assertStdoutContains (substring: string) (label: string) (result: RunResult) =
    if result.Stdout.IndexOf(substring, StringComparison.OrdinalIgnoreCase) >= 0 then
        pass label
    else
        fail label (sprintf "expected stdout to contain '%s'\nstdout: %s\nstderr: %s"
                        substring result.Stdout result.Stderr)

/// Assert that a file exists at the given path.
let assertFileExists (path: string) (label: string) =
    if IO.File.Exists(path) then
        pass label
    else
        fail label (sprintf "expected file to exist: %s" path)

// ---------------------------------------------------------------------------
// Output helpers
// ---------------------------------------------------------------------------

/// Print a section header.
let section (title: string) =
    printfn ""
    printfn "--- %s ---" title

/// Print the full result for debugging.
let printResult (result: RunResult) =
    if result.Stdout.Trim().Length > 0 then
        printfn "  stdout: %s" (result.Stdout.Trim())
    if result.Stderr.Trim().Length > 0 then
        printfn "  stderr: %s" (result.Stderr.Trim())
