#!/bin/bash
# Run from the repository root: bash tests/scripts/code_style_rename_test.sh
set -euo pipefail

REPOSITORY_ROOT=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
TEST_DIRECTORY=$(mktemp -d)
trap 'rm -rf "${TEST_DIRECTORY}"' EXIT

mkdir -p "${TEST_DIRECTORY}/repository" "${TEST_DIRECTORY}/bin"
export DOTNET_ARGUMENTS_FILE="${TEST_DIRECTORY}/dotnet-arguments"

# Capture the selected files without invoking ReSharper or requiring a .NET SDK.
cat > "${TEST_DIRECTORY}/bin/dotnet" <<'STUB'
#!/bin/bash
set -euo pipefail
printf '%s\n' "$@" > "${DOTNET_ARGUMENTS_FILE}"
for argument in "$@"; do
    case "${argument}" in
        --output=*) printf '<Report><Issues /></Report>\n' > "${argument#--output=}" ;;
    esac
done
STUB
chmod +x "${TEST_DIRECTORY}/bin/dotnet"
export PATH="${TEST_DIRECTORY}/bin:${PATH}"

cd "${TEST_DIRECTORY}/repository"
git init --quiet
git config user.name "Script tests"
git config user.email "script-tests@example.invalid"
git config commit.gpgsign false
git config core.hooksPath "${TEST_DIRECTORY}/empty-hooks"
git config diff.renames true

{
    echo 'class Example'
    echo '{'
    for number in {1..20}; do
        echo "    public int Value${number} => ${number};"
    done
    echo '}'
} > Original.cs
git add Original.cs
git commit --quiet -m "Create rename fixture"

git mv Original.cs Renamed.cs
sed -i 's/Value1 => 1/value1 => 99/' Renamed.cs
git add Renamed.cs

# Ensure the fixture exercises a rename with edits, rather than an add/delete pair.
git diff --cached --name-status | grep -Eq $'^R[0-9]+\tOriginal.cs\tRenamed.cs$'
git diff --cached -- Renamed.cs | grep -Fq '+    public int value1 => 99;'

for invocation in cleanup inspect inspect-base; do
    rm -f "${DOTNET_ARGUMENTS_FILE}"
    case "${invocation}" in
        cleanup) bash "${REPOSITORY_ROOT}/cleanup_code.sh" ;;
        inspect) bash "${REPOSITORY_ROOT}/inspect_code.sh" ;;
        inspect-base) bash "${REPOSITORY_ROOT}/inspect_code.sh" --base HEAD ;;
    esac

    if [ ! -f "${DOTNET_ARGUMENTS_FILE}" ] ||
        ! grep -Fxq -- '--include=**/Renamed.cs' "${DOTNET_ARGUMENTS_FILE}"; then
        echo "FAIL: ${invocation} did not select the staged rename destination." >&2
        exit 1
    fi
    echo "PASS: ${invocation} selects the staged rename with edits."
done
