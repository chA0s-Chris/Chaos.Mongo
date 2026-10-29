# Shared helpers of the release scripts. Source this file; it runs nothing by itself.
#
# Every value that comes from outside the repository (workflow inputs, release notes, API responses) reaches the
# scripts through environment variables or files and is only ever expanded in double quotes.

# SemVer 2.0.0 without build metadata. nuget.org ignores build metadata for package identity, so 1.0.0+a and 1.0.0+b
# would collide. Without it, "contains -" is a correct prerelease test.
readonly RELEASE_VERSION_PATTERN='^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-((0|[1-9][0-9]*|[0-9]*[a-zA-Z-][0-9a-zA-Z-]*)(\.(0|[1-9][0-9]*|[0-9]*[a-zA-Z-][0-9a-zA-Z-]*))*))?$'

# The exit status of release_query for a release that does not exist.
readonly RELEASE_NOT_FOUND=10

log() {
    printf '%s\n' "$*" >&2
}

fail() {
    printf 'Error: %s\n' "$*" >&2
    exit 1
}

require_env() {
    local name
    for name in "$@"; do
        [[ -n ${!name:-} ]] || fail "The environment variable $name is not set."
    done
}

# Matches the entire value, so a multi-line value whose first line is valid is rejected.
is_valid_version() {
    [[ $1 =~ $RELEASE_VERSION_PATTERN ]]
}

is_prerelease() {
    # shellcheck disable=SC2153 # VERSION comes from the environment.
    [[ $VERSION == *-* ]]
}

# Prints the sorted package IDs of every project that the Nuke Pack target packs: every project under src/.
package_ids() {
    local src_dir="${SRC_DIR:-src}" project id
    local -a projects=() ids=()

    mapfile -t projects < <(find "$src_dir" -name '*.csproj' -type f)
    ((${#projects[@]} > 0)) || fail "No projects found under $src_dir."

    for project in "${projects[@]}"; do
        id="$(sed -n 's:.*<PackageId>\([^<]*\)</PackageId>.*:\1:p' "$project" | head -n 1)"
        [[ -n $id ]] || id="$(basename "$project" .csproj)"
        ids+=("$id")
    done

    printf '%s\n' "${ids[@]}" | LC_ALL=C sort
}

# Downloads the nuspec that nuget.org serves for package $1 in version $2 to file $3 and prints the HTTP status.
# A 404 only means that the read API does not serve the version (yet).
fetch_nuspec() {
    local id="${1,,}" version="${2,,}" target="$3"
    local url="${NUGET_FLATCONTAINER_URL:-https://api.nuget.org/v3-flatcontainer}/$id/$version/$id.nuspec"

    curl --silent --show-error --location --retry 3 --output "$target" --write-out '%{http_code}' "$url"
}

nuspec_version() {
    sed -n 's:.*<version>\([^<]*\)</version>.*:\1:p' "$1" | head -n 1
}

nuspec_commit() {
    sed -n 's:.*<repository [^>]*commit="\([^"]*\)".*:\1:p' "$1" | head -n 1
}

# Fails unless the served nuspec $2 of package $1 has exactly the requested version and the dispatched commit.
check_served_nuspec() {
    local id="$1" nuspec="$2" served_version served_commit

    served_version="$(nuspec_version "$nuspec")"
    served_commit="$(nuspec_commit "$nuspec")"

    [[ $served_version == "$VERSION" ]] ||
        fail "nuget.org serves $id as version '$served_version' instead of '$VERSION'."
    [[ $served_commit == "$GITHUB_SHA" ]] ||
        fail "nuget.org serves $id $VERSION from commit '$served_commit' instead of '$GITHUB_SHA'."

    log "nuget.org serves $id $VERSION from commit $GITHUB_SHA."
}

# Prints "present" if the tag v$VERSION exists on origin and peels to $GITHUB_SHA, or "absent" if no tag matches.
# Fails if a tag differs only in case, because NuGet versions are case-insensitive, or points to another commit.
remote_tag_state() {
    local tag="v$VERSION" refs sha ref name direct="" peeled="" commit

    refs="$(git ls-remote --tags origin)" || fail "Could not list the tags of origin."

    while IFS=$'\t' read -r sha ref; do
        [[ -n $ref ]] || continue
        name="${ref#refs/tags/}"

        if [[ $name == "$tag" ]]; then
            direct="$sha"
        elif [[ $name == "$tag^{}" ]]; then
            peeled="$sha"
        else
            name="${name%'^{}'}"
            [[ ${name,,} != "${tag,,}" ]] || fail "The tag '$name' differs from '$tag' only in case."
        fi
    done <<<"$refs"

    if [[ -z $direct ]]; then
        printf 'absent\n'
        return
    fi

    commit="${peeled:-$direct}"
    [[ $commit == "$GITHUB_SHA" ]] ||
        fail "The tag '$tag' points to commit '$commit' instead of '$GITHUB_SHA'."

    printf 'present\n'
}

# Prints field $2 of the GitHub release with tag $1, extracted with the jq expression $3. Returns RELEASE_NOT_FOUND if
# the release does not exist and fails on any other error. gh also finds drafts if the token may see them.
release_query() {
    local tag="$1" field="$2" expression="$3" output errors status=0

    errors="$(mktemp)"
    output="$(gh release view "$tag" --json "$field" --jq "$expression" 2>"$errors")" || status=$?

    if ((status != 0)); then
        if grep -qi 'release not found' "$errors"; then
            rm -f "$errors"
            return "$RELEASE_NOT_FOUND"
        fi

        cat "$errors" >&2
        rm -f "$errors"
        fail "Could not read the release '$tag'."
    fi

    rm -f "$errors"
    printf '%s\n' "$output"
}
