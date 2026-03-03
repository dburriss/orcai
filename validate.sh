#!/bin/bash

# Wrapper script for the orca validation suite.
#
# Builds the CLI, then runs orca run, orca info, and orca cleanup against
# the fixture config in validation/ and reports pass/fail for each.
#
# Usage:
#   ./validate.sh

set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

echo "Building orca..."
dotnet build "$SCRIPT_DIR/src/Orca.Cli/Orca.Cli.fsproj" -c Debug --nologo -v quiet

export ORCA_BIN="$SCRIPT_DIR/src/Orca.Cli/bin/Debug/net10.0/orca"

dotnet fsi "$SCRIPT_DIR/validation/validate.fsx" "$@"
