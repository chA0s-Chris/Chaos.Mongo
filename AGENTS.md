# Root AGENTS.md

`Chaos.Mongo` is a .NET library for working with MongoDB providing additional features like database migrations, distributed locking, message queues, and more.

## Implementation rules

When implementing an active plan, mark each acceptance criterion as complete only after verifying it. Do not modify historical plans during unrelated work.

## General Rules for the Code Base

- `<TreatWarningsAsErrors>` is enabled in Release builds, so your code changes must not generate warnings.
- Expose types and members publicly when consumers need them for configuration, extension, or testing. Keep implementation details non-public, and treat every new public API as a compatibility commitment.
- Target frameworks are defined in `Directory.Build.props` and directory-specific props files. Do not change them unless the task explicitly requires it.
- NuGet package versions are managed centrally in `Directory.Packages.props`; project files reference packages without versions.

### Code Style

For the project's code style, refer to `CODESTYLE.md`.

## Local Development Commands

Run these commands from the repository root. The code-style scripts and coverage tooling use the local tools declared in `.config/dotnet-tools.json`, so run `dotnet tool restore` after cloning or when the manifest changes.

| Purpose | Command |
| --- | --- |
| Restore local tools | `dotnet tool restore` |
| Apply the code style | `./cleanup_code.sh` |
| Inspect changed C# files | `./inspect_code.sh` |
| Inspect C# changes relative to a base | `./inspect_code.sh --base <revision>` |
| Complete test suite | `dotnet test Chaos.Mongo.slnx` |
| Tests excluding integration tests | `dotnet test Chaos.Mongo.slnx --filter "FullyQualifiedName!~.Integration."` |
| One test project | `dotnet test <path-to-test-project.csproj>` |
| Release build | `dotnet build -c Release Chaos.Mongo.slnx` |
| Validate the Nuke test pipeline and collect merged coverage | `bash ./build.sh Test` |

The complete suite includes Testcontainers-based MongoDB integration tests and needs Docker. Integration tests currently live in `.Integration` namespaces rather than carrying an `Integration` test category, so use the namespace filter above to exclude them. The test projects target multiple frameworks; install the SDK selected by `global.json` and the runtimes required by `Directory.Build.props` and any directory-specific props files.

Prefer direct `dotnet` commands for normal development. Use `bash ./build.sh Test` when changing `build/` or other Nuke pipeline behavior, or when measuring the merged coverage required by `tests/AGENTS.md`. It runs the test projects with XPlat Code Coverage and writes `artifacts/test-coverage/coverage.cobertura.merged.xml`. Remove `artifacts/test-coverage` before requesting a fresh coverage measurement so the merge does not include previous results.

### Code Style and Inspections

Run `./cleanup_code.sh` when a code change is complete. It applies ReSharper's `Zorn` profile to relevant files that Git reports as changed, staged, or untracked. Inspect the resulting diff, then run scoped inspections and the relevant build and test checks. Do not run cleanup during read-only reviews.

`./inspect_code.sh` reports semantic findings at warning or error severity by default and selects only C# files. Arguments not owned by the script are forwarded to `dotnet jb inspectcode`; for example, `-e=SUGGESTION` also includes suggestions. The script owns `--all`, `--base`, `--format`, and `--output`; it rejects the last two to preserve the XML report used to display findings grouped by project. It displays every finding in that report without suppressing diagnostic IDs. Both scripts print `No matching files to process.` and exit successfully when no relevant files are selected.

Choose the inspection scope deliberately:

- **No arguments** inspects changed, staged, and untracked C# files. Use this during implementation, after cleanup and before committing.
- **`--base <revision>`** inspects C# files added, modified, or renamed in `<revision>...HEAD`, together with current working-tree changes. Use this for committed branch changes; `--base=<revision>` is equivalent.
- **`--all`** inspects the whole solution. Reserve this for explicit whole-solution audits, analyzer or configuration changes, and broad refactors because it includes pre-existing findings.

`--base` and `--all` are mutually exclusive. If a scoped run selects no C# files, inspection is not applicable to that change; do not fall back to `--all`.

Findings are advisory: the script reports them and exits successfully. A non-zero exit means the inspection itself failed, such as from invalid arguments, an unresolved base revision, or a missing merge base in a shallow clone. A successful exit alone does not mean there are no findings.

Formatting remains `cleanup_code.sh`'s responsibility; inspections are not a formatting check. Do not use `dotnet format`: it does not read `Chaos.Mongo.slnx.DotSettings` and can contradict the repository's ReSharper profile. `git diff --check` checks whitespace hygiene, not compliance with that profile.

## Production Code Rules

Read ./src/AGENTS.md for details about the production code.

## Testing Rules

Read ./tests/AGENTS.md for details about how to write tests.

## Plan Rules

Read ./ai-plans/AGENTS.md for details on how to write plans.

## Benchmark Rules

Read ./benchmarks/AGENTS.md for details about how to write benchmarks.

## Here is Your Space

If you encounter something worth noting while working on this code base, report it in your final response instead of editing this file. I will discuss it with you, and we can decide where to put the note.
