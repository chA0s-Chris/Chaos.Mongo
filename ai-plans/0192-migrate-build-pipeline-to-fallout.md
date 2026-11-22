# Migrate the build pipeline from NUKE to Fallout

> Issue: [#192](https://github.com/chA0s-Chris/Chaos.Mongo/issues/192)

## Rationale

NUKE is no longer maintained, and its stale dependencies forced the transitive pins of #126 into the build project. [Fallout](https://fallout.build/) is the maintained hard fork. Version 10.5.0 ships `NuGet.Packaging` 7.9.0 ([Fallout-build/Fallout#677](https://github.com/Fallout-build/Fallout/pull/677)) and a patched `System.Security.Cryptography.Xml`, which make the pins unnecessary, and fixes a startup crash with an exported `HOST` variable that NUKE 10.1.0 also has ([Fallout-build/Fallout#705](https://github.com/Fallout-build/Fallout/issues/705)). A trial migration to 10.5.0 without the pins ran `Test` and `PackCheck` successfully and without warnings.

## Acceptance Criteria

- [x] The build project references `Fallout.Common` 10.5.0 and targets `net10.0`, and no tracked file outside `ai-plans/`, `CHANGELOG.md` and the blocks managed by repo-baseline mentions NUKE in its path or content, regardless of case. No build source uses the `Fallout.Common.ProjectModel` namespace.
- [x] `dotnet repo-baseline check .` reports no findings, and Git tracks `.fallout/parameters.json` and ignores everything else in `.fallout/`.
- [x] `bash build.sh Test`, run without existing `build/bin` and `build/obj`, passes without warnings in the build project's restore and compile output and in the pipeline, including no NU1701, NU1901, NU1902 or NU1903 warnings and no `Exception was suppressed` warning.
- [x] `bash build.sh Pack --release-version <test version>`, run with the release key and an empty publish directory, produces one `.nupkg` and one `.snupkg` for each of the three packages and verifies the signatures, and `bash build.sh PackCheck` passes.
- [x] `pwsh -File build.ps1 PackCheck` passes.
- [x] `bash build.sh Test` passes in a linked git worktree outside the clone's directory tree and writes the merged coverage file, so #321 stays fixed.
- [x] `HOST=chaos-mongo-workstation bash build.sh Restore` passes.
- [x] With the .NET 10 and .NET 11 SDKs installed, Fallout's in-process MSBuild loads from the .NET 10 SDK and evaluates the `net10.0` and the `net11.0` target of `src/Chaos.Mongo/Chaos.Mongo.csproj` with the final set of pins.
- [x] `dotnet list build/Build.csproj package --vulnerable --include-transitive` reports no vulnerable packages.
- [x] Every #126 pin is either removed or kept with a comment that explains why Fallout 10.5.0 still needs it.
- [x] For each target framework, the packed nuspecs of all three packages declare the same dependency IDs and version ranges as packages built from the branch's base commit.

## Technical Details

The migration is a manual rename that follows the [migration guide](https://github.com/Fallout-build/Fallout/blob/10.5.0/docs/Migration/from-nuke.md): namespaces, `NukeBuild` to `FalloutBuild`, the `NukeRootDirectory` and `NukeScriptDirectory` properties, and `.nuke/` to `.fallout/`. One namespace does not follow the prefix rename: `Nuke.Common.ProjectModel` becomes `Fallout.Solutions`. Fallout 10.5.0 keeps `Fallout.Common.ProjectModel` only as a temporary shim, which compiles without a warning. `fallout-migrate`, the transition shim and the global tool are not used. `NukeTelemetryVersion` goes away, because Fallout has no telemetry. `build.sh` and `build.ps1` keep running the build project with `dotnet build` and `dotnet run`; they lose the Nuke Enterprise feed block. `build.ps1` keeps its CRLF line endings. `build/Nuke.csproj` becomes `build/Build.csproj`, a tool-neutral name, and `Chaos.Mongo.slnx` follows. The three "Run NUKE pipeline" steps in `ci.yml` and the NUKE mentions in comments and `AGENTS.md` are renamed as well; the commands that CI runs do not change.

`[UsedImplicitly]` on four targets comes from `JetBrains.Annotations`, which NUKE provided transitively and Fallout 10.5.0 does not. The build project gets a direct, centrally versioned reference, and the attributes stay. This was the only source break in the trial.

The build project stays on `net10.0`, and a comment in the project file says why. It then loads MSBuild from the .NET 10 SDK, which binds `NuGet.Frameworks` 7.9.0, the version Fallout 10.5.0 resolves. This needs an installed .NET 10 SDK; with only SDK 11, evaluation fails because the `net10.0` process cannot load its MSBuild. With the .NET 10 runtime installed, `PackCheck` still passes then. A `net11.0` build project loads MSBuild from SDK 11, which needs a newer version, and project evaluation fails ([Fallout-build/Fallout#731](https://github.com/Fallout-build/Fallout/issues/731), still open).

The pipeline's targets do not trigger this evaluation under Fallout, so a clean log does not show that it works: Fallout 10.4.0 logs nothing although the evaluation fails. Verify it with a temporary target that calls `ProjectModelTasks.Initialize()`, evaluates the project once per target framework, and reports the path of the loaded MSBuild and the evaluated target framework identifier and version. The target is not committed. `Initialize()` must run before the method that uses MSBuild types is compiled ([Fallout-build/Fallout#745](https://github.com/Fallout-build/Fallout/issues/745)).

`Pack` needs the ignored `Chaos.Mongo.snk`, which new clones and worktrees lack, and also verifies packages that earlier runs left in `artifacts/packages`. `Clean` deletes all of `artifacts/`, so move older packages aside if they are still needed.

repo-baseline manages the `.gitignore` block with the `.nuke` rules and the block in `.github/copilot-instructions.md` with the `./.nuke` entry, and `repo-baseline check` fails in CI when they change. Both blocks stay as they are. The `.fallout` rules and the `./.fallout` entry go after the respective block. The remaining `.nuke` rules are harmless and keep existing local `.nuke/` folders ignored. [chA0s-Chris/repo-baseline#45](https://github.com/chA0s-Chris/repo-baseline/issues/45) adds Fallout to the synced content; the local rules are removed once a release with it is synced.

`Fallout.Common` 11.0.x on nuget.org was published by mistake and is unlisted; it must not be used. Removing the NuGet pins should make the open Renovate pull request #133 unnecessary.

No automated test is added. `tests/BuildPipeline.Tests` links only build sources that do not depend on the build framework, so it cannot run the pipeline, and no behavior of those sources changes.

The change is build configuration only, so the pull request carries `skip-changelog` instead of the issue's `enhancement` label.

PR/MR description must include:

- Fallout reads `parameters.<profile>.json` from `.fallout/` instead of `.nuke/`; local profile files must be moved there.

Verified by PR/MR checks:

- The CI workflow passes on the pull request.
