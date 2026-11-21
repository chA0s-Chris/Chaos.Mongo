# Finish the 1.0 Release Mechanics and Test the Latest Driver in CI

> Issue: [#254](https://github.com/chA0s-Chris/Chaos.Mongo/issues/254)

## Rationale

The packages declare `MongoDB.Driver` 3.9.0 as their minimum, and that is the only driver version CI tests. A manual
trial with 3.13.0 passed, but nothing would catch a later driver release that breaks the library. The steps that turn
a release candidate into a stable release are also untracked: folding `PublicAPI.Unshipped.txt` into
`PublicAPI.Shipped.txt` and setting `PackageValidationBaselineVersion` afterwards are written down nowhere.

The goal is to close both gaps, freeze the 1.0 public API in the API files, and ship it as `v1.0.0-rc.2`, the last
release candidate before 1.0.

## Acceptance Criteria

### Latest driver in CI

- [x] A new CI job runs the complete suite with the latest-tested `MongoDB.Driver` on the `Primary` MongoDB image
  for every framework in `MainTargets`, and the `result` job requires it. The existing `ci` matrix keeps testing the
  3.9.0 minimum.
- [x] The latest-tested driver version is pinned in exactly one place in the repository. Renovate proposes minor and
  patch updates to it without Dependency Dashboard approval. Major updates stay held for approval by the
  repo-baseline preset, so moving the job to a new driver major is a deliberate decision.
- [x] The new Renovate custom regex manager matches the pin line. This is checked locally, for example with
  `renovate-config-validator` and a regex test against the file.
- [x] Packages produced by `Pack` and `PackCheck` still declare `MongoDB.Driver` `>= 3.9.0`.
- [x] `docs/requirements.md` states that CI tests both the minimum and the latest-tested driver.

### Release mechanics

- [x] A new Nuke target folds each `src/` project's `PublicAPI.Unshipped.txt` into its `PublicAPI.Shipped.txt`,
  applying `*REMOVED*` entries, and fails without changing any file when a removed entry is absent from the shipped
  file.
- [x] `BuildPipeline.Tests` covers the fold logic, including additions, removals, the failure case and the header.
- [x] The API files are folded for 1.0: every `PublicAPI.Unshipped.txt` contains only `#nullable enable`, and
  `dotnet build -c Release Chaos.Mongo.slnx` passes without RS0016 or RS0017.
- [x] A new release document describes how to cut a release: when to run the fold target, dispatching the Release
  workflow, setting `PackageValidationBaselineVersion` after a stable release, and the rc.2-then-1.0.0 order for
  this milestone. `CONTRIBUTING.md` links to it.
- [x] A follow-up issue tracks setting `PackageValidationBaselineVersion` to `1.0.0` once 1.0.0 is published.

### After merge (maintainer)

- [ ] `v1.0.0-rc.2` is released through the Release workflow, and that run's `pack-check` job passes.

## Technical Details

### Driver override

Keep the `PackageVersion` item for `MongoDB.Driver` in `Directory.Packages.props` unchanged so that it remains the
packed floor and stays under the existing "src dependency minimums" Renovate rule.

The pinned latest-tested version (currently 3.14.0) is a new MSBuild property in `Directory.Packages.props`, next to
the minimum and its comment. A Renovate custom regex manager updates it, following the pattern used for
`tests/MongoDbTestImages.cs`. Because that manager is not `nuget`, the dependency-dashboard hold on the minimum does
not apply to it. The repo-baseline preset's `major.dependencyDashboardApproval` still does.

An opt-in override applies the pinned version only when a boolean MSBuild property is set, for example
(illustrative):

```xml
<PackageVersion Update="MongoDB.Driver" Version="$(LatestTestedMongoDriverVersion)"
                Condition="'$(UseLatestTestedMongoDriver)' == 'true'" />
```

The Nuke `Test` pipeline gets a new boolean switch that sets that property, so CI and local runs read the same pin.
It must reach `Restore`, `BuildTests` and `Test` consistently, since `BuildTests` and `Test` run with
`--no-restore`. `Pack`, `PackCheck` and `Build` never set it. The new CI job reuses the `ci` job's steps (Docker Hub
login, both SDKs) with `CHAOSMONGO_MONGODB_IMAGE=Primary` and a framework matrix kept in sync with `MainTargets`.
`release.yml` reuses `ci.yml`, so the new job also gates every release. Because the version is pinned, that gate is
deterministic.

### API fold

Put the fold logic in a new pure static class under `build/`, called by a new Nuke target, so that
`BuildPipeline.Tests` can test it without the file system. For each project:

- Shipped keeps the `#nullable enable` header. Unshipped additions are added, and each `*REMOVED*<entry>` deletes
  `<entry>` from Shipped.
- Shipped entries are written in ordinal order, so the output is deterministic. The first fold therefore also
  reorders the currently unsorted shipped files once.
- Unshipped is reset to the header only.
- If any removed entry is missing from Shipped, the target fails before writing anything.

### Release document and baseline

The new release document sits beside `CONTRIBUTING.md`, which links to it, and is written for the maintainer. Its rule
for the baseline: after a stable release is published to nuget.org, set `PackageValidationBaselineVersion` in
`src/Directory.Build.props` to that version. Package validation needs that version on nuget.org first, so the 1.0.0
baseline cannot be set in this change. The follow-up issue gets no milestone.

Tagging rc.2 is the last step of the milestone. It happens after this change is merged, by dispatching `release.yml`
with `1.0.0-rc.2`, which runs `ci.yml`, including `pack-check`, before packing. The fold is done in this change so
that rc.2 ships the frozen 1.0 surface. Any API added between rc.2 and 1.0.0 goes into `PublicAPI.Unshipped.txt` and is
folded again at 1.0.0. #209 (net11.0) is already closed, so nothing waits on it.
