# Releasing

This guide is for the maintainer. It covers the steps of a release that the Release workflow does not take on its own.

## Ship the public API

Each `src/` project declares its public API in `PublicAPI.Shipped.txt` and `PublicAPI.Unshipped.txt`. Pull requests
add new API to the unshipped file and mark removed API there with `*REMOVED*`. When a release ships that API, fold the
unshipped file into the shipped one:

```bash
bash build.sh FoldPublicApi
```

The target adds every unshipped entry to `PublicAPI.Shipped.txt`, deletes the entries marked `*REMOVED*`, sorts the
shipped entries ordinally and leaves only `#nullable enable` in `PublicAPI.Unshipped.txt`. If a removed entry is not
shipped or an added entry already is, it fails and changes no file.

Fold before every stable release, and before the release candidate that freezes a major version's API. Merge the
result through a pull request before dispatching the release, so the tagged commit carries the folded files.

## Run the Release workflow

Dispatch the **Release** workflow from `main` in GitHub Actions and enter the version without a leading `v`, for
example `1.2.3` or `1.2.3-rc.1`. A version with a prerelease part is published as a prerelease.

The workflow checks the version and existing tags and packages, runs the complete CI workflow including `pack-check`,
packs and signs the packages, pushes them to nuget.org and creates the tag and the GitHub release. A rerun after an
interruption completes the missing steps. For a stable release, it also opens a pull request that adds the release
notes to `CHANGELOG.md`; review and merge it.

## Update the package validation baseline

With `PackageValidationBaselineVersion` set in `src/Directory.Build.props`, package validation compares each package
with that version. The property is not set before 1.0.0;
[#307](https://github.com/chA0s-Chris/Chaos.Mongo/issues/307) adds it. After every stable release is published to
nuget.org, set it to that version, so the next release is checked against it. Package validation downloads the baseline
package, so the version must be on nuget.org first.

Package validation then fails `Pack` and `PackCheck` on any breaking change against the baseline. When a new major
version breaks API on purpose, record those breaks in the project's `CompatibilitySuppressions.xml`, which
`dotnet pack -p:ApiCompatGenerateSuppressionFile=true` generates. When the baseline moves to that major version, delete
the file or generate it again, because its entries no longer apply.

## Release 1.0

1. Release `1.0.0-rc.2` once the code issues of the 1.0 milestone are closed. Fold the public API again
   first if any `PublicAPI.Unshipped.txt` has gained entries.
2. Fix what rc.2 turns up. New or removed API goes into `PublicAPI.Unshipped.txt` as usual; fold it again before
   1.0.0.
3. Release `1.0.0`.
4. Set `PackageValidationBaselineVersion` to `1.0.0`, tracked in
   [#307](https://github.com/chA0s-Chris/Chaos.Mongo/issues/307).
