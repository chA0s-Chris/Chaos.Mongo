#!/usr/bin/env bash
# Fails if the version already has a tag or a package on nuget.org that does not belong to the dispatched commit, so
# that a rerun can only complete the release of the same commit. Only reads.
#
# Environment: VERSION, GITHUB_SHA, optionally SRC_DIR and NUGET_FLATCONTAINER_URL.
set -euo pipefail
# shellcheck source=lib.sh source-path=SCRIPTDIR
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

require_env VERSION GITHUB_SHA

tag_state="$(remote_tag_state)"
log "Tag v$VERSION: $tag_state."

id_list="$(package_ids)"
mapfile -t ids <<<"$id_list"
nuspec="$(mktemp)"

for id in "${ids[@]}"; do
    status="$(fetch_nuspec "$id" "$VERSION" "$nuspec")" || fail "Could not query nuget.org for $id $VERSION."

    case "$status" in
        200) check_served_nuspec "$id" "$nuspec" ;;
        404) log "nuget.org does not serve $id $VERSION yet." ;;
        *) fail "nuget.org answered $status for $id $VERSION." ;;
    esac
done

rm -f "$nuspec"
