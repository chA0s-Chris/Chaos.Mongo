# Run packaging checks in CI and make CI work for forks

## Rationale

`ci.yml` currently only runs `build.sh Test`. Packaging is never exercised outside
the release workflow, the mandatory Docker Hub login breaks every fork PR because
forks don't receive repository secrets, the coverage numbers that
`BuildPipeline.Test.cs` prints are not enforced, the workflow has no timeout or
concurrency control, and all MongoDB integration tests run only against the
floating `mongo:8` tag that is duplicated across three `MongoDbTestContainer`
copies. See [#217](https://github.com/chA0s-Chris/Chaos.Mongo/issues/217) for the
full background and the owner's decisions, which this plan follows directly.

## Acceptance Criteria

- [ ] A PR opened from a fork completes CI successfully (no step requires
      `DOCKERHUB_USER`/`DOCKERHUB_PAT` or `CHAOSMONGO_SNK` to succeed).
- [ ] CI fails when packaging is broken: a new Nuke target packs every `src/`
      project with public signing (no private key involved), runs .NET package
      validation on the result, and asserts every packed assembly's public key
      token equals the released token. Its output never lands in the directory
      `Publish` globs, so a public-signed package can never reach `Publish`.
- [ ] `Test` clears `coverlet.xml`-driven coverage data before merging, passes
      `coverlet.xml` as the Coverlet settings file, and the merged
      `coverage.cobertura.merged.xml` contains line-rate data for
      `Chaos.Mongo`, `Chaos.Mongo.EventStore`, and `Chaos.Mongo.Outbox`, and none
      for any `*.Tests` assembly.
- [ ] Running `Test` without `CHAOSMONGO_MONGODB_IMAGE` set, or with it set to
      `Primary`, fails when merged aggregate line coverage drops below 95% or
      any one of the three packages' own line coverage drops below 90%,
      comparing the unrounded values, and reports the measured values either
      way. Running `Test` against `MinimumSupported` or `Latest` never
      enforces either threshold, matching the issue's "gate runs on Primary"
      decision.
- [ ] Automated regression tests exercise the new enforcement logic directly,
      independent of a full `Test` run against a real MongoDB: the merged-
      coverage threshold comparison at and immediately below both the 95%
      aggregate and the 90% per-package boundary, a per-package failure masked
      by a passing aggregate, and `PackCheck`'s public-key-token check against
      both a correctly and an incorrectly signed sample package.
- [ ] Every job in `ci.yml` has `timeout-minutes: 15`.
- [ ] Pushing a new commit to an open PR cancels that PR's in-progress CI run.
      Pushes to `main` and runs started by the release workflow are never
      cancelled by another such run.
- [ ] The full `Test` suite passes with the MongoDB server pinned to each of the
      three constants in the new `MongoDbTestImages` class (minimum supported,
      primary, latest), including any integration test adjusted for this plan.
- [ ] The three pinned `mongo:` image tags exist only as the three constants in
      `MongoDbTestImages`; no other file hard-codes a Mongo image tag,
      including the two `benchmarks/` projects that start their own MongoDB
      container today and the `netem/Dockerfile` that
      `Chaos.Mongo.EventStore.LoadTests`' three-node topology builds (its
      `FROM` becomes a build argument supplied from
      `MongoDbTestImages.Primary`, with no hard-coded default). Each constant
      carries its own Renovate
      `datasource=docker`/`depName`/`versioning=docker` comment with a
      distinct `depName` per constant, resolving to the same `mongo` Docker
      Hub package, and `renovate.json`'s custom manager under `tests/`
      extracts all three so the Dependency Dashboard lists them as three
      separate detected dependencies, not merely as config that matches
      locally.
- [ ] A GitHub ruleset on `main` requires a single, name-stable CI status check
      to succeed before merging, readable back via the GitHub API.

## Technical Details

### Fork-safe packaging check (`build/BuildPipeline.NuGet.cs`, `build/BuildPipeline.Signing.cs`)

Add a new Nuke target, `PackCheck`, that depends on `Restore` only (not on
`Pack`'s `AssertReleaseKeyFileExists`, which requires the release private key).
It reuses `ClearSourceBuildOutputs()` and then runs `DotNetPack` for every
project under `SourceDirectory` with:

- An output directory distinct from `PublishDirectory` (e.g.
  `ArtifactsDirectory / "packages-check"`), so `Publish`'s
  `PublishDirectory.GlobFiles("*.nupkg")` can never see these packages.
- No `AssemblyOriginatorKeyFile`/`PublicSign` overrides — the root
  `Directory.Build.props` already defaults every ordinary build to public
  signing with the committed `Chaos.Mongo.Public.snk`, which is what a
  secret-less CI run must use.
- `EnablePackageValidation=true` (plus `EnableStrictModeForCompatibleTfms`/
  equivalent default validation properties), so a broken package layout is
  caught even though a single-TFM, no-baseline package has little to compare
  against yet.

After packing, verify every assembly in every produced package: its SHA-1
public-key-token (reuse `GetPublicKeyToken`) must equal the existing
`ReleasedPublicKeyToken` constant. This does not need the full strong-name
signature verification `VerifyPackageSignatures` performs for `Pack` (a
public-signed assembly has no usable signature); only the token check applies
here. Factor the token/assembly-enumeration logic shared with
`VerifyPackageSignatures` as needed, but keep `Pack`'s existing full signature
check unchanged — it stays release-only.

Extract the token check into a method that can be exercised without running
`DotNetPack`, and add regression tests that feed it both a correctly
public-signed sample assembly and one built with a different key/token, so the
pass and fail paths are both covered without needing a full packaging run.

### CI workflow (`.github/workflows/ci.yml`)

- Add a workflow-level `concurrency` block:
  `group: ${{ github.workflow }}-${{ github.event.pull_request.number || github.run_id }}`,
  `cancel-in-progress: ${{ github.event_name == 'pull_request' }}`. Because the
  group falls back to the unique `run_id` outside of `pull_request` events, pushes
  to `main` and the release workflow's call never share a group with anything
  that could cancel them.
- Add `timeout-minutes: 15` to every job.
- Map `secrets.DOCKERHUB_USER`/`DOCKERHUB_PAT` to job-level `env`, and guard the
  existing "Authenticate to Docker Hub" step with
  `if: env.DOCKERHUB_USER != ''`. Fork PRs simply skip the login and docker then
  pulls `mongo` anonymously; this is the only tolerated behavior change for forks
  per the issue (occasional rate-limit reruns are acceptable, no mirror).
- Add a `strategy.matrix` over the three `MongoDbTestImages` constant names
  (`MinimumSupported`, `Primary`, `Latest`) to the existing test job. Pass the
  selected constant name to the test process through an environment variable,
  e.g. `CHAOSMONGO_MONGODB_IMAGE: ${{ matrix.mongo-image }}`.
- Add a separate `pack-check` job (no Docker Hub login, no `CHAOSMONGO_SNK`) that
  runs `bash build.sh PackCheck`.
- Move the existing "Test the release scripts" step into its own lightweight job
  (it already runs fully against local doubles, with no Docker/network/.NET
  dependency) so it does not triplicate across the new Mongo-version matrix.
- Add a final job, e.g. `result`, with `if: always()` and
  `needs: [ci, pack-check, release-scripts]` (the three jobs above), that fails
  if any dependency failed or was cancelled. This job's name is stable across
  matrix legs and is the one the branch ruleset requires.
- Leave the `workflow_call` secrets inputs as currently declared; `release.yml`
  (the only caller) still supplies both, so nothing about the reusable-workflow
  interface needs to change.

### Coverage gate (`build/BuildPipeline.Test.cs`, `coverlet.xml`)

- At the start of `Test`, clear `CoverageDirectory` before running
  `DotNetTest`, so a stale prior run's per-project `coverage.cobertura.xml`
  files are never merged in.
- Add `.SetSettingsFile(RootDirectory / "coverlet.xml")` to the shared
  `DotNetTest` settings, so the merge now reflects the filtered assemblies
  instead of being driven by whatever Coverlet's defaults pick up.
- Replace `coverlet.xml`'s contents with the filters the issue specifies
  exactly (both `[Chaos.Mongo]*` and `[Chaos.Mongo.*]*` in `Include`, so the
  core assembly and the two extension packages are all covered; a
  `[Chaos.Mongo*.Tests]*` `Exclude`; `ExcludeByAttribute` for
  `GeneratedCodeAttribute,ExcludeFromCodeCoverageAttribute`; `ExcludeByFile`
  for `**/obj/**/*.cs`). Deliberately omit `CompilerGeneratedAttribute` from
  `ExcludeByAttribute`, unlike the ShadowDrop reference this is modeled on, so
  that code inside lambdas/closures (e.g. the transaction callbacks in
  `MongoHelperExtensions`) keeps counting.
- Extend `ReportTestCountAndCoverage` to also parse each `<package name=...
  line-rate=...>` element of the merged cobertura file (in addition to the
  root `<coverage line-rate=...>` it already reads), and compare the unrounded
  `line-rate` values against two thresholds: 95% for the aggregate and 90% for
  every individual package. Collect any threshold violation and `Assert.Fail`
  with the specific package(s)/values involved after printing the existing
  summary, so a failing run still reports the measured numbers.
- Only enforce these two thresholds when `CHAOSMONGO_MONGODB_IMAGE` is unset
  (the ordinary local run) or equals `Primary`; a run against
  `MinimumSupported` or `Latest` still computes and prints the coverage but
  never fails on it. This keeps the gate on the version the issue designates
  (Primary) instead of also failing the compatibility legs on coverage
  differences between MongoDB versions.
- Extract the two threshold comparisons into a method that takes the already-
  parsed aggregate/per-package line rates (not the file or the enforcement
  flag), so regression tests can feed it synthetic values at and immediately
  below each boundary, including a per-package failure alongside a passing
  aggregate, without running `dotnet test` or starting MongoDB.

### MongoDB server version matrix (`tests/MongoDbTestImages.cs`, `tests/Directory.Build.props`, `benchmarks/Directory.Build.props`, three `MongoDbTestContainer.cs` files, the two benchmark files that start a container, `benchmarks/Chaos.Mongo.EventStore.LoadTests/netem/Dockerfile`, `renovate.json`)

- Add `tests/MongoDbTestImages.cs` with a public static class holding three
  `public const String` fields — `MinimumSupported` (currently 7.0.x, the
  oldest version still vendor-supported), `Primary` (currently 8.0.x, today's
  default and the version the coverage gate and `PackCheck` run under), and
  `Latest` (currently 9.0.x) — each preceded by its own
  `// renovate: datasource=docker depName=<role> versioning=docker` comment
  using a distinct, role-specific `depName` per constant (e.g.
  `mongo-minimum-supported`, `mongo-primary`, `mongo-latest`) plus
  `packageName=mongo` so Renovate still resolves updates against the real
  `mongo` Docker Hub package while treating the three constants as
  independently matchable dependencies, pinning the exact current patch tag
  for each (verify the current tags against Docker Hub at implementation time;
  this plan does not fix a specific patch number). Add a `Selected` property
  that reads `CHAOSMONGO_MONGODB_IMAGE` from the environment and maps the
  three constant names to their values, defaulting to `Primary` when the
  variable is unset or unrecognized — this keeps the existing local-run
  behavior (`bash build.sh Test` without the variable still uses the current
  default version).
- Link the one file into all three test projects by adding a `<Compile
  Include>` of it to `tests/Directory.Build.props` (the common import all
  three `*.Tests.csproj` already pull in), following the pattern the issue
  cites from ShadowDrop. Do not duplicate the file. Give the class a
  namespace that is not specific to any one test project (e.g.
  `Chaos.Mongo.TestSupport`), since it is now also compiled into the two
  `benchmarks/` projects below.
- Change each of the three `MongoDbTestContainer.cs` copies'
  `.WithImage("mongo:8")` call to `.WithImage(MongoDbTestImages.Selected)`.
  Keep the rest of each copy (including the EventStore copy's
  `enableTestCommands=1` and the shared `GLIBC_TUNABLES`/`Ulimit` workarounds)
  unchanged.
- Add the same `<Compile Include>` of `tests/MongoDbTestImages.cs` to
  `benchmarks/Directory.Build.props`, and change the two existing
  `new MongoDbBuilder("mongo:8")` calls
  (`benchmarks/Chaos.Mongo.EventStore.Benchmarks/EventStoreAppendBenchmarks.cs`
  and `benchmarks/Chaos.Mongo.EventStore.LoadTests/MongoTopology.cs`) to
  `new MongoDbBuilder(MongoDbTestImages.Primary)`. Benchmarks are not part of
  the CI version matrix and don't read `CHAOSMONGO_MONGODB_IMAGE`, so they use
  the constant directly rather than `.Selected`.
- `MongoTopology.StartThreeNodeAsync` builds a third, separate image from
  `netem/Dockerfile` (via `ImageFromDockerfileBuilder`) to add the `iproute2`
  package on top of the Mongo image, and that Dockerfile's `FROM mongo:8` is
  not covered by the `MongoDbBuilder` replacements above. Change the
  Dockerfile to declare `ARG MONGO_IMAGE` with no default value, before
  `FROM ${MONGO_IMAGE}`, and drop the literal tag from its descriptive
  comment. A direct `docker build` of this Dockerfile without
  `--build-arg MONGO_IMAGE=...` is not a supported path; the only supported
  path is `MongoTopology`, which always supplies the argument. Add
  `.WithBuildArgument("MONGO_IMAGE", MongoDbTestImages.Primary)` to the
  `ImageFromDockerfileBuilder` chain in `StartThreeNodeAsync`, matching the
  `.Primary`-direct approach used for the other two benchmark call sites.
  Leave the builder's own local cache tag (`chaos-mongo-loadtest-netem:8`)
  unchanged — it names the derived image Testcontainers builds and caches
  locally, not an upstream Mongo Hub tag, so it carries no Renovate
  obligation.
- In `renovate.json`, add a custom regex manager that matches the
  `datasource=docker depName=<role> versioning=docker` comment/constant pairs
  in `tests/MongoDbTestImages.cs`, and an `ignorePaths` override that
  reproduces Renovate's current `config:recommended` default ignore list minus
  `**/tests/**` (verify the exact current default list from Renovate's
  published configuration at implementation time — copying it incorrectly
  would silently stop unrelated dependency updates under `tests/`, or fail to
  exclude paths the defaults intentionally skip elsewhere). Add `packageRules`
  entries matching each role-specific `depName` that restrict
  `MinimumSupported` and `Primary` to their respective `7.0.x`/`8.0.x` line
  via `allowedVersions` (a regex such as `/^7\.0\./` or `/^8\.0\./`, not just
  `dependencyDashboardApproval`, which only delays opening a PR and does not
  stop either line from being bumped to a newer minor); leave `Latest`
  unrestricted, still gated only by the
  existing top-level `major.dependencyDashboardApproval` rule for major
  version bumps. Confirm after implementation, via Renovate's config
  validator and a dry-run/lookup against the three depNames (or the resulting
  Dependency Dashboard issue once available), that all three are actually
  detected as separate dependencies — matching the custom manager's regex
  locally is not sufficient evidence on its own.
- Some tests, most plausibly `BulkWriteOptimizationIntegrationTests` (today
  only ever run against `mongo:8`), may behave differently against 7.0.x or
  9.0.x. Diagnose and fix any such failure that surfaces once the matrix runs
  against all three versions, in the affected test or production code,
  without broadening the change beyond what compatibility requires.

### Required status check (GitHub ruleset on `main`)

This is a repository setting, not a file in the repository. Using the
already-authenticated `gh` CLI, create (or update, if one targeting `main`
already exists) a branch ruleset on `main` whose `required_status_checks`
rule names the stable `result` job from `ci.yml` as a required check context.
Read the created/updated ruleset back via the API as evidence it matches
what's required, and link it to this issue for traceability. `main` currently
has no branch protection and no rulesets (confirmed via `gh api
repos/.../branches/main/protection` → 404 and `gh api
repos/.../rulesets` → `[]`), so this plan adds a new ruleset rather than
editing an existing one.
