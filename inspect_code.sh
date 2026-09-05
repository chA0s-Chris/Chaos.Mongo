#!/bin/bash
#
#
set -euo pipefail

CACHES_HOME="tmp/inspectcode-caches"
REPORT_FILE="tmp/inspectcode-report.xml"

# `inspectcode` reports semantic issues, not formatting ones, and its rule set only covers C#
# in this solution. Formatting stays the job of cleanup_code.sh.
ARGUMENTS=(--caches-home="${CACHES_HOME}"
           --format=Xml
           --absolute-paths
           --output="${REPORT_FILE}"
           --verbosity=ERROR)

INSPECT_ALL=0
BASE_COUNT=0
BASE_REVISION=""
AWAITING_BASE_REVISION=0
HAS_SEVERITY=0

# --all and --base are script-owned modes and are accepted in any position: inspectcode ignores an
# unknown option, so a forwarded --all would silently inspect the changed files instead of the
# solution, and a forwarded --base=<revision> would silently inspect the working tree.
for argument in "$@"; do
    if [ "${AWAITING_BASE_REVISION}" -eq 1 ]; then
        # A leading dash is an option, not a revision: `--base --all` must report the missing
        # revision rather than fail later trying to resolve `--all` as a commit.
        case "${argument}" in
            -*) ;;
            *)
                BASE_REVISION="${argument}"
                AWAITING_BASE_REVISION=0
                continue
                ;;
        esac
    fi

    case "${argument}" in
        --all)
            INSPECT_ALL=1
            ;;
        --base)
            BASE_COUNT=$((BASE_COUNT + 1))
            AWAITING_BASE_REVISION=1
            ;;
        --base=*)
            BASE_COUNT=$((BASE_COUNT + 1))
            BASE_REVISION="${argument#--base=}"
            ;;
        -e|--severity|--sEverity|-e=*|--severity=*|--sEverity=*)
            HAS_SEVERITY=1
            ARGUMENTS+=("${argument}")
            ;;
        -f|--format|-o|--output|-f=*|--format=*|-o=*|--output=*)
            # Forwarding either one breaks the report this script parses: a second --format makes
            # inspectcode write nothing at all, and --output moves the report out from under the
            # reader. Both would surface as the missing-report error below rather than as the
            # argument problem they are.
            echo "${argument%%=*} is owned by this script: the report has to stay XML at ${REPORT_FILE} so findings can be displayed." >&2
            exit 2
            ;;
        *)
            ARGUMENTS+=("${argument}")
            ;;
    esac
done

if [ "${HAS_SEVERITY}" -eq 0 ]; then
    ARGUMENTS+=(--severity=WARNING)
fi

if [ "${BASE_COUNT}" -gt 1 ]; then
    echo "Only one --base <revision> is allowed." >&2
    exit 2
fi

if [ "${AWAITING_BASE_REVISION}" -eq 1 ] || { [ "${BASE_COUNT}" -eq 1 ] && [ -z "${BASE_REVISION}" ]; }; then
    echo "--base needs a revision, for example: ./inspect_code.sh --base main" >&2
    exit 2
fi

if [ "${BASE_COUNT}" -eq 1 ] && [ "${INSPECT_ALL}" -eq 1 ]; then
    echo "--base and --all are mutually exclusive: --base inspects a diff, --all the whole solution." >&2
    exit 2
fi

BASE_COMMIT=""

if [ "${BASE_COUNT}" -eq 1 ]; then
    # Resolving the revision here turns an unusable base into a diagnostic instead of an empty diff
    # that looks like a clean inspection.
    if ! BASE_COMMIT=$(git rev-parse --verify --quiet "${BASE_REVISION}^{commit}"); then
        echo "Cannot resolve --base revision '${BASE_REVISION}' to a commit." >&2
        exit 2
    fi
fi

if [ "${INSPECT_ALL}" -eq 0 ]; then
    # Untracked files are included for the same reason cleanup_code.sh includes them: a new file is
    # not staged yet when this script is normally run, and it is the most likely to have findings.
    # With --base, the committed changes of a branch or stack layer join that selection. A deletion
    # leaves nothing to inspect, and a copy arrives as an addition because Git does not detect copies
    # unless it is asked to. Both committed and staged renames are inspected under their new paths.
    COMMITTED_PATHS=""

    if [ -n "${BASE_COMMIT}" ]; then
        # Kept out of the group below, which reports the status of its last command only: a failing
        # diff would be masked there and silently narrow the inspection to the working tree while
        # still looking like a clean scoped run.
        if ! COMMITTED_PATHS=$(git diff --name-only --diff-filter=ACMR "${BASE_COMMIT}...HEAD"); then
            echo "Cannot diff '${BASE_REVISION}...HEAD'. A shallow clone has no merge base; fetch more history." >&2
            exit 2
        fi
    fi

    PATTERNS=$({ if [ -n "${COMMITTED_PATHS}" ]; then printf '%s\n' "${COMMITTED_PATHS}"; fi
                 git diff --name-only --diff-filter=ACMR
                 git diff --name-only --cached --diff-filter=ACMR
                 git ls-files --others --exclude-standard; } | { grep '\.cs$' | sort -u | sed 's|^|**/|' | paste -sd ';' || true; })

    # Without --include, inspectcode analyzes the whole solution, so an empty file set must not
    # simply be passed through.
    if [ -z "${PATTERNS}" ]; then
        echo "No matching files to process."
        exit 0
    fi

    ARGUMENTS+=(--include="${PATTERNS}")
fi

mkdir -p "$(dirname "${REPORT_FILE}")"

# A stale report must not survive a failed run, or it could be mistaken for the current result.
rm -f "${REPORT_FILE}"

dotnet jb inspectcode "${ARGUMENTS[@]}" Chaos.Mongo.slnx

if [ ! -f "${REPORT_FILE}" ]; then
    echo "inspectcode wrote no report. The solution most likely failed to build." >&2
    exit 1
fi

# Display every finding from the XML report, grouped by project.
FINDINGS=$(awk '
    function attr(text, name,    rest, end) {
        # The leading space keeps a name from matching the tail of another one, such as Id in TypeId.
        # An attribute value cannot contain a quote, because XML escapes it, so the value ends at the
        # next one.
        if (!match(text, " " name "=\"")) return ""
        rest = substr(text, RSTART + RLENGTH)
        end = index(rest, "\"")
        if (end == 0) return ""
        return substr(rest, 1, end - 1)
    }

    function unescape(text) {
        gsub(/&lt;/, "<", text)
        gsub(/&gt;/, ">", text)
        gsub(/&quot;/, "\"", text)
        gsub(/&apos;/, "'"'"'", text)
        gsub(/&amp;/, "\\&", text)
        return text
    }

    /^[[:space:]]*<Project / {
        project = unescape(attr($0, "Name"))
        entries = ""
        next
    }

    /^[[:space:]]*<Issue / {
        file = unescape(attr($0, "File"))
        line = attr($0, "Line")
        message = unescape(attr($0, "Message"))
        # A finding without a line number belongs to the file as a whole.
        entries = entries sprintf("      %s%s %s\n", file, (line == "" ? "" : ":" line), message)
        next
    }

    # Projects without findings must not leave a heading behind.
    /^[[:space:]]*<\/Project>/ {
        if (entries != "") printf "    Project %s\n%s", project, entries
        next
    }
' "${REPORT_FILE}")

if [ -n "${FINDINGS}" ]; then
    echo "Solution $(pwd)/Chaos.Mongo.slnx"
    printf '%s\n' "${FINDINGS}"
else
    echo "No issues found."
fi
