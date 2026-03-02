module Orca.Core.Tests.LockFileTests

open Xunit
open Orca.Core.LockFile

// ---------------------------------------------------------------------------
// Unit tests for LockFile — path derivation, read, and write.
// No I/O mocking required for path derivation tests.
// ---------------------------------------------------------------------------

[<Fact>]
let ``lockFilePath derives correct path from yaml path`` () =
    let result = lockFilePath "/projects/myjob.yml"
    Assert.Equal("/projects/myjob.lock.json", result)

[<Fact>]
let ``lockFilePath handles yaml file without directory`` () =
    let result = lockFilePath "job.yml"
    Assert.Equal("job.lock.json", result)
