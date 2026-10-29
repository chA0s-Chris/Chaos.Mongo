#!/usr/bin/env bash
# Tests the release scripts against the doubles in doubles/, which take the place of git, gh, curl and dotnet.
#
# Usage: bash .github/scripts/release/tests/run.sh [test-name...]
#
# Each test runs in its own process with a temporary bare repository as origin, a fake nuget.org feed and fake GitHub
# state. The git double runs the real git against these local repositories, the other doubles replay the fake state.
# Every double records its calls, so the tests assert which writes happen and that no destructive write happens.
set -euo pipefail

TESTS_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SCRIPTS_DIR="$(dirname "$TESTS_DIR")"
SELF="$TESTS_DIR/$(basename "${BASH_SOURCE[0]}")"
readonly TESTS_DIR SCRIPTS_DIR SELF

readonly PACKAGE_IDS=(Chaos.Mongo Chaos.Mongo.EventStore Chaos.Mongo.Outbox)
readonly OTHER_COMMIT=0123456789abcdef0123456789abcdef01234567

# Matches every call that changes remote or repository state.
readonly WRITE_CALLS='^(git( .*)? (push|tag)( |$)|gh (release|pr) (create|upload|edit|delete|delete-asset|close|merge)( |$)|dotnet nuget (push|delete)( |$))'
# Matches calls that would overwrite or delete published state. Deleting an incomplete release asset is allowed; the gh
# double records the deletion of a complete one, which run_one rejects.
readonly DESTRUCTIVE_CALLS='^(git( .*)? push( .*)? (--force|--force-with-lease|-f|--delete|-d|--mirror|\+[^ ]*)( |$)|gh( .*)? (--clobber|delete)( |$)|dotnet nuget delete)'

# A release notes body with characters that a shell would interpret. Set per test, because it names the test directory.
DRAFTED_BODY=""
TEMP_DIRS=()

# --- Test environment -------------------------------------------------------------------------------------------------

git_real() {
    "$REAL_GIT" "$@"
}

commit_all() {
    git_real add --all
    git_real -c user.name=Test -c user.email=test@example.invalid commit --quiet --message "$1"
}

setup() {
    T="$(mktemp -d)"
    TEMP_DIRS+=("$T")
    trap 'rm -rf "${TEMP_DIRS[@]}"' EXIT

    mkdir -p "$T/bin" "$T/state"
    cp "$TESTS_DIR"/doubles/* "$T/bin/"
    chmod +x "$T/bin"/*

    export PATH="$T/bin:$PATH"
    export DOUBLE_STATE="$T/state" DOUBLE_CALLS="$T/calls" DOUBLE_ORIGIN="$T/origin.git"
    : >"$DOUBLE_CALLS"

    git_real init --quiet --bare --initial-branch=main "$DOUBLE_ORIGIN"
    git_real init --quiet --initial-branch=main "$T/source"
    cd "$T/source"
    git_real remote add origin "$DOUBLE_ORIGIN"

    mkdir -p src/Chaos.Mongo src/Chaos.Mongo.EventStore src/Chaos.Mongo.Outbox
    printf '<Project Sdk="Microsoft.NET.Sdk">\n  <PropertyGroup>\n    <PackageId>Chaos.Mongo</PackageId>\n  </PropertyGroup>\n</Project>\n' \
        >src/Chaos.Mongo/Chaos.Mongo.csproj
    printf '\xef\xbb\xbf<Project Sdk="Microsoft.NET.Sdk">\n  <PropertyGroup>\n    <PackageId>Chaos.Mongo.EventStore</PackageId>\n  </PropertyGroup>\n</Project>\n' \
        >src/Chaos.Mongo.EventStore/Chaos.Mongo.EventStore.csproj
    # Without PackageId, the package ID is the project name.
    printf '<Project Sdk="Microsoft.NET.Sdk">\n</Project>\n' >src/Chaos.Mongo.Outbox/Chaos.Mongo.Outbox.csproj
    printf '# Changelog\nAll notable changes to this project will be documented in this file.\n\n\n## v1.2.2 (2026-01-01)\n\n### Features\n\n- Older change (#1) by @someone\n' \
        >CHANGELOG.md
    commit_all "Previous commit"
    PREVIOUS_COMMIT="$(git_real rev-parse HEAD)"

    printf 'release\n' >release.txt
    commit_all "Release commit"
    git_real push --quiet origin main

    GITHUB_SHA="$(git_real rev-parse HEAD)"
    export GITHUB_SHA
    export GITHUB_REF=refs/heads/main
    export GITHUB_OUTPUT="$T/github-output"
    export VERSION=1.2.3
    export GH_REPO=chA0s-Chris/Chaos.Mongo
    export NOTES_DIR="$T/artifacts/release-notes"
    export NOTES_FILE="$NOTES_DIR/body.md"
    export PACKAGE_DIR="$T/artifacts/packages"
    export NUGET_KEY=short-lived-key
    export NUGET_POLL_ATTEMPTS=3
    export NUGET_POLL_INTERVAL=0

    # shellcheck disable=SC2089,SC2090 # The quotes and backslashes are meant to be literal.
    DRAFTED_BODY="### Features

- Quote \"double\" and 'single' (#2) by @a
- Backtick \`touch $T/pwned-backtick\` (#3) by @b
- Substitution \$(touch $T/pwned-substitution) and \${HOME} (#4) by @c
- Percent 100% %s %n and backslash \\n (#5) by @d"
    # shellcheck disable=SC2090
    export DRAFTED_BODY
}

# Checks out the dispatched commit in a fresh clone, like actions/checkout does for every job.
checkout() {
    cd "$T"
    rm -rf "$T/job"
    git_real clone --quiet "$DOUBLE_ORIGIN" "$T/job"
    cd "$T/job"
    git_real -c advice.detachedHead=false checkout --quiet "$GITHUB_SHA"
}

# Pushes a tag to origin without recording the call. $3 is "annotated" or "lightweight".
seed_tag() {
    local name="$1" commit="$2" kind="$3"
    cd "$T/source"
    if [[ $kind == annotated ]]; then
        git_real -c user.name=Test -c user.email=test@example.invalid tag --annotate "$name" --message "$name" "$commit"
    else
        git_real tag "$name" "$commit"
    fi
    git_real push --quiet origin "refs/tags/$name"
}

# Makes the fake feed serve package $1 in version $2, with nuspec version $3 and commit $4.
seed_package() {
    local directory="$DOUBLE_STATE/nuget/${1,,}/${2,,}"
    mkdir -p "$directory"
    printf '<package><metadata><id>%s</id><version>%s</version><repository type="git" url="https://github.com/chA0s-Chris/Chaos.Mongo" commit="%s" /></metadata></package>\n' \
        "$1" "$3" "$4" >"$directory/nuspec"
}

seed_all_packages() {
    local id
    for id in "${PACKAGE_IDS[@]}"; do
        seed_package "$id" "$VERSION" "$VERSION" "$GITHUB_SHA"
    done
}

seed_symbols() {
    local id
    for id in "${PACKAGE_IDS[@]}"; do
        : >"$DOUBLE_STATE/nuget/${id,,}/${VERSION,,}/symbols"
    done
}

# Creates the release $1 with draft flag $2, body $3 and the assets named in the remaining arguments.
seed_release() {
    local tag="$1" draft="$2" body="$3" asset
    shift 3
    local release="$DOUBLE_STATE/releases/$tag"
    mkdir -p "$release/assets"
    printf '%s\n' "$draft" >"$release/draft"
    printf 'false\n' >"$release/prerelease"
    printf 'true\n' >"$release/latest"
    printf '%s\n' "$body" >"$release/body"
    for asset in "$@"; do
        printf 'original\n' >"$release/assets/$asset"
    done
}

asset_names() {
    local id
    for id in "${PACKAGE_IDS[@]}"; do
        printf '%s\n' "$id.$VERSION.nupkg" "$id.$VERSION.snupkg"
    done
}

# Stands in for the Nuke Pack target: one package and one symbol package per project.
pack() {
    local id
    [[ -f $NOTES_DIR/ReleaseNotes.md ]] || { printf 'pack: ReleaseNotes.md is missing\n' >&2; return 1; }
    mkdir -p "$PACKAGE_DIR"
    for id in "${PACKAGE_IDS[@]}"; do
        printf '%s %s\n' "$id" "$VERSION" >"$PACKAGE_DIR/$id.$VERSION.nupkg"
        printf '%s %s\n' "$id" "$VERSION" >"$PACKAGE_DIR/$id.$VERSION.snupkg"
    done
}

script() {
    bash "$SCRIPTS_DIR/$1" >>"$T/output" 2>&1
}

# Runs the jobs of release.yml in their order and with their environments, from job $1 (default: prepare) up to job
# $2 (default: changelog), and stops at the first failure like "needs:" does. The test job is this suite and the .NET
# pipeline; pack is a stub. Starting at a later job is "Re-run failed jobs": the outputs and artifacts of the earlier
# jobs are reused.
run_release() {
    local from="${1:-prepare}" until="${2:-changelog}" started=false job prerelease

    for job in prepare test pack publish release changelog; do
        [[ $job == "$from" || $started == true ]] || continue
        started=true

        case "$job" in
            prepare)
                rm -rf "$NOTES_DIR"
                : >"$GITHUB_OUTPUT"
                checkout
                script validate.sh || return 1
                script preflight.sh || return 1
                # The prepare job only has read permissions, which do not show drafts.
                DOUBLE_GH_HIDE_DRAFTS=1 script release-notes.sh || return 1
                ;;
            pack)
                rm -rf "$PACKAGE_DIR"
                pack || return 1
                ;;
            publish)
                checkout
                script publish.sh || return 1
                ;;
            release)
                checkout
                script verify-published.sh || return 1
                script release.sh || return 1
                ;;
            changelog)
                prerelease="$(sed -n 's/^prerelease=//p' "$GITHUB_OUTPUT")"
                [[ $prerelease == false ]] || continue
                checkout
                script changelog.sh || return 1
                ;;
        esac

        [[ $job != "$until" ]] || break
    done
}

# Reduces a recorded write call to the command and its target.
summarize_write() {
    local -a fields
    read -ra fields <<<"$1"
    case "${fields[0]} ${fields[1]}" in
        "dotnet nuget") printf '%s' "${fields[*]:0:4}" ;;
        "gh release") [[ ${fields[2]} == upload || ${fields[2]} == delete-asset ]] && printf '%s' "${fields[*]:0:5}" || printf '%s' "${fields[*]:0:4}" ;;
        "gh pr") printf '%s' "${fields[*]:0:3}" ;;
        *) [[ " ${fields[*]} " == *" tag "* ]] && printf 'git tag' || printf 'git push %s %s' "${fields[-2]}" "${fields[-1]}" ;;
    esac
}

# --- Assertions -------------------------------------------------------------------------------------------------------

fail_test() {
    printf 'Assertion failed: %s\n' "$*" >&2
    if [[ -f $T/calls ]]; then
        printf -- '--- calls\n' >&2
        cat "$T/calls" >&2
    fi
    if [[ -f $T/output ]]; then
        printf -- '--- output\n' >&2
        cat "$T/output" >&2
    fi
    exit 1
}

assert_succeeds() {
    "$@" || fail_test "expected success: $*"
}

assert_fails() {
    if "$@"; then
        fail_test "expected failure: $*"
    fi
}

assert_output_contains() {
    grep -qF -- "$1" "$T/output" || fail_test "output does not contain '$1'"
}

# Asserts that exactly $1 calls match the extended regular expression $2.
assert_calls() {
    local expected="$1" pattern="$2" actual
    actual="$(grep -cE -- "$pattern" "$DOUBLE_CALLS" || true)"
    [[ $actual == "$expected" ]] || fail_test "expected $expected calls matching '$pattern', got $actual"
}

assert_no_writes() {
    assert_calls 0 "$WRITE_CALLS"
}

assert_no_calls() {
    [[ ! -s $DOUBLE_CALLS ]] || fail_test "expected no calls"
}

assert_equals() {
    [[ $1 == "$2" ]] || fail_test "expected '$2', got '$1'${3:+ ($3)}"
}

assert_file_content() {
    [[ -f $1 ]] || fail_test "$1 does not exist"
    assert_equals "$(<"$1")" "$2" "$1"
}

assert_release() {
    local release="$DOUBLE_STATE/releases/$1"
    [[ -d $release ]] || fail_test "the release $1 does not exist"
    assert_file_content "$release/draft" false
    assert_file_content "$release/prerelease" "$2"
    assert_file_content "$release/latest" "$3"
    assert_equals "$(find "$release/assets" -type f -printf '%f\n' | LC_ALL=C sort)" "$(asset_names | LC_ALL=C sort)" "assets of $1"
}

assert_remote_tag() {
    assert_equals "$(git_real --git-dir="$DOUBLE_ORIGIN" rev-parse "refs/tags/$1^{commit}")" "$2" "commit of tag $1"
}

assert_no_remote_tag() {
    if git_real --git-dir="$DOUBLE_ORIGIN" rev-parse --quiet --verify "refs/tags/$1" >/dev/null; then
        fail_test "the tag $1 exists"
    fi
}

assert_no_remote_branch() {
    if git_real --git-dir="$DOUBLE_ORIGIN" rev-parse --quiet --verify "refs/heads/$1" >/dev/null; then
        fail_test "the branch $1 exists"
    fi
}

assert_pull_requests() {
    local count
    count="$(find "$DOUBLE_STATE/pulls" -type f 2>/dev/null | wc -l)"
    assert_equals "$count" "$1" "pull requests"
}

assert_no_execution() {
    [[ ! -e $T/pwned-backtick && ! -e $T/pwned-substitution && ! -e $T/pwned-version ]] ||
        fail_test "untrusted text was executed"
}

# Asserts the complete outcome of a successful stable release of $VERSION.
assert_complete_stable_release() {
    assert_remote_tag "v$VERSION" "$GITHUB_SHA"
    assert_release "v$VERSION" false true
    assert_succeeds git_real --git-dir="$DOUBLE_ORIGIN" rev-parse --quiet --verify "refs/heads/release/changelog-v$VERSION"
    assert_pull_requests 1
    local id
    for id in "${PACKAGE_IDS[@]}"; do
        [[ -f $DOUBLE_STATE/nuget/${id,,}/${VERSION,,}/symbols ]] || fail_test "the symbols of $id were not pushed"
    done
}

# --- Validation -------------------------------------------------------------------------------------------------------

test_valid_versions_are_accepted() {
    local version expected
    local -a versions=(1.2.3 1.2.3-rc.1 1.0.0-alpha-1 0.0.0 10.20.30-0.a-b.1 1.2.3-rc.lock.1 1.2.3-unlock)

    for version in "${versions[@]}"; do
        expected=false
        [[ $version != *-* ]] || expected=true
        : >"$GITHUB_OUTPUT"
        VERSION="$version" assert_succeeds script validate.sh
        assert_file_content "$GITHUB_OUTPUT" "prerelease=$expected"
    done

    # The only calls are the local tag name checks.
    assert_calls "${#versions[@]}" '^git check-ref-format refs/tags/v'
    assert_equals "$(wc -l <"$DOUBLE_CALLS")" "${#versions[@]}" "number of calls"
}

test_versions_that_git_cannot_tag_fail_before_any_side_effect() {
    local version
    for version in 1.2.3-rc.lock 1.2.3-lock.lock 1.0.0-alpha.1.lock; do
        : >"$T/output"
        VERSION="$version" assert_fails run_release
        assert_output_contains "Git does not accept v$version as a tag name."
        assert_calls 0 '^(gh|curl|dotnet) '
        assert_no_writes
        assert_no_remote_tag "v$version"
        [[ ! -e $NOTES_DIR ]] || fail_test "release notes were written for $version"
    done
}

test_invalid_versions_fail_before_any_side_effect() {
    local version
    local -a versions=(v1.2.3 1.2 01.2.3 1.2.3+build 1.2.3- 1.2.3-01 "" $'1.2.3\n2.0.0' $'1.2.3\n' ' 1.2.3' 1.2.3.4
        "1.2.3-rc.1\$(touch $T/pwned-version)" "1.2.3-\`touch $T/pwned-version\`")

    for version in "${versions[@]}"; do
        : >"$T/output"
        VERSION="$version" assert_fails run_release
        assert_output_contains "is not a SemVer 2.0.0 version"
        assert_calls 0 '^(gh|curl|dotnet) '
        assert_no_writes
        [[ ! -e $NOTES_DIR ]] || fail_test "release notes were written for $(printf '%q' "$version")"
    done
    assert_no_execution
}

test_other_refs_fail_before_any_side_effect() {
    local ref
    for ref in refs/heads/feature/release refs/heads/main-backup refs/tags/v1.2.3 refs/pull/1/merge ""; do
        : >"$T/output"
        GITHUB_REF="$ref" assert_fails run_release
        assert_output_contains "Releases only run from refs/heads/main"
        assert_calls 0 '^(gh|curl|dotnet) '
        assert_no_writes
    done
}

# --- Preflight --------------------------------------------------------------------------------------------------------

test_preflight_queries_every_project_with_lowercase_urls() {
    VERSION=1.2.3-RC.1 assert_succeeds script preflight.sh
    assert_calls 3 '^curl '
    assert_calls 1 'https://api\.nuget\.org/v3-flatcontainer/chaos\.mongo/1\.2\.3-rc\.1/chaos\.mongo\.nuspec$'
    assert_calls 1 'https://api\.nuget\.org/v3-flatcontainer/chaos\.mongo\.eventstore/1\.2\.3-rc\.1/chaos\.mongo\.eventstore\.nuspec$'
    assert_calls 1 'https://api\.nuget\.org/v3-flatcontainer/chaos\.mongo\.outbox/1\.2\.3-rc\.1/chaos\.mongo\.outbox\.nuspec$'
    assert_no_writes
}

test_tag_at_another_commit_fails_before_any_side_effect() {
    local kind
    for kind in lightweight annotated; do
        setup
        seed_tag v1.2.3 "$PREVIOUS_COMMIT" "$kind"
        assert_fails run_release
        assert_output_contains "points to commit '$PREVIOUS_COMMIT'"
        assert_no_writes
    done
}

test_tag_with_other_casing_fails_before_any_side_effect() {
    seed_tag V1.2.3 "$GITHUB_SHA" lightweight
    assert_fails run_release
    assert_output_contains "The tag 'V1.2.3' differs from 'v1.2.3' only in case."
    assert_no_writes

    setup
    seed_tag v1.2.3-RC.1 "$GITHUB_SHA" annotated
    VERSION=1.2.3-rc.1 assert_fails run_release
    assert_output_contains "The tag 'v1.2.3-RC.1' differs from 'v1.2.3-rc.1' only in case."
    assert_no_writes
}

test_package_from_another_commit_fails_before_any_side_effect() {
    seed_package Chaos.Mongo.EventStore 1.2.3 1.2.3 "$OTHER_COMMIT"
    assert_fails run_release
    assert_output_contains "nuget.org serves Chaos.Mongo.EventStore 1.2.3 from commit '$OTHER_COMMIT'"
    assert_no_writes
}

test_package_with_other_version_casing_fails_before_any_side_effect() {
    seed_package Chaos.Mongo 1.2.3-rc.1 1.2.3-RC.1 "$GITHUB_SHA"
    VERSION=1.2.3-rc.1 assert_fails run_release
    assert_output_contains "nuget.org serves Chaos.Mongo as version '1.2.3-RC.1' instead of '1.2.3-rc.1'."
    assert_no_writes
}

test_unexpected_feed_status_fails_before_any_side_effect() {
    mkdir -p "$DOUBLE_STATE/nuget/chaos.mongo.outbox/1.2.3"
    printf '500' >"$DOUBLE_STATE/nuget/chaos.mongo.outbox/1.2.3/status"
    assert_fails run_release
    assert_output_contains "nuget.org answered 500 for Chaos.Mongo.Outbox 1.2.3."
    assert_no_writes
}

# --- Order and complete releases --------------------------------------------------------------------------------------

test_stable_release_runs_every_step_in_order() {
    assert_succeeds run_release
    assert_complete_stable_release

    local -a expected=(
        "dotnet nuget push $PACKAGE_DIR/Chaos.Mongo.1.2.3.nupkg"
        "dotnet nuget push $PACKAGE_DIR/Chaos.Mongo.EventStore.1.2.3.nupkg"
        "dotnet nuget push $PACKAGE_DIR/Chaos.Mongo.Outbox.1.2.3.nupkg"
        "dotnet nuget push $PACKAGE_DIR/Chaos.Mongo.1.2.3.snupkg"
        "dotnet nuget push $PACKAGE_DIR/Chaos.Mongo.EventStore.1.2.3.snupkg"
        "dotnet nuget push $PACKAGE_DIR/Chaos.Mongo.Outbox.1.2.3.snupkg"
        "git tag"
        "git push origin refs/tags/v1.2.3"
        "gh release create v1.2.3"
        "gh release upload v1.2.3 $PACKAGE_DIR/Chaos.Mongo.1.2.3.nupkg"
        "gh release upload v1.2.3 $PACKAGE_DIR/Chaos.Mongo.EventStore.1.2.3.nupkg"
        "gh release upload v1.2.3 $PACKAGE_DIR/Chaos.Mongo.Outbox.1.2.3.nupkg"
        "gh release upload v1.2.3 $PACKAGE_DIR/Chaos.Mongo.1.2.3.snupkg"
        "gh release upload v1.2.3 $PACKAGE_DIR/Chaos.Mongo.EventStore.1.2.3.snupkg"
        "gh release upload v1.2.3 $PACKAGE_DIR/Chaos.Mongo.Outbox.1.2.3.snupkg"
        "git push origin HEAD:refs/heads/release/changelog-v1.2.3"
        "gh pr create"
    )
    local actual="" call
    while IFS= read -r call; do
        actual+="$(summarize_write "$call")"$'\n'
    done < <(grep -E -- "$WRITE_CALLS" "$DOUBLE_CALLS")
    assert_equals "$actual" "$(printf '%s\n' "${expected[@]}")"$'\n' "write calls"

    assert_calls 3 '^dotnet nuget push .*\.nupkg --api-key short-lived-key --source https://api\.nuget\.org/v3/index\.json --no-symbols --skip-duplicate$'
    assert_calls 3 '^dotnet nuget push .*\.snupkg --api-key short-lived-key --source https://api\.nuget\.org/v3/index\.json --skip-duplicate$'
    assert_calls 1 '^gh release create v1\.2\.3 --verify-tag --title v1\.2\.3 --notes-file .* --prerelease=false --latest$'
    assert_equals "$(git_real --git-dir="$DOUBLE_ORIGIN" cat-file -t refs/tags/v1.2.3)" tag "type of tag v1.2.3"
}

test_prerelease_is_marked_and_skips_the_changelog() {
    export VERSION=1.2.3-rc.1
    assert_succeeds run_release
    assert_remote_tag v1.2.3-rc.1 "$GITHUB_SHA"
    assert_release v1.2.3-rc.1 true false
    assert_calls 1 '^gh release create v1\.2\.3-rc\.1 .* --prerelease --latest=false$'
    assert_no_remote_branch release/changelog-v1.2.3-rc.1
    assert_pull_requests 0

    # The script itself also refuses to update the changelog for a prerelease.
    : >"$DOUBLE_CALLS"
    assert_succeeds script changelog.sh
    assert_no_calls
}

test_release_notes_arrive_verbatim_and_nothing_executes() {
    assert_succeeds run_release
    assert_no_execution

    assert_file_content "$NOTES_FILE" "$DRAFTED_BODY"
    assert_file_content "$DOUBLE_STATE/releases/v1.2.3/body" "$DRAFTED_BODY"

    local heading
    heading="$(head -n 1 "$NOTES_DIR/ReleaseNotes.md")"
    [[ $heading =~ ^'# v1.2.3 ('[0-9]{4}-[0-9]{2}-[0-9]{2}')'$ ]] || fail_test "unexpected heading '$heading'"
    assert_file_content "$NOTES_DIR/ReleaseNotes.md" "$heading

$DRAFTED_BODY"

    local changelog date
    changelog="$(git_real --git-dir="$DOUBLE_ORIGIN" show refs/heads/release/changelog-v1.2.3:CHANGELOG.md)"
    date="$(sed -n 's/^## v1\.2\.3 (\(.*\))$/\1/p' <<<"$changelog")"
    [[ $date =~ ^[0-9]{4}-[0-9]{2}-[0-9]{2}$ ]] || fail_test "unexpected changelog date '$date'"
    assert_equals "$changelog" "# Changelog
All notable changes to this project will be documented in this file.



## v1.2.3 ($date)

$DRAFTED_BODY

## v1.2.2 (2026-01-01)

### Features

- Older change (#1) by @someone" "CHANGELOG.md"
    assert_equals "$(git_real --git-dir="$DOUBLE_ORIGIN" log -1 --format=%s refs/heads/release/changelog-v1.2.3)" \
        "docs: update CHANGELOG for 1.2.3 [skip ci]" "changelog commit message"
    assert_equals "$(git_real --git-dir="$DOUBLE_ORIGIN" rev-parse refs/heads/release/changelog-v1.2.3^)" "$GITHUB_SHA" \
        "changelog parent"
}

test_completed_release_is_not_changed_by_a_new_dispatch() {
    assert_succeeds run_release
    local release_body
    release_body="$(<"$DOUBLE_STATE/releases/v1.2.3/body")"

    : >"$DOUBLE_CALLS"
    rm -rf "$T/artifacts"
    export DRAFTED_BODY="- No changes since last release"
    assert_succeeds run_release

    assert_calls 0 '^(git( .*)? (push|tag)|gh (release|pr) (create|upload|edit))( |$)'
    assert_calls 6 '^dotnet nuget push '
    assert_output_contains "Reusing the notes of the published release v1.2.3."
    assert_file_content "$NOTES_FILE" "$release_body"
    assert_complete_stable_release
}

# --- Publishing -------------------------------------------------------------------------------------------------------

test_symbols_are_pushed_when_packages_are_duplicates() {
    seed_all_packages
    mkdir -p "$NOTES_DIR" && : >"$NOTES_DIR/ReleaseNotes.md"
    pack
    assert_succeeds script publish.sh

    assert_calls 3 '^dotnet nuget push .*\.nupkg .* --no-symbols --skip-duplicate$'
    assert_calls 3 '^dotnet nuget push .*\.snupkg '
    local id
    for id in "${PACKAGE_IDS[@]}"; do
        [[ -f $DOUBLE_STATE/nuget/${id,,}/1.2.3/symbols ]] || fail_test "the symbols of $id were not pushed"
    done
}

# Versions whose package file names contain "409" or "conflict", which must not make an error look like a duplicate.
readonly CONFLICTING_VERSIONS=(1.2.409 1.2.3-conflict.409)
readonly SYMBOL_ERRORS=(
    "Response status code does not indicate success: 500 (Internal Server Error)."
    "Response status code does not indicate success: 401 (Unauthorized)."
    "The HTTP request to 'PUT https://www.nuget.org/api/v2/symbolpackage/' has timed out after 300000ms."
)

test_duplicate_symbol_packages_count_as_pushed() {
    local version
    for version in 1.2.3 "${CONFLICTING_VERSIONS[@]}"; do
        setup
        export VERSION="$version"
        seed_all_packages
        seed_symbols
        mkdir -p "$NOTES_DIR" && : >"$NOTES_DIR/ReleaseNotes.md"
        pack
        assert_succeeds script publish.sh
        assert_calls 6 '^dotnet nuget push .* --skip-duplicate$'
        assert_output_contains "Package '$PACKAGE_DIR/Chaos.Mongo.Outbox.$version.snupkg' already exists at feed"
    done
}

test_failed_symbol_push_fails_the_publish_job() {
    local version error
    for version in 1.2.3 "${CONFLICTING_VERSIONS[@]}"; do
        for error in "${SYMBOL_ERRORS[@]}"; do
            setup
            export VERSION="$version"
            DOUBLE_DOTNET_FAIL_SYMBOLS="$error" assert_fails run_release
            assert_output_contains "Pushing Chaos.Mongo.$version.snupkg to 'https://api.nuget.org/v3/index.json'..."
            assert_output_contains "error: $error"
            assert_output_contains "Pushing Chaos.Mongo.$version.snupkg failed."
            assert_calls 3 '^dotnet nuget push .*\.nupkg '
            assert_calls 1 '^dotnet nuget push .*\.snupkg '
            assert_calls 0 '^(git( .*)? (push|tag)|gh (release|pr) (create|upload|edit|delete-asset))( |$)'
            assert_no_remote_tag "v$version"
            assert_no_remote_branch "release/changelog-v$version"
            [[ ! -e $DOUBLE_STATE/releases/v$version ]] || fail_test "the release v$version was created"
        done
    done
}

test_failed_symbol_push_is_completed_by_rerunning_failed_jobs_or_a_new_dispatch() {
    local mode
    for mode in rerun dispatch; do
        setup
        export VERSION=1.2.409
        DOUBLE_DOTNET_FAIL_SYMBOLS="${SYMBOL_ERRORS[0]}" assert_fails run_release
        assert_no_remote_tag v1.2.409

        : >"$DOUBLE_CALLS"
        if [[ $mode == rerun ]]; then
            assert_succeeds run_release publish
        else
            rm -rf "$T/artifacts"
            assert_succeeds run_release
        fi
        # The packages are skipped as duplicates, and every symbol package is pushed.
        assert_calls 3 '^dotnet nuget push .*\.nupkg '
        assert_calls 3 '^dotnet nuget push .*\.snupkg '
        assert_calls 1 '^git( .*)? push origin refs/tags/v1\.2\.409$'
        assert_calls 1 '^gh release create '
        assert_complete_stable_release
    done
}

test_missing_packages_fail_the_publish_job() {
    mkdir -p "$PACKAGE_DIR"
    assert_fails script publish.sh
    assert_output_contains "No packages found"
    assert_no_calls
}

# --- Verification of the published packages ---------------------------------------------------------------------------

test_delayed_package_visibility_is_awaited() {
    export DOUBLE_INDEX_DELAY=2
    assert_succeeds run_release
    assert_complete_stable_release
}

test_visibility_timeout_fails_before_tagging_and_a_rerun_resumes() {
    export DOUBLE_INDEX_DELAY=4
    assert_fails run_release
    assert_output_contains "nuget.org still does not serve Chaos.Mongo Chaos.Mongo.EventStore Chaos.Mongo.Outbox 1.2.3."
    assert_calls 0 '^(git( .*)? (push|tag)|gh (release|pr) (create|upload|edit))( |$)'
    assert_no_remote_tag v1.2.3

    assert_succeeds run_release release
    assert_complete_stable_release
}

test_package_from_another_commit_after_the_push_fails_before_tagging() {
    export DOUBLE_PACKED_COMMIT="$OTHER_COMMIT"
    assert_fails run_release
    assert_output_contains "nuget.org serves Chaos.Mongo 1.2.3 from commit '$OTHER_COMMIT'"
    assert_calls 0 '^(git( .*)? (push|tag)|gh (release|pr) (create|upload|edit))( |$)'
    assert_no_remote_tag v1.2.3
}

# --- Tag and GitHub release -------------------------------------------------------------------------------------------

test_existing_tag_at_the_dispatched_commit_is_kept() {
    local kind
    for kind in annotated lightweight; do
        setup
        seed_tag v1.2.3 "$GITHUB_SHA" "$kind"
        assert_succeeds run_release
        assert_calls 0 '^git( .*)? (tag|push origin refs/tags/)'
        assert_complete_stable_release
    done
}

test_missing_release_is_created_for_an_existing_tag() {
    seed_tag v1.2.3 "$GITHUB_SHA" annotated
    seed_all_packages
    seed_symbols
    assert_succeeds run_release
    assert_calls 1 '^gh release create v1\.2\.3 '
    assert_calls 6 '^gh release upload '
    assert_complete_stable_release
}

test_draft_release_with_a_missing_asset_is_completed() {
    seed_tag v1.2.3 "$GITHUB_SHA" annotated
    mapfile -t assets < <(asset_names)
    seed_release v1.2.3 true "Draft notes" "${assets[@]:1}"

    assert_succeeds run_release
    assert_calls 0 '^gh release create '
    assert_calls 1 '^gh release upload '
    assert_calls 1 "^gh release upload v1\.2\.3 .*/${assets[0]//./\\.}$"
    assert_calls 1 '^gh release edit v1\.2\.3 --draft=false --prerelease=false --latest$'
    assert_complete_stable_release
    assert_file_content "$DOUBLE_STATE/releases/v1.2.3/assets/${assets[1]}" original
}

test_published_release_with_missing_assets_only_gets_those_assets() {
    seed_tag v1.2.3 "$GITHUB_SHA" annotated
    seed_all_packages
    seed_symbols
    mapfile -t assets < <(asset_names)
    seed_release v1.2.3 false "Original notes" "${assets[@]:2}"

    assert_succeeds run_release
    assert_file_content "$NOTES_FILE" "Original notes"
    assert_calls 0 '^gh release (create|edit) '
    assert_calls 2 '^gh release upload '
    assert_complete_stable_release
    local asset
    for asset in "${assets[@]:2}"; do
        assert_file_content "$DOUBLE_STATE/releases/v1.2.3/assets/$asset" original
    done
}

test_failed_tag_push_is_completed_by_rerunning_failed_jobs() {
    DOUBLE_GIT_FAIL_PUSH=1 assert_fails run_release
    assert_no_remote_tag v1.2.3
    assert_calls 0 '^gh release create '

    : >"$DOUBLE_CALLS"
    assert_succeeds run_release release
    assert_calls 1 '^git( .*)? push origin refs/tags/v1\.2\.3$'
    assert_complete_stable_release
}

test_failed_upload_is_completed_by_a_new_dispatch() {
    DOUBLE_GH_FAIL_UPLOAD=1 assert_fails run_release
    assert_calls 1 '^gh release create '

    rm -rf "$T/artifacts"
    export DRAFTED_BODY="- No changes since last release"
    assert_succeeds run_release
    assert_calls 1 '^gh release create '
    assert_calls 1 '^git( .*)? push origin refs/tags/'
    assert_file_content "$DOUBLE_STATE/releases/v1.2.3/body" "$(<"$NOTES_FILE")"
    [[ $(<"$NOTES_FILE") != "$DRAFTED_BODY" ]] || fail_test "the notes of the published release were not reused"
    assert_complete_stable_release
}

# Fails the third upload after GitHub kept an incomplete asset, then completes the release by rerunning the failed jobs
# ($1 = rerun) or by a new dispatch ($1 = dispatch).
assert_incomplete_asset_is_uploaded_again() {
    local release="$DOUBLE_STATE/releases/v1.2.3"
    # release.sh uploads every package before the symbol packages.
    local -a uploads=(
        Chaos.Mongo.1.2.3.nupkg Chaos.Mongo.EventStore.1.2.3.nupkg Chaos.Mongo.Outbox.1.2.3.nupkg
        Chaos.Mongo.1.2.3.snupkg Chaos.Mongo.EventStore.1.2.3.snupkg Chaos.Mongo.Outbox.1.2.3.snupkg
    )
    local incomplete="${uploads[2]}"

    DOUBLE_GH_STARTER_UPLOAD="$incomplete" assert_fails run_release
    [[ -e $release/starter/$incomplete && ! -s $release/assets/$incomplete ]] ||
        fail_test "the failed upload did not leave an incomplete asset"
    assert_equals "$(find "$release/assets" -type f -printf '%f\n' | LC_ALL=C sort)" \
        "$(printf '%s\n' "${uploads[@]:0:3}" | LC_ALL=C sort)" "assets after the failed upload"

    : >"$DOUBLE_CALLS"
    if [[ $1 == rerun ]]; then
        assert_succeeds run_release release
    else
        rm -rf "$T/artifacts"
        assert_succeeds run_release
    fi

    # The complete assets are kept, the incomplete one is deleted and uploaded again, and the missing ones are uploaded.
    local -a expected=(
        "gh release delete-asset v1.2.3 $incomplete"
        "gh release upload v1.2.3 $PACKAGE_DIR/$incomplete"
        "gh release upload v1.2.3 $PACKAGE_DIR/${uploads[3]}"
        "gh release upload v1.2.3 $PACKAGE_DIR/${uploads[4]}"
        "gh release upload v1.2.3 $PACKAGE_DIR/${uploads[5]}"
        "git push origin HEAD:refs/heads/release/changelog-v1.2.3"
        "gh pr create"
    )
    local actual="" call
    while IFS= read -r call; do
        actual+="$(summarize_write "$call")"$'\n'
    done < <(grep -E -- "$WRITE_CALLS" "$DOUBLE_CALLS" | grep -v '^dotnet nuget push ')
    assert_equals "$actual" "$(printf '%s\n' "${expected[@]}")"$'\n' "write calls"

    assert_calls 1 "^gh release delete-asset v1\.2\.3 ${incomplete//./\\.} --yes$"
    assert_output_contains "The release v1.2.3 has the incomplete asset $incomplete in state 'starter'. Uploading it again."
    [[ ! -e $release/starter/$incomplete ]] || fail_test "the asset $incomplete is still incomplete"
    assert_file_content "$release/assets/$incomplete" "Chaos.Mongo.Outbox 1.2.3"
    assert_complete_stable_release
}

test_incomplete_asset_is_uploaded_again_by_rerunning_failed_jobs() {
    assert_incomplete_asset_is_uploaded_again rerun
}

test_incomplete_asset_is_uploaded_again_by_a_new_dispatch() {
    assert_incomplete_asset_is_uploaded_again dispatch
}

test_incomplete_asset_of_a_published_release_is_uploaded_again() {
    seed_tag v1.2.3 "$GITHUB_SHA" annotated
    seed_all_packages
    seed_symbols
    mapfile -t assets < <(asset_names)
    seed_release v1.2.3 false "Original notes" "${assets[@]}"
    local release="$DOUBLE_STATE/releases/v1.2.3"
    mkdir -p "$release/starter"
    : >"$release/assets/${assets[0]}"
    : >"$release/starter/${assets[0]}"

    assert_succeeds run_release
    assert_calls 0 '^gh release (create|edit) '
    assert_calls 1 '^gh release delete-asset '
    assert_calls 1 '^gh release upload '
    assert_calls 1 "^gh release upload v1\.2\.3 .*/${assets[0]//./\\.}$"
    assert_file_content "$release/assets/${assets[0]}" "Chaos.Mongo 1.2.3"
    local asset
    for asset in "${assets[@]:1}"; do
        assert_file_content "$release/assets/$asset" original
    done
    assert_complete_stable_release
}

# --- Partial failures of the nuget.org push ---------------------------------------------------------------------------

test_failed_package_push_is_completed_by_rerunning_failed_jobs() {
    DOUBLE_DOTNET_FAIL_PACKAGE=Chaos.Mongo.Outbox assert_fails run_release
    assert_calls 0 '^(git( .*)? (push|tag)|gh (release|pr) (create|upload|edit))( |$)'
    assert_no_remote_tag v1.2.3

    assert_succeeds run_release publish
    assert_calls 1 '^git( .*)? push origin refs/tags/v1\.2\.3$'
    assert_calls 1 '^gh release create '
    assert_calls 6 '^gh release upload '
    assert_complete_stable_release
}

test_failed_package_push_is_completed_by_a_new_dispatch() {
    DOUBLE_DOTNET_FAIL_PACKAGE=Chaos.Mongo.Outbox assert_fails run_release

    rm -rf "$T/artifacts"
    assert_succeeds run_release
    assert_calls 1 '^git( .*)? push origin refs/tags/v1\.2\.3$'
    assert_calls 1 '^gh release create '
    assert_complete_stable_release
}

# --- Changelog --------------------------------------------------------------------------------------------------------

test_changelog_entry_on_main_skips_the_changelog() {
    assert_succeeds run_release prepare release
    # The changelog pull request of an earlier run was merged.
    cd "$T/source"
    printf '## v1.2.3 (2026-02-02)\n' >>CHANGELOG.md
    commit_all "docs: update CHANGELOG for 1.2.3 [skip ci]"
    git_real push --quiet origin main

    : >"$DOUBLE_CALLS"
    assert_succeeds run_release changelog
    assert_output_contains "CHANGELOG.md on main already contains v1.2.3."
    assert_no_writes
    assert_no_remote_branch release/changelog-v1.2.3
}

test_existing_changelog_branch_without_pull_request_gets_one() {
    cd "$T/source"
    git_real push --quiet origin "HEAD:refs/heads/release/changelog-v1.2.3"
    assert_succeeds run_release
    assert_calls 0 '^git( .*)? push origin HEAD:refs/heads/'
    assert_calls 1 '^gh pr create --base main --head release/changelog-v1\.2\.3 --title docs:\\ update\\ CHANGELOG\.md\\ for\\ v1\.2\.3 '
    assert_pull_requests 1
}

test_existing_changelog_pull_request_is_kept() {
    cd "$T/source"
    git_real push --quiet origin "HEAD:refs/heads/release/changelog-v1.2.3"
    mkdir -p "$DOUBLE_STATE/pulls"
    printf 'release/changelog-v1.2.3\nopen\n' >"$DOUBLE_STATE/pulls/1"
    assert_succeeds run_release
    assert_calls 0 '^(git( .*)? push origin HEAD:|gh pr create)'
    assert_pull_requests 1
}

test_closed_changelog_pull_request_is_not_reopened() {
    cd "$T/source"
    git_real push --quiet origin "HEAD:refs/heads/release/changelog-v1.2.3"
    mkdir -p "$DOUBLE_STATE/pulls"
    printf 'release/changelog-v1.2.3\nclosed\n' >"$DOUBLE_STATE/pulls/1"
    assert_succeeds run_release
    assert_output_contains "A pull request for release/changelog-v1.2.3 already exists."
    assert_calls 0 '^(git( .*)? push origin HEAD:|gh pr create)'
    assert_pull_requests 1
}

test_failed_pull_request_is_completed_by_rerunning_failed_jobs() {
    DOUBLE_GH_FAIL_PR=1 assert_fails run_release
    assert_pull_requests 0

    assert_succeeds run_release changelog
    assert_calls 1 '^git( .*)? push origin HEAD:refs/heads/release/changelog-v1\.2\.3$'
    assert_complete_stable_release
}

# --- Runner -----------------------------------------------------------------------------------------------------------

run_one() {
    REAL_GIT="$(command -v git)"
    export REAL_GIT
    # Isolate Git from the configuration of the machine, such as commit signing.
    export GIT_CONFIG_GLOBAL=/dev/null GIT_CONFIG_NOSYSTEM=1
    unset DOUBLE_GH_HIDE_DRAFTS DOUBLE_GH_FAIL_UPLOAD DOUBLE_GH_STARTER_UPLOAD DOUBLE_GH_FAIL_PR DOUBLE_GIT_FAIL_PUSH DOUBLE_DOTNET_FAIL_PACKAGE \
        DOUBLE_DOTNET_FAIL_SYMBOLS DOUBLE_INDEX_DELAY DOUBLE_PACKED_COMMIT \
        NUGET_SOURCE NUGET_FLATCONTAINER_URL SRC_DIR GH_TOKEN GITHUB_TOKEN

    setup
    "$1"

    # Whatever happened, nothing may be overwritten or deleted.
    if grep -E -- "$DESTRUCTIVE_CALLS" "$DOUBLE_CALLS" >&2; then
        fail_test "destructive calls"
    fi
    if [[ -e $DOUBLE_STATE/deleted-uploaded-assets ]]; then
        cat "$DOUBLE_STATE/deleted-uploaded-assets" >&2
        fail_test "complete release assets were deleted"
    fi
}

main() {
    local -a tests=("$@")
    local name passed=0 failed=0 log

    if ((${#tests[@]} == 0)); then
        mapfile -t tests < <(declare -F | sed -n 's/^declare -f \(test_.*\)$/\1/p')
    fi

    log="$(mktemp)"
    for name in "${tests[@]}"; do
        if bash "$SELF" --run-one "$name" >"$log" 2>&1; then
            printf 'PASS %s\n' "$name"
            passed=$((passed + 1))
        else
            printf 'FAIL %s\n' "$name"
            sed 's/^/    /' "$log"
            failed=$((failed + 1))
        fi
    done
    rm -f "$log"

    printf '\n%d passed, %d failed\n' "$passed" "$failed"
    ((failed == 0))
}

if [[ ${1-} == --run-one ]]; then
    run_one "$2"
else
    main "$@"
fi
