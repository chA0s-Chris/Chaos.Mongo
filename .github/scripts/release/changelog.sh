#!/usr/bin/env bash
# Opens a pull request that adds the release notes to CHANGELOG.md. Does nothing for prereleases or if main already
# contains the entry. Safe to rerun: an existing changelog branch or pull request is kept.
#
# Environment: VERSION, NOTES_FILE, GH_TOKEN, GH_REPO.
set -euo pipefail
# shellcheck source=lib.sh source-path=SCRIPTDIR
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

require_env VERSION NOTES_FILE

readonly bot_name='github-actions[bot]'
readonly bot_email='github-actions[bot]@users.noreply.github.com'

if is_prerelease; then
    log "Prereleases do not update CHANGELOG.md."
    exit 0
fi

tag="v$VERSION"
branch="release/changelog-$tag"

[[ -f $NOTES_FILE ]] || fail "The release notes file $NOTES_FILE does not exist."

git fetch --no-tags origin '+refs/heads/main:refs/remotes/origin/main'
main_changelog="$(git show origin/main:CHANGELOG.md)"

if awk -v heading="## $tag " 'index($0, heading) == 1 { found = 1 } END { exit !found }' <<<"$main_changelog"; then
    log "CHANGELOG.md on main already contains $tag."
    exit 0
fi

if [[ -n "$(git ls-remote --heads origin "refs/heads/$branch")" ]]; then
    log "The branch $branch already exists."
else
    git switch --no-track --create "$branch" origin/main

    header="$(mktemp)"
    changes="$(mktemp)"

    # Keep the header lines until the first '## ' heading.
    sed -n '/^## /q; p' CHANGELOG.md >"$header"
    sed -n '/^## /,$p' CHANGELOG.md >"$changes"

    {
        cat "$header"
        printf '\n'
        printf '## %s (%s)\n' "$tag" "$(date +%Y-%m-%d)"
        printf '\n'
        cat "$NOTES_FILE"
        printf '\n'
        cat "$changes"
    } >CHANGELOG.md

    rm -f "$header" "$changes"

    git add CHANGELOG.md
    git -c user.name="$bot_name" -c user.email="$bot_email" commit -m "docs: update CHANGELOG for $VERSION [skip ci]"
    git push origin "HEAD:refs/heads/$branch"
fi

pull_requests="$(gh pr list --head "$branch" --state all --json number --jq length)"

if ((pull_requests > 0)); then
    log "A pull request for $branch already exists."
else
    gh pr create --base main --head "$branch" \
        --title "docs: update CHANGELOG.md for $tag" \
        --body "Automatic CHANGELOG.md update for $tag."
fi
