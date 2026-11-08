#!/bin/bash
# Managed by repo-baseline (https://github.com/chA0s-Chris/repo-baseline). Do not edit; change it there.
set -euo pipefail

cd "$(git rev-parse --show-toplevel)"

# jq reads inspection.suppressedIds from the manifest. It is required even without a manifest, so a
# missing jq shows up on the first run instead of when a repository starts suppressing an inspection.
if ! command -v jq > /dev/null; then
    echo "inspect_code.sh needs jq to read inspection.suppressedIds from .repo-baseline.json. Install jq and run it again." >&2
    exit 2
fi

CACHES_HOME="tmp/inspectcode-caches"
REPORT_FILE="tmp/inspectcode-report.xml"

# `inspectcode` reports semantic issues, not formatting ones, so the script selects C# and Razor
# files only. Formatting stays the job of cleanup_code.sh.
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

# The solution is discovered instead of named, so the same script works in every repository.
shopt -s nullglob
SOLUTIONS=(*.slnx)
shopt -u nullglob

if [ "${#SOLUTIONS[@]}" -eq 0 ]; then
    echo "No .slnx solution found in $(pwd)." >&2
    exit 2
fi

if [ "${#SOLUTIONS[@]}" -gt 1 ]; then
    echo "Found several .slnx solutions in $(pwd): ${SOLUTIONS[*]}. Keep exactly one in the repository root." >&2
    exit 2
fi

SOLUTION="${SOLUTIONS[0]}"

# Inspection IDs listed here are dropped from the report, for example a compiler diagnostic that a
# DiagnosticSuppressor already handles: ReSharper does not run suppressors, so it reports it anyway.
SUPPRESSED_IDS=""

if [ -f .repo-baseline.json ]; then
    if ! SUPPRESSED_IDS=$(jq -r '(.inspection.suppressedIds // [])
                                 | if type == "array" and all(.[]; type == "string") then .[]
                                   else error("inspection.suppressedIds must be an array of strings") end' .repo-baseline.json); then
        echo "Cannot read inspection.suppressedIds from .repo-baseline.json." >&2
        exit 2
    fi
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
                 git ls-files --others --exclude-standard; } | { grep '\.\(cs\|razor\)$' | sort -u | sed 's|^|**/|' | paste -sd ';' || true; })

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

dotnet jb inspectcode "${ARGUMENTS[@]}" "${SOLUTION}"

if [ ! -f "${REPORT_FILE}" ]; then
    echo "inspectcode wrote no report. The solution most likely failed to build." >&2
    exit 1
fi

# Display the findings from the XML report, grouped by project. The report is XML rather than text
# because only XML carries the inspection ID that suppressed findings are recognized by.
FINDINGS=$(SUPPRESSED_IDS="${SUPPRESSED_IDS}" awk '
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

    # Orders target frameworks by version, oldest first: netX.Y names compare numerically, so net9.0
    # comes before net10.0, and every other name, such as netstandard2.0, follows them sorted by name.
    function framework_before(a, b,    a_version, b_version, a_parts, b_parts) {
        a_version = (a ~ /^net[0-9]+\.[0-9]+$/)
        b_version = (b ~ /^net[0-9]+\.[0-9]+$/)
        if (a_version != b_version) return a_version
        if (!a_version) return a < b
        split(substr(a, 4), a_parts, ".")
        split(substr(b, 4), b_parts, ".")
        if (a_parts[1] + 0 != b_parts[1] + 0) return a_parts[1] + 0 < b_parts[1] + 0
        return a_parts[2] + 0 < b_parts[2] + 0
    }

    BEGIN {
        count = split(ENVIRON["SUPPRESSED_IDS"], ids, "\n")
        for (i = 1; i <= count; i++) {
            if (ids[i] != "") suppressed[ids[i]] = 1
        }
    }

    # split() empties an array portably; `delete array` is not available in every awk.
    /^[[:space:]]*<Project / {
        project = unescape(attr($0, "Name"))
        finding_count = 0
        project_framework_count = 0
        split("", finding_index)
        split("", finding_text)
        split("", finding_framework_count)
        split("", finding_frameworks)
        split("", finding_has_framework)
        split("", project_has_framework)
        next
    }

    # inspectcode reports a finding once per target framework of a multi-targeted project. Such copies
    # differ only in their TargetFramework attribute, so they are printed as one finding.
    /^[[:space:]]*<Issue / {
        framework = attr($0, "TargetFramework")
        # The frameworks of the project are collected before suppression, so suppressing one inspection
        # does not change how the other findings are labeled.
        if (framework != "" && !(framework in project_has_framework)) {
            project_has_framework[framework] = 1
            project_framework_count++
        }
        type_id = unescape(attr($0, "TypeId"))
        if (type_id in suppressed) next
        file = unescape(attr($0, "File"))
        line = attr($0, "Line")
        message = unescape(attr($0, "Message"))
        key = type_id SUBSEP file SUBSEP attr($0, "Offset") SUBSEP message
        if (!(key in finding_index)) {
            finding_index[key] = ++finding_count
            # A finding without a line number belongs to the file as a whole.
            finding_text[finding_count] = sprintf("      %s%s %s", file, (line == "" ? "" : ":" line), message)
            finding_framework_count[finding_count] = 0
        }
        n = finding_index[key]
        if (framework != "" && !((n, framework) in finding_has_framework)) {
            finding_has_framework[n, framework] = 1
            finding_frameworks[n, ++finding_framework_count[n]] = framework
        }
        next
    }

    # A project without findings, or with suppressed findings only, must not leave a heading behind.
    /^[[:space:]]*<\/Project>/ {
        if (finding_count == 0) next
        printf "    Project %s\n", project
        for (n = 1; n <= finding_count; n++) {
            text = finding_text[n]
            frameworks = finding_framework_count[n]
            # Only a finding missing from some frameworks of the project names the ones it occurs in.
            if (frameworks > 0 && frameworks < project_framework_count) {
                for (i = 2; i <= frameworks; i++) {
                    current = finding_frameworks[n, i]
                    for (j = i - 1; j >= 1 && framework_before(current, finding_frameworks[n, j]); j--) {
                        finding_frameworks[n, j + 1] = finding_frameworks[n, j]
                    }
                    finding_frameworks[n, j + 1] = current
                }
                suffix = finding_frameworks[n, 1]
                for (i = 2; i <= frameworks; i++) suffix = suffix ", " finding_frameworks[n, i]
                text = text " (" suffix ")"
            }
            print text
        }
        next
    }
' "${REPORT_FILE}")

if [ -n "${FINDINGS}" ]; then
    echo "Solution $(pwd)/${SOLUTION}"
    printf '%s\n' "${FINDINGS}"
else
    echo "No issues found."
fi
