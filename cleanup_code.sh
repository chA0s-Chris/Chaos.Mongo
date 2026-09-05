#!/bin/bash
#
#
set -euo pipefail

# Untracked files are included on purpose: new files are the ones most in need of the code style,
# and they are not staged yet at the point this script is normally run.
PATTERNS=$({ git diff --name-only --diff-filter=ACM; git diff --name-only --cached --diff-filter=ACM; git ls-files --others --exclude-standard; } | { grep '\.\(cs\|csproj\|json\|sh\|slnx\|config\)$' | sort -u | sed 's|^|**/|' | paste -sd ';' || true; })

if [ -n "${PATTERNS}" ]; then
    dotnet jb cleanupcode --profile="Zorn" --verbosity=ERROR --include="${PATTERNS}" Chaos.Mongo.slnx
else
    echo "No matching files to process."
fi
