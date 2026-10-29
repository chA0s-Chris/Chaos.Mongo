#!/usr/bin/env bash
# Waits until nuget.org serves every package of the version and fails unless each one has the exact version and the
# dispatched commit. nuget.org accepts a push before it validates and indexes the package, so only this check proves
# that the published packages belong to the commit that is about to be tagged.
#
# Environment: VERSION, GITHUB_SHA, optionally SRC_DIR, NUGET_FLATCONTAINER_URL, NUGET_POLL_ATTEMPTS (default 60) and
# NUGET_POLL_INTERVAL in seconds (default 30).
set -euo pipefail
# shellcheck source=lib.sh source-path=SCRIPTDIR
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

require_env VERSION GITHUB_SHA

attempts="${NUGET_POLL_ATTEMPTS:-60}"
interval="${NUGET_POLL_INTERVAL:-30}"

id_list="$(package_ids)"
mapfile -t pending <<<"$id_list"
nuspec="$(mktemp)"

for ((attempt = 1; ; attempt++)); do
    remaining=()

    for id in "${pending[@]}"; do
        status="$(fetch_nuspec "$id" "$VERSION" "$nuspec")" || status="error"

        if [[ $status == 200 ]]; then
            check_served_nuspec "$id" "$nuspec"
        else
            log "nuget.org does not serve $id $VERSION yet ($status)."
            remaining+=("$id")
        fi
    done

    pending=("${remaining[@]}")
    ((${#pending[@]} > 0)) || break
    ((attempt < attempts)) ||
        fail "nuget.org still does not serve ${pending[*]} $VERSION. Re-run the failed jobs to keep waiting."

    sleep "$interval"
done

rm -f "$nuspec"
