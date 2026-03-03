module Orca.Auth.Tests.TestHelpers

open System

// ---------------------------------------------------------------------------
// Environment variable helpers shared across test modules.
// ---------------------------------------------------------------------------

/// Set an env var for the duration of `f`, then restore the original value.
let withEnv (name: string) (value: string option) (f: unit -> unit) =
    let original = Environment.GetEnvironmentVariable(name) |> Option.ofObj
    try
        match value with
        | Some v -> Environment.SetEnvironmentVariable(name, v)
        | None   -> Environment.SetEnvironmentVariable(name, null)
        f ()
    finally
        match original with
        | Some v -> Environment.SetEnvironmentVariable(name, v)
        | None   -> Environment.SetEnvironmentVariable(name, null)

/// Set multiple env vars for the duration of `f`, restoring all on exit.
let withEnvVars (pairs: (string * string option) list) (f: unit -> unit) =
    let originals =
        pairs |> List.map (fun (name, _) ->
            name, Environment.GetEnvironmentVariable(name) |> Option.ofObj)
    try
        for (name, value) in pairs do
            match value with
            | Some v -> Environment.SetEnvironmentVariable(name, v)
            | None   -> Environment.SetEnvironmentVariable(name, null)
        f ()
    finally
        for (name, original) in originals do
            match original with
            | Some v -> Environment.SetEnvironmentVariable(name, v)
            | None   -> Environment.SetEnvironmentVariable(name, null)
