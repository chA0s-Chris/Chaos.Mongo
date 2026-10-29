#!/usr/bin/env bash
# Tags the dispatched commit and makes sure that a published GitHub release with every package exists. Safe to rerun:
# an existing tag is kept, a draft left by an interrupted run is completed, and only missing assets are uploaded.
# Uploaded assets are never replaced, so a new dispatch does not overwrite the originally published packages. Only an
# incomplete asset, which GitHub leaves behind when an upload fails upstream, is deleted and uploaded again.
#
# Run verify-published.sh first. Environment: VERSION, GITHUB_SHA, PACKAGE_DIR, NOTES_FILE, GH_TOKEN, GH_REPO.
set -euo pipefail
# shellcheck source=lib.sh source-path=SCRIPTDIR
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

require_env VERSION GITHUB_SHA PACKAGE_DIR NOTES_FILE

readonly bot_name='github-actions[bot]'
readonly bot_email='github-actions[bot]@users.noreply.github.com'

tag="v$VERSION"

if is_prerelease; then
    flags=(--prerelease --latest=false)
else
    flags=(--prerelease=false --latest)
fi

shopt -s nullglob
assets=("$PACKAGE_DIR"/*.nupkg "$PACKAGE_DIR"/*.snupkg)
shopt -u nullglob

((${#assets[@]} > 0)) || fail "No packages found in $PACKAGE_DIR."
[[ -f $NOTES_FILE ]] || fail "The release notes file $NOTES_FILE does not exist."

tag_state="$(remote_tag_state)"

if [[ $tag_state == absent ]]; then
    git -c user.name="$bot_name" -c user.email="$bot_email" tag -a "$tag" -m "Release $tag" "$GITHUB_SHA"
    git push origin "refs/tags/$tag"
else
    log "The tag $tag already exists at $GITHUB_SHA."
fi

status=0
is_draft="$(release_query "$tag" isDraft .isDraft)" || status=$?

if ((status == RELEASE_NOT_FOUND)); then
    gh release create "$tag" --verify-tag --title "$tag" --notes-file "$NOTES_FILE" "${flags[@]}"
    is_draft=false
elif ((status != 0)); then
    exit "$status"
fi

# Only the state "uploaded" marks a complete asset. An upload that fails upstream can leave an empty asset in another
# state, which GitHub documents as safe to delete.
existing_assets="$(release_query "$tag" assets '.assets[] | [.name, .state] | @tsv')"
declare -A asset_states=()
while IFS=$'\t' read -r name state; do
    [[ -z $name ]] || asset_states[$name]="$state"
done <<<"$existing_assets"

for asset in "${assets[@]}"; do
    name="$(basename "$asset")"

    if [[ -z ${asset_states[$name]+set} ]]; then
        gh release upload "$tag" "$asset"
    elif [[ ${asset_states[$name]} == uploaded ]]; then
        log "The release $tag already has $name."
    else
        log "The release $tag has the incomplete asset $name in state '${asset_states[$name]}'. Uploading it again."
        gh release delete-asset "$tag" "$name" --yes
        gh release upload "$tag" "$asset"
    fi
done

if [[ $is_draft == true ]]; then
    gh release edit "$tag" --draft=false "${flags[@]}"
fi
