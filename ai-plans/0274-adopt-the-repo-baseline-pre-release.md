# Adopt the repo-baseline pre-release

> Issue: [#274](https://github.com/chA0s-Chris/Chaos.Mongo/issues/274)

## Rationale

Chaos.Mongo keeps local copies of the code style, ReSharper settings, scripts, build properties and agent instructions that all of the maintainer's .NET repositories share, and these copies have drifted from each other. [repo-baseline](https://github.com/chA0s-Chris/repo-baseline) is now the single source for that content, and Chaos.Mongo pilots it as the published-library repository before its `v1.0.0` release (chA0s-Chris/repo-baseline#4).

Adopting the pinned pre-release replaces the local copies with synced content, reduces the local files to what is specific to this repository, and runs `check` in CI so that shared content cannot drift again. The pilot also verifies how Rider saves edits to the injected settings layer (TOOL-3), and reports problems it finds in repo-baseline back there.

## Acceptance Criteria

- [ ] `.repo-baseline.json` pins `0.1.0` with the profiles `common`, `dotnet`, `dotnet-nuget` and `github`, and `check` against `v0.1.0` reports no findings.
- [ ] A CI job in `.github/workflows/ci.yml` runs `check` with the snippet from the repo-baseline README, and `result` fails unless that job succeeds.
- [ ] The root, `src/` and `tests/` `Directory.Build.props` import `.repo-baseline/Common.props`, `.repo-baseline/Packaging.props` and `.repo-baseline/Tests.props` respectively, and keep only settings that are local or override the shared ones.
- [ ] `Chaos.Mongo.slnx.DotSettings` contains only the injection of `.repo-baseline/ReSharper.DotSettings` and local settings; `.editorconfig`, `.gitignore`, `AGENTS.md` and `.github/copilot-instructions.md` contain only local content beside their managed blocks.
- [ ] The local tool manifest is the root `dotnet-tools.json` (BLD-5); `.config/dotnet-tools.json` is deleted, and no instruction, script, build or workflow file refers to it.
- [ ] Every test class in an `.Integration` namespace carries `[Category("Integration")]` (TST-3), the fast-test command in `AGENTS.md` uses `TestCategory!=Integration&TestCategory!=E2E`, and that filter selects the same tests as the previous `FullyQualifiedName!~.Integration.` filter.
- [ ] `tests/AGENTS.md` states that integration tests live in an `.Integration` namespace and carry `[Category("Integration")]`.
- [ ] There is no direct `PackageReference` or central `PackageVersion` for `Microsoft.TestPlatform.TestHost`. Moq remains a local reference in `tests/Directory.Build.props` with a comment explaining why (TST-1).
- [ ] `renovate.json` extends `github>chA0s-Chris/repo-baseline//renovate/default` and otherwise contains only local rules.
- [ ] The reformatting caused by the shared rules is a commit of its own and passes the Release build and full test suite on its own. `.git-blame-ignore-revs` lists that commit.
- [ ] `./inspect_code.sh --all` reports no findings at warning severity; each former finding is fixed or suppressed with a reason (CS-11).
- [ ] The Release build has no warnings, the full test suite passes, and `bash ./build.sh PackCheck` passes.
- [ ] Before and after adoption, `bash ./build.sh Pack` produces a `.nupkg` and `.snupkg` for each library. License, authors, repository metadata and Source Link mappings are preserved, allowing for expected version and commit changes.
- [ ] The Rider settings-layer results are recorded in this plan and reported on chA0s-Chris/repo-baseline#4.
- [ ] Problems found in repo-baseline's profile content or tool are reported as issues in chA0s-Chris/repo-baseline after the maintainer approved their text.

## Technical Details

### Running the tool

Use a separate repo-baseline checkout at `v0.1.0` outside this repository. Run the commands from that checkout so local runs and CI use the same tool and profile content. `<path>` is the Chaos.Mongo working copy.

```bash
dotnet run tool/sync.cs -- init <path> 0.1.0 --name Chaos.Mongo --type library --forge github --repository-url https://github.com/chA0s-Chris/Chaos.Mongo --target-frameworks net10.0
dotnet run tool/sync.cs -- sync <path>
dotnet run tool/sync.cs -- check <path>
```

`init` skips all existing files. It does not overwrite the ignored release key `Chaos.Mongo.snk`, which must exist in the working copy before `init` runs; otherwise `init` generates a new key that must not replace the one in the `CHAOSMONGO_SNK` secret. `Chaos.Mongo.Public.snk` and the public signing in the root `Directory.Build.props` stay local. The created root `dotnet-tools.json` replaces `.config/dotnet-tools.json`, but keeps the tool versions of the current manifest (the template pins an older ReSharper version). Renovate's NuGet manager matches a root `dotnet-tools.json` by default.

### Drift on `main` at `2a3055b`

Re-verified against the issue (`2a3055b` differs from the measured `1db6f36` only in two workflow files):

| Item | State | Resolution |
| --- | --- | --- |
| Tool manifest in `.config/` (BLD-5) | present | Move to the root; update `AGENTS.md`. |
| Integration category (TST-3) | 76 files in `.Integration` namespaces, 60 with tests, none with the category | Add the category to every test class there; setup fixtures and helpers need none. No guard test; `tests/AGENTS.md` states the rule for new tests. |
| `ArrangeArgumentsStyle="True"` (CS-1) | in the local `Zorn` profile | The local profile is removed; the shared one applies. |
| `Microsoft.TestPlatform.TestHost` (TST-1) | referenced in `tests/Directory.Build.props` and versioned since the initial commit, without a recorded reason | Remove both. |
| Moq (TST-1) | 19 test files use it | Stays a local reference until those tests are migrated. |
| `cleanup_code.sh`, `inspect_code.sh` (TOOL-1) | differ | Replaced by `sync`. |

Further drift that the issue does not list:

- The synced `.props` files are not imported yet. `Common.props` makes warnings errors in CI whatever the configuration (BLD-1) and nullable warnings errors in every configuration (CS-4). The Nuke targets already build Release (`TargetBuildConfiguration` in `build/BuildPipeline.Common.cs`), so only local Debug builds change: nullable warnings fail them, and with `CI=true` all warnings do.
- The root `Directory.Build.props` drops `LangVersion`, `ImplicitUsings`, `Nullable`, `NoWarn`, `PreferredUILang` and `EmbedUntrackedSources`: its plain `<NoWarn>1591</NoWarn>` would overwrite the shared `$(NoWarn);1591`, and `EmbedUntrackedSources` matters only for packages, where `Packaging.props` sets it. `src/Directory.Build.props` drops `GenerateDocumentationFile`, `PackageLicenseExpression` and `Authors`, and `tests/Directory.Build.props` drops the package references that `Tests.props` provides, because duplicates fail the Release build with NU1504. `Packaging.props` also sets `IncludeSymbols` and `snupkg`, which the Nuke pack target already sets; signing is not part of it. For the package comparison, compare the `.nuspec` files and the Source Link JSON read from the PDBs in the `.snupkg` files with a throwaway file-based script and no new tool. `Pack` needs the release key, so this comparison runs only locally.
- Every shared key of the ReSharper layer is duplicated in `Chaos.Mongo.slnx.DotSettings`. The four keys that exist only locally (`WRAP_LIMIT`, the primary-constructor severity, `PreferQualifiedReference`, `QualifiedUsingAtNestedScope`) are expressed by the shared `.editorconfig` block, so the local file keeps the layer injection, added by hand in the form of the repo-baseline template, and the dictionary word `Mongo`.
- `.editorconfig` keeps only a local `[*.cs]` section with the `file_header_template`; all other local keys equal the block.
- `.gitignore` keeps locally `!/.github/scripts/release/`, which must stay after the block's `[Rr]elease/`, plus `.idea` and `Chaos.Mongo.snk`. No tracked file is under a nested `artifacts/` directory, so the block's anchored `/artifacts/` is enough.
- The managed `AGENTS.md` blocks replace the local sections "Implementation rules", "Code Style" (under "General Rules for the Code Base"), "Code Style and Inspections", "Plan Rules" and "Here is Your Space". The local part keeps the project description, general rules, the commands table and the pointers to nested `AGENTS.md` files.
- The local `.github/copilot-instructions.md` content equals its block and is removed.

### Renovate

Keep only the local rules next to the preset: `helpers:pinGitHubActionDigests`, the MongoDB test-image custom manager, the two test-image holds, and the Dashboard approval for the `src/` dependency minimums. The intended differences from the current configuration are the unified group names, the new GitHub Actions group, the two `Microsoft.Testing` packages in the Test SDK group, and no `skip-changelog` label, because release-drafter already excludes `renovate` (chA0s-Chris/repo-baseline#5).

### Commits and merge

The branch keeps these commits apart, in this order:

1. Adoption: tool output, reduced local files, props imports, manifest move, package and Renovate changes.
2. Reformatting: only the output of `./cleanup_code.sh --all` under the new settings, mostly the NUnit member order (CS-13, about 50 files) and explicit object creation (CS-7, about 10 files).
3. `.git-blame-ignore-revs` (new) with the full hash of commit 2.
4. Integration categories.
5. Inspection fixes.
6. The CI job.

If the reformatting commit fails the Release build or the test suite, for example because member reordering moved a static field initializer, fix the cause in a commit before it and regenerate the reformatting commit.

The PR must be merged with a merge commit; a squash or rebase merge changes the hash in `.git-blame-ignore-revs`. If the branch is rebased before merging, update the hash.

### Inspection findings

With the shared settings applied, `inspect_code.sh --all` reports 28 findings at `2a3055b`, all in test projects:

- `AccessToDisposedClosure` (11): `TimestampTokenValidationTests`, `TypedOutboxIntegrationTests`, `TypedOutboxLifecycleTests`, `MongoQueueSubscriptionTests`.
- `SuspiciousTypeConversion.Global` (7): casts to the test doubles `LifecycleDatabase`, `LifecycleCollection` and `LifecycleIndexes` in `TypedOutboxLifecycleTests`.
- `UnusedTypeParameter` (5) and `UnusedMember.Local` (2): type shapes in `CollectionTypeMapTests` and `MongoQueueCollectionNameGeneratorTests`.
- `ClassCannotBeInstantiated` (3): `TypedOutboxLifecycleTests`, `TypedOutboxRegistrationTests`.

Fix a finding when a small, behavior-neutral change removes it; otherwise suppress it with `// ReSharper disable once <InspectionId>` and a comment line with the reason, for example for deliberate test shapes or closures that run before disposal. `inspection.suppressedIds` stays empty.

### CI

Add a job that clones the pinned release into `${RUNNER_TEMP}` and runs `check` as in the repo-baseline README section "In CI". Give the job `contents: read` and use checkout with `persist-credentials: false` and `actions/setup-dotnet` for .NET 10, both pinned by digest. Add the job to `result.needs` and require its outcome to be `success` in `result`.

### Rider settings-layer test (TOOL-3)

The maintainer runs these steps by hand and reports the results, which are recorded here:

1. Open `Chaos.Mongo.slnx` in Rider and confirm under Settings → Manage Layers that the `repo-baseline` layer loads from `.repo-baseline/ReSharper.DotSettings` (the `RelativePath` form with backslashes on Linux).
2. Change a shared setting, such as a typing-assist option, and edit the `Zorn` cleanup profile; save both and record which file each change is written to.
3. Record whether opening or saving writes the `Environment/SettingsMigration/IsMigratorApplied` keys back into the local file.
4. Copy one shared key into `Chaos.Mongo.slnx.DotSettings` and confirm that `check` reports it as redefined.
5. Revert all changes.

Results: pending.

### repo-baseline findings

Candidate issue, to be shown to the maintainer before it is opened: `check` reports no finding when the synced `.props` files are not imported by the repository's `Directory.Build.props` files, so shared build settings can be silently inactive. Verified on a scratch copy of this repository after `init` and `sync`.
