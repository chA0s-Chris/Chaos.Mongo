# Harden the Release Workflow

## Rationale

`.github/workflows/release.yml` interpolates untrusted values with `${{ }}` directly into shell scripts: the signing key secret, the release-drafter body (which contains PR titles) and the `version` input. A backtick or `$(…)` in a PR title therefore runs as a command while the job holds `contents: write` and `pull-requests: write`. The workflow can be started from any branch, never runs the tests, accepts an unvalidated `version` that defaults to the already released `0.1.0`, and pushes the tag, the changelog PR and the GitHub release before it pushes to nuget.org with the long-lived `NUGET_API_KEY`. If that push fails, a released tag without packages is left behind, and a rerun fails on the existing tag. All workflows use floating action tags, and the packages lack a repository URL, use a 2025-only copyright and the single tag `MongoDB` ([#216](https://github.com/chA0s-Chris/Chaos.Mongo/issues/216)).

The goal is a release that only runs from `main` with a valid version, tests before it packs, publishes to nuget.org first through trusted publishing, and can be rerun after any partial failure until every step is done. Every workflow gets SHA-pinned actions maintained by Renovate and job-level least-privilege permissions, and the package metadata is completed.

## Acceptance Criteria

- [ ] No `run:` step in any workflow under `.github/workflows/` contains a `${{ }}` expression; every such value reaches the script through `env:` and is quoted, and multi-line text is written with `printf '%s\n'`.
- [ ] The `version` input has no default. A run from a ref other than `refs/heads/main`, or with a version that does not match the contract in Technical Details, fails in the first job before any write to the repository, the releases, nuget.org or pull requests, and before any secret is used.
- [ ] The release runs in the order: validate, test (the complete suite, including integration tests), pack, push to nuget.org (packages with `--skip-duplicate`, then symbol packages), verify the published packages' commit, tag, GitHub release, changelog PR. Each step only starts after the previous one succeeded.
- [ ] After a failure in any step after the push to nuget.org has started, both "Re-run failed jobs" and a new dispatch with the same version from the same commit finish the missing steps without manual cleanup, including symbol packages whose package was already pushed. Steps that already took effect are detected and skipped rather than failing or creating duplicates.
- [ ] A dispatch fails in the first job, before any side effect, if its version already has a tag (compared case-insensitively) that differs from `v<version>` or does not point to the dispatched commit, or if nuget.org already serves a package of that version from a different commit or with a different version casing.
- [ ] The tag and the GitHub release are only created after nuget.org serves every package of the version with the dispatched commit as its repository commit. A different commit, or no served package after the bounded wait, fails the run before tagging.
- [ ] Packages are pushed with NuGet trusted publishing (`NuGet/login`, OIDC, `id-token: write`). No workflow or build file references `NUGET_API_KEY`.
- [ ] Every `uses:` of a remote action in every workflow is pinned to a full 40-character commit SHA followed by a `# vX.Y.Z` comment, and `renovate.json` makes Renovate pin and update these digests.
- [ ] Every workflow sets `permissions: {}` at the workflow level and grants each job only the permissions listed in Technical Details.
- [ ] The release logic is covered by a committed automated shell test suite that runs in `ci.yml` and passes. It uses hand-crafted doubles for `git`, `gh`, `curl` and `dotnet` and covers the scenarios listed in Technical Details.
- [ ] For each of the three packages, the nuspec of a Release `dotnet pack` contains `<repository type="git" url="https://github.com/chA0s-Chris/Chaos.Mongo" commit="…">`, the package-specific tags, and a copyright range starting in 2025 and ending in the build year. The `Chaos.Mongo.EventStore` description mentions tamper evidence.
- [ ] `dotnet build -c Release Chaos.Mongo.slnx`, the complete test suite and `bash ./build.sh Test` succeed.
- [ ] After merge, the owner has deleted the `NUGET_API_KEY` repository secret and revoked the key on nuget.org. This is an owner action that the implementation cannot perform; it stays unchecked until the owner confirms it.

## Technical Details

### Job structure and permissions

`release.yml` is split into jobs chained with `needs:`. The split is what gives each job its own permissions, keeps the signing key and the OIDC token away from jobs that do not need them, and lets "Re-run failed jobs" reuse the outputs and artifacts of jobs that already succeeded. The workflow sets `concurrency: { group: release, cancel-in-progress: false }` so two releases never race.

| Job | Permissions | Secrets | Responsibility |
| --- | --- | --- | --- |
| `prepare` | `contents: read`, `pull-requests: read` | none | Validation, preflight checks, release notes |
| `test` | `contents: read` | `DOCKERHUB_USER`, `DOCKERHUB_PAT` | Calls `ci.yml` |
| `pack` | `contents: read` | `CHAOSMONGO_SNK` | `bash build.sh Pack`, uploads the packages |
| `publish` | `contents: read`, `id-token: write` | none | Trusted publishing push to nuget.org |
| `release` | `contents: write` | none | Commit verification, tag and GitHub release |
| `changelog` | `contents: write`, `pull-requests: write` | none | Changelog branch and PR; stable versions only |

`ci.yml` gains a `workflow_call` trigger with the two Docker Hub secrets declared, and `release.yml` calls it as a local reusable workflow, so the release runs exactly the pipeline CI runs rather than a copy that can drift. A local `uses: ./.github/workflows/ci.yml` cannot and need not be SHA-pinned. `ci.yml` keeps `contents: read` on its job; `update-draft.yml` moves its `contents: write` and `pull-requests: read` from the workflow to its job. Only the `release` and `changelog` jobs push, so all other checkouts use `persist-credentials: false`. The `publish` job checks out only to run its script, and it does not run any build.

### Release scripts and their tests

Decision: the shell logic of `prepare`, `publish`, `release` and `changelog` moves from inline `run:` blocks into new bash scripts under `.github/scripts/release/`. The workflow steps call these scripts and pass every value through `env:`. This is what makes the rerun and rejection behavior testable at all. The repository has no GitHub Actions harness, and the .NET test pipeline cannot exercise shell logic. The scripts use `set -euo pipefail` and only the tools available on `ubuntu-latest` (`bash`, `git`, `gh`, `curl`, `dotnet`).

A new runner, `.github/scripts/release/tests/run.sh`, runs each script against hand-crafted doubles for `git`, `gh`, `curl` and `dotnet`, placed first on `PATH`. The doubles replay fixture state and record every call. No mocking framework or extra test tool is added. `ci.yml` runs the suite as a step before `bash build.sh Test`, so both pull requests and the release's `test` job run it. The suite asserts which write calls happen, and that no duplicate or destructive writes (force push, release overwrite, asset clobber) happen. The cases are:

- An invalid version or a non-`main` ref is rejected with zero write calls. This includes each invalid value listed under Validation.
- An existing tag with the same or a different commit, or with a different casing, is handled.
- A served nuspec with the same commit, a different commit, a different version casing, or a 404 is handled.
- Packages already pushed: the package push is skipped as a duplicate, but the symbol packages are still pushed.
- Nuspec visibility is delayed, then succeeds. Visibility times out. A mismatched commit appears after the push.
- The tag exists but the release does not. A draft release exists with an asset missing. A published release exists with an asset missing.
- A stable version whose changelog heading already exists on `origin/main`. A changelog branch that exists but has no PR. A branch whose PR already exists. A prerelease, which skips the changelog.
- Release notes and changelog text containing `` ` ``, `$(…)`, `"` and `%` arrive verbatim, and nothing executes.

### Validation and preflight (`prepare`)

The first steps fail the run unless `GITHUB_REF` is `refs/heads/main` and the version matches this exact contract:

```text
^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-((0|[1-9][0-9]*|[0-9]*[a-zA-Z-][0-9a-zA-Z-]*)(\.(0|[1-9][0-9]*|[0-9]*[a-zA-Z-][0-9a-zA-Z-]*))*))?$
```

The match covers the entire value with bash `[[ $VERSION =~ $re ]]`, not a line-oriented tool such as `grep`. An API-dispatched input can contain newlines, and `grep` would accept `1.2.3` followed by any further line. This is the official SemVer 2.0.0 expression without the build-metadata part. Decision: build metadata is rejected because nuget.org ignores it for package identity, so `1.0.0+a` and `1.0.0+b` would collide and `--skip-duplicate` would silently skip the second release. It also keeps "contains `-`" a correct prerelease test, which drives `prerelease`, `latest` and the changelog condition. A leading `v` is rejected; the workflow adds it to the tag.

Preflight then rejects every conflict that is already observable, so that a rerun only completes the release of the same commit (`GITHUB_SHA`):

- Remote tags are compared case-insensitively, because NuGet versions are case-insensitive. A matching tag must be exactly `v<version>`, and the commit it peels to must equal `GITHUB_SHA`.
- If nuget.org already serves a package version, its nuspec `<version>` must equal the input exactly, and the `commit` attribute of its `<repository>` element must equal `GITHUB_SHA`. The nuspec is public at `https://api.nuget.org/v3-flatcontainer/<lowercase-id>/<lowercase-version>/<lowercase-id>.nuspec`, and the published 0.9.0 nuspec already carries this attribute. Check every packable project under `src/` rather than a hard-coded list.

A 404 only means the version is absent from the read API. It does not prove the version is unclaimed, because nuget.org accepts a push before validating and indexing it. That gap is closed after the push (see Rerunnable steps), not in preflight. Validation and preflight only read, so a rejection here has no side effects by construction.

### Release notes

The notes are computed once in `prepare` and passed on as a file artifact, never through a `run:` expression. If a published, non-draft GitHub release `v<version>` already exists (a rerun after the release step succeeded), its body is reused; otherwise release-drafter computes it. Recomputing would be wrong in that case, because the drafter would then compare against the new release and produce "No changes since last release".

release-drafter runs with `dry-run: true` and `commitish` set to `GITHUB_SHA`. Dry-run is supported by release-drafter v7 and still sets the `body` output. It needs only read permissions and no longer rewrites the `vNext` draft that `update-draft.yml` maintains; that draft is refreshed by the next push to `main`, for example the changelog PR merge. `ReleaseNotes.md`, which Nuke `Pack` reads into `PackageReleaseNotes`, keeps its current shape: `# v<version> (<date>)`, a blank line, then the body.

### Rerunnable steps

Each step after the push detects its own completed state, so a rerun skips it instead of failing:

- `pack` decodes the key with `printf '%s' "$CHAOSMONGO_SNK" | base64 --decode > Chaos.Mongo.snk` only after tests passed. It runs `bash build.sh Pack --release-version "$VERSION"`, which keeps the full-signature verification from #215, and uploads `artifacts/packages/*.nupkg` and `*.snupkg`.
- `publish` gets a short-lived key from `NuGet/login` with `user: chAos-Chris` (letter O, unlike the GitHub account `chA0s-Chris`), the nuget.org owner of all three packages; it is a public profile name, not a secret. The key reaches the script through `env:`. The script first pushes every `.nupkg` with `--no-symbols --skip-duplicate`, then pushes every `.snupkg` explicitly. The NuGet client only pushes a neighboring symbol package when the main package was actually accepted, so it skips the symbols on a retry after a duplicate; the separate pass closes that gap. A symbol push counts as done if the server accepts it or rejects it as a duplicate. Any other failure fails the job. A rebuilt symbol package from the same commit matches the original assemblies, because Nuke packs with `ContinuousIntegrationBuild`.
- `release` first verifies the publication. It polls every package's flat-container nuspec, with a bounded wait of about 30 minutes, until each one is served. It requires that each nuspec's `commit` equals `GITHUB_SHA` and its `<version>` equals the input. A mismatch fails immediately. A timeout fails the run, and "Re-run failed jobs" resumes the wait. Known limit: suppose an earlier run at another commit had nuget.org accept a package but failed before indexing, and the version is then re-dispatched from a moved `main` within the indexing window. That dispatch can push its remaining packages before this check refuses to tag. nuget.org does not expose accepted but unindexed packages and does not allow deletion, so the workflow can only stop before the tag and report the mismatch.
- After the verification, `release` creates and pushes the annotated tag `v<version>` at `GITHUB_SHA` only if the remote tag does not exist. The job must end with a published, non-draft release with the correct prerelease or latest flag. If no release exists, it is created with `--verify-tag`, the notes file and these flags, without assets. An existing draft, which an interrupted `gh release create` leaves behind, is completed and published rather than treated as done. In every case only the assets missing from the release are uploaded, and existing ones are never replaced, so a new dispatch does not overwrite the originally published packages. Use the `gh` CLI instead of `softprops/action-gh-release`, because these existence checks need explicit control.
- `changelog` is skipped for prereleases. It does nothing if `CHANGELOG.md` on `origin/main` already contains the `## v<version> ` heading. It pushes `release/changelog-v<version>` only if that remote branch does not exist, and opens the PR only if none exists for the branch. Plain `git` replaces `EndBug/add-and-commit`, whose `new_branch` push fails when the branch already exists. The header-preserving insertion and the `[skip ci]` commit message stay as they are.

Text is only ever written with `printf '%s\n' "$VALUE"` from `env:` variables, never with `echo "${{ … }}"`.

### Trusted publishing rollout

These are owner actions outside the repository, and the PR description must list them:

1. Before the first release, create a nuget.org trusted publishing policy for repository owner `chA0s-Chris`, repository `Chaos.Mongo` and workflow file `release.yml`, without an environment.
2. After merge, delete the `NUGET_API_KEY` repository secret and revoke the key on nuget.org.

The Nuke `Publish` target and its `NUGET_APIKEY` parameter are a separate, local publishing path. They are not part of the release workflow and stay unchanged.

### Action pinning and Renovate

Pin each remote action to the commit that its most recent full release tag in the major version currently used peels to (for annotated tags, the `^{}` entry of `git ls-remote --tags`), with the tag as a trailing comment, for example `actions/checkout@<sha> # v7.0.1`. This covers checkout, setup-dotnet, docker/login-action, release-drafter, and the newly added `NuGet/login` and upload/download-artifact actions. No major version changes. Add the `helpers:pinGitHubActionDigests` preset to `renovate.json`, which makes Renovate pin newly added actions and keep the SHA and version comment of existing ones up to date.

### Package metadata

All of this goes in `src/Directory.Build.props` and the three project files:

- `PublishRepositoryUrl=true`, with `RepositoryUrl` set explicitly to `https://github.com/chA0s-Chris/Chaos.Mongo` and `RepositoryType=git`. The explicit URL keeps the nuspec independent of the cloning remote, such as a `.git` suffix or a fork. SourceLink (`Microsoft.SourceLink.GitHub`, already referenced) keeps supplying the commit.
- `Copyright © 2025-$([System.DateTime]::UtcNow.Year) Christian Flessa`. Decision: the end year is computed, so the range never goes stale again. Source-file headers are out of scope.
- `PackageTags` keeps `MongoDB` in the shared props, and each project appends its own tags. Tags contain no spaces. The following are illustrative, derived from the package READMEs: core `migrations;distributed-lock;queue;transactions;dependency-injection`; EventStore `event-sourcing;event-store;aggregates;tamper-evidence;hash-chain;rfc3161`; Outbox `outbox;transactional-outbox;messaging`.
- The `Chaos.Mongo.EventStore` description adds opt-in tamper evidence: hash-chained event streams anchored with RFC 3161 timestamps.

### Validation

Besides the committed release-script suite, validate the following:

- A throwaway script outside the repository parses every workflow. It fails on `${{` inside any `run:` value, on a remote `uses:` without a 40-hex SHA, and on missing workflow-level `permissions: {}`. Also run `actionlint` and `shellcheck` if they are available.
- The version check accepts `1.2.3`, `1.2.3-rc.1` and `1.0.0-alpha-1`. It rejects `v1.2.3`, `1.2`, `01.2.3`, `1.2.3+build`, `1.2.3-`, `1.2.3-01`, the empty string and a multi-line value whose first line is valid. These cases belong to the committed suite.
- Run the real preflight nuspec lookup once against the published `Chaos.Mongo` 0.9.0 (commit `7d538ee…`) and against an unpublished version.
- Run a Release `dotnet pack` of each project (public-signed; the private key is not needed) to a temporary directory and inspect the nuspec metadata.
- Run the build and test commands from the acceptance criteria, because the props changes affect every build.

The end-to-end release against real GitHub and nuget.org is first exercised by the owner's next real release.
