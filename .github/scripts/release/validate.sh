#!/usr/bin/env bash
# Fails unless the release runs from main with a valid version. Reads nothing remote and writes no state.
#
# Environment: VERSION, GITHUB_REF, GITHUB_OUTPUT.
set -euo pipefail
# shellcheck source=lib.sh source-path=SCRIPTDIR
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

require_env GITHUB_OUTPUT

[[ ${GITHUB_REF:-} == refs/heads/main ]] ||
    fail "Releases only run from refs/heads/main, not from $(printf '%q' "${GITHUB_REF:-}")."

is_valid_version "${VERSION-}" ||
    fail "The version $(printf '%q' "${VERSION-}") is not a SemVer 2.0.0 version without build metadata and leading v."

# Git rejects some valid SemVer versions as tag names, such as 1.2.3-rc.lock, because a ref cannot end in .lock. Such a
# version would be published to nuget.org before tagging fails, and no rerun could complete the release.
git check-ref-format "refs/tags/v$VERSION" ||
    fail "Git does not accept v$VERSION as a tag name. A tag cannot end in .lock."

prerelease=false
if is_prerelease; then
    prerelease=true
fi

printf 'prerelease=%s\n' "$prerelease" >>"$GITHUB_OUTPUT"
log "Releasing v$VERSION (prerelease: $prerelease)."
