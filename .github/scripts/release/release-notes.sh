#!/usr/bin/env bash
# Writes the release notes body (body.md) and the ReleaseNotes.md that Nuke Pack reads to NOTES_DIR.
#
# If the release v$VERSION is already published, its body is reused: release-drafter would compare against that
# release and report no changes. Otherwise the body drafted by release-drafter is used.
#
# Environment: VERSION, NOTES_DIR, DRAFTED_BODY, GH_TOKEN, GH_REPO.
set -euo pipefail
# shellcheck source=lib.sh source-path=SCRIPTDIR
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

require_env VERSION NOTES_DIR

tag="v$VERSION"
status=0
is_draft="$(release_query "$tag" isDraft .isDraft)" || status=$?

if ((status == 0)) && [[ $is_draft == false ]]; then
    log "Reusing the notes of the published release $tag."
    body="$(release_query "$tag" body .body)"
elif ((status == 0 || status == RELEASE_NOT_FOUND)); then
    body="${DRAFTED_BODY-}"
    [[ -n $body ]] || fail "release-drafter produced no release notes."
else
    exit "$status"
fi

mkdir -p "$NOTES_DIR"
printf '%s\n' "$body" >"$NOTES_DIR/body.md"

{
    printf '# %s (%s)\n' "$tag" "$(date +%Y-%m-%d)"
    printf '\n'
    printf '%s\n' "$body"
} >"$NOTES_DIR/ReleaseNotes.md"
