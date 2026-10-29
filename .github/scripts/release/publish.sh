#!/usr/bin/env bash
# Pushes the packages, then the symbol packages, to nuget.org. Safe to rerun.
#
# The NuGet client only pushes a neighboring symbol package when it actually pushed the package, so after a retry that
# skips the packages as duplicates, it would never push their symbols. The symbol packages are therefore pushed in a
# separate pass. There, --skip-duplicate makes the client count a symbol package that is already on nuget.org as done;
# every other failure fails the job. The client's output is not inspected, because it also names the package file, and
# a version such as 1.2.409 would make any error look like a duplicate.
#
# Environment: NUGET_KEY, PACKAGE_DIR, optionally NUGET_SOURCE.
set -euo pipefail
# shellcheck source=lib.sh source-path=SCRIPTDIR
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

require_env NUGET_KEY PACKAGE_DIR

source_url="${NUGET_SOURCE:-https://api.nuget.org/v3/index.json}"

shopt -s nullglob
packages=("$PACKAGE_DIR"/*.nupkg)
symbol_packages=("$PACKAGE_DIR"/*.snupkg)
shopt -u nullglob

((${#packages[@]} > 0)) || fail "No packages found in $PACKAGE_DIR."

for package in "${packages[@]}"; do
    dotnet nuget push "$package" --api-key "$NUGET_KEY" --source "$source_url" --no-symbols --skip-duplicate
done

for symbol_package in "${symbol_packages[@]}"; do
    dotnet nuget push "$symbol_package" --api-key "$NUGET_KEY" --source "$source_url" --skip-duplicate ||
        fail "Pushing $(basename "$symbol_package") failed."
done
