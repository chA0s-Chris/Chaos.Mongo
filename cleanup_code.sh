#!/bin/bash
# Managed by repo-baseline (https://github.com/chA0s-Chris/repo-baseline). Do not edit; change it there.
set -euo pipefail

cd "$(git rev-parse --show-toplevel)"

CLEAN_ALL=0

for argument in "$@"; do
    case "${argument}" in
        --all)
            CLEAN_ALL=1
            ;;
        *)
            echo "Unknown argument '${argument}'. Usage: ./cleanup_code.sh [--all]" >&2
            exit 2
            ;;
    esac
done

# The solution is discovered instead of named, so the same script works in every repository.
shopt -s nullglob
SOLUTIONS=(*.slnx)
shopt -u nullglob

if [ "${#SOLUTIONS[@]}" -eq 0 ]; then
    echo "No .slnx solution found in $(pwd)." >&2
    exit 2
fi

if [ "${#SOLUTIONS[@]}" -gt 1 ]; then
    echo "Found several .slnx solutions in $(pwd): ${SOLUTIONS[*]}. Keep exactly one in the repository root." >&2
    exit 2
fi

SOLUTION="${SOLUTIONS[0]}"

if [ "${CLEAN_ALL}" -eq 1 ]; then
    dotnet jb cleanupcode --profile="Zorn" --verbosity=ERROR "${SOLUTION}"
    exit 0
fi

# Untracked files are included on purpose: new files are the ones most in need of the code style,
# and they are not staged yet at the point this script is normally run.
# Include rename destinations as well, since staged renames can also contain code changes.
# Razor files are left out on purpose: ReSharper's Razor cleanup does not apply the shared C# rules
# to them and would rewrite them against those rules (TOOL-1). Plain .xml files are left out as well,
# because they are often data such as test fixtures.
PATTERNS=$({ git diff --name-only --diff-filter=ACMR; git diff --name-only --cached --diff-filter=ACMR; git ls-files --others --exclude-standard; } | { grep '\.\(cs\|csproj\|json\|sh\|slnx\|config\|props\|targets\|nuspec\)$' | sort -u | sed 's|^|**/|' | paste -sd ';' || true; })

if [ -n "${PATTERNS}" ]; then
    dotnet jb cleanupcode --profile="Zorn" --verbosity=ERROR --include="${PATTERNS}" "${SOLUTION}"
else
    echo "No matching files to process."
fi
