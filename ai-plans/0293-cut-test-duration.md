# Cut the Duration of the Complete Suite and the CI Test Job

> Issue: [#293](https://github.com/chA0s-Chris/Chaos.Mongo/issues/293)

## Rationale

The 1.0 milestone added many tests, mostly to `Chaos.Mongo.EventStore.Tests`, and the suite now runs long. The Nuke `Test` target runs every project and target framework one after another, so each `ci` leg pays the sum of all runs, about 4 minutes per framework and twice that since net11.0. No test project enables NUnit parallelism, and a few hot spots dominate: two tests spend most of their time generating large CRLs, three isolated child-process tests start a second MongoDB container each, fixed delays add about 35 s, and per-test databases are never dropped. A local net10.0 solution run on `606201f` took 225 s wall time for 292 s of summed test time; the issue records the full measurement.

The goal is a CI test job close to one `Chaos.Mongo.EventStore.Tests` run and a faster local suite, without weakening what the tests verify. The work stays one issue because every part serves the same outcome and is judged by the same measurement. It is delivered as a stack so that low-risk layers can merge first, while fixture parallelism, the one change likely to cause flaky tests, is reviewed and validated on its own at the top.

## Acceptance Criteria

### Layer 1: Pipeline

- [x] The Nuke `Test` target runs different test projects concurrently, while the target frameworks of one project still run one after another. Every run still writes its JUnit result file named by `TestResultFiles.GetFileName`, the coverage merge and the `CODE_COVERAGE` line still work, a missing result file still fails the report, and a failing test run fails the target.
- [x] A new Nuke parameter restricts `BuildTests` and `Test` to one target framework. Without it, both targets cover every framework the test projects declare. A framework that a test project does not declare fails the target instead of silently running fewer tests.
- [x] `BuildTests` builds all test projects in one `dotnet build` invocation, without building the benchmarks.
- [x] The `ci` job's matrix runs every MongoDB image on every target framework (six legs), each leg passing its framework to the Nuke parameter. The coverage gate is enforced on each `Primary` leg, `result` still aggregates the `ci` legs, and `release.yml`, which calls `ci.yml`, keeps working.
- [x] Pure logic added to the build pipeline, such as validating the requested framework, has tests in `BuildPipeline.Tests`.
- [x] Three consecutive local `bash ./build.sh Test` runs pass on all target frameworks without retries. Any Docker `Conflict ... container ... is not running` failure is reported with its cause.
- [ ] The pull request records the local `bash ./build.sh Test` wall time and the per-assembly times before and after this layer.

### Layer 2: Test Hot Spots

- [ ] `TestTimestampAuthority` produces a CRL with 230,000 revoked certificates in under 1 s locally, and the measurement is recorded in the pull request. The produced CRL is accepted by the same production checks as before (signature by the authority's root, CRL number, issuer), and a test proves that its encoding is a valid CRL signed by the root that lists the requested entries.
- [ ] `RunAsync_EvidenceWellBeyond16Megabytes_CompletesAnchorWithoutEmbeddingIt` and `RunAsync_EvidencePartiallyStoredBeforeAFailure_CompletesTheAnchorOnALaterRun` keep their CRL sizes and authorities and still pass.
- [ ] Tests run through `IsolatedConventionTestProcess` start no MongoDB container of their own; the child process uses the parent's container and never stops it. The isolated tests keep running in a separate process.
- [ ] The fixed delays listed in Technical Details no longer coordinate tests: they are replaced by explicit synchronization, bounded polling or shorter configured intervals (for example a shorter migration lease). Delays that assert that something does *not* happen within a window remain, with windows as short as the assertion allows.
- [ ] The pull request records the durations of the changed tests and the per-assembly wall times before and after this layer.

### Layer 3: Databases

- [ ] Integration tests drop every database they create, including when a test fails. After a complete run of a test project, the shared container holds no database created by tests. This is checked automatically at the end of the run, and a leftover database fails the test run (verified once by deliberately leaking a database).
- [ ] Configuring once per fixture is evaluated for fixtures whose tests all use identical options (for example `EventStoreIntegrationTests` and `AggregateRepositoryIntegrationTests`), and adopted only if the project's wall time drops by at least 10 % across two runs each with and without the change, and no state is shared between tests. The measurement, or why no fixture qualifies, is recorded in the pull request.
- [ ] A tmpfs mount for the MongoDB data directory is measured. It is adopted only if the `Chaos.Mongo.EventStore.Tests` wall time drops by at least 10 % across two runs each with and without tmpfs, and the replica-set and change-stream tests keep passing; the result is recorded in the pull request either way.
- [ ] The pull request records the per-assembly wall times before and after this layer.

### Layer 4: Parallelism

- [ ] `Chaos.Mongo.EventStore.Tests` runs fixtures in parallel. Every remaining `[NonParallelizable]` has a comment naming the shared state that requires it.
- [ ] Fixture parallelism is measured for `Chaos.Mongo.Tests` and `Chaos.Mongo.Outbox.Tests` and enabled in a project only if its wall time drops by at least 10 % across two runs each with and without it; the result is recorded in the pull request.
- [ ] Three consecutive local `bash ./build.sh Test` runs pass on all target frameworks with the `Primary` image, and one run each with `CHAOSMONGO_MONGODB_IMAGE=MinimumSupported` and `Latest`, all without retries.
- [ ] All six `ci` legs pass on the published head.
- [ ] The pull request records the per-assembly wall times before and after the stack, locally and of the CI `ci` job.
- [ ] The `ci` job's `timeout-minutes` is lowered to about two to three times the measured duration of the slowest `ci` leg on the published head, with the reason in a comment.
- [ ] `./cleanup_code.sh` has been run and its diff inspected, `./inspect_code.sh` reports no warning or error findings for the changes, the Release build has no warnings, and `bash ./build.sh Test` passes with the coverage gate met.

## Technical Details

### Stack Design

The layers have little code dependency on each other; their order is chosen by risk and measurement. Each layer merges after the one below it and is timed against it, so the timings in each pull request isolate that layer's effect, and the risky parallelism layer comes last.

1. `0293-cut-test-duration-pipeline` — run test projects concurrently in Nuke, build the tests once, and split the target frameworks into the CI matrix.
2. `0293-cut-test-duration-hot-spots` — remove the large-CRL generation cost, the second container of isolated child processes, and the fixed delays; depends on the pipeline layer for its baseline timings and CI evidence.
3. `0293-cut-test-duration-databases` — drop per-test databases, and measure per-fixture configuration and tmpfs; depends on the hot-spots layer because child processes then share the parent's container and must drop their databases too.
4. `0293-cut-test-duration-parallel` — enable NUnit fixture parallelism and record the cumulative measurements; depends on all lower layers, which remove the delays, extra containers and database growth that would otherwise skew or destabilize parallel runs.

Every layer measures the same way: `bash ./build.sh Test` on all target frameworks with the `Primary` image, two runs per measurement point, recording the target's wall time and the duration of each project and framework run. The first "before" is measured on the stack's trunk commit, not on `606201f`, which only anchors the issue's measurement; after that, a layer's "after" is the next layer's "before". The adoption experiments (per-fixture configuration, tmpfs, and parallelism in the two smaller projects) time the affected project alone on one framework. Implementation creates no pull requests, so each layer's numbers go into the implementation handoff and are copied into its pull request at submission; the "pull request records" criteria are checked then. CI evidence and the timeout change need the published head: the six green `ci` legs, the CI wall times and the `timeout-minutes` change stay unchecked during implementation and are completed after publication, the timeout change as a follow-up commit on the top layer.

### Pipeline

The comment in `build/BuildPipeline.Test.cs` keeps the two framework runs of one project sequential: in the #209 trial, running both frameworks of `Chaos.Mongo.Outbox.Tests` at the same time failed tests with Docker's `Conflict ... container ... is not running`. Keep that rule. Different projects already run concurrently in every local `dotnet test Chaos.Mongo.slnx`. Either run one sequential chain per project in parallel, or run one parallel batch per framework; Nuke's `DotNetTest` accepts a degree of parallelism for combined settings. If the Docker flake appears, find its cause instead of adding retries. It appeared once in about 19 full runs during the stack review: the MongoDB container of `Chaos.Mongo.Tests` stopped while Testcontainers initiated the replica set, and Docker kept no trace of why. Neither 120 concurrent container starts through Docker nor 150 through the test setup in six concurrent test processes reproduced it, so the cause is still unknown. A failed container start now reports the container's exit code and last log lines, so the next occurrence names its cause.

The XPlat collector writes each run's `coverage.cobertura.xml` into its own subdirectory of `CoverageDirectory`, so concurrent runs do not collide. `GetTargetFrameworks` stays the source of the framework list; the new parameter (name to be chosen in Nuke style, for example `TestFramework`) filters it. The coverage gate is evaluated per leg as today, so a `Primary` leg gates its framework's coverage; the code under test is identical across frameworks.

One `dotnet build` over the test projects needs a single entry point that excludes `benchmarks/` and `build/`, for example a solution filter or traversal project (new). Building the whole `Chaos.Mongo.slnx` would also build the benchmarks.

The repository ruleset requires the `result` check, not the individual `ci` legs, so renaming the legs to include the framework is safe. `release.yml` calls `ci.yml` through `workflow_call` and inherits the matrix.

The matrix lists the frameworks statically and is kept in sync with `MainTargets` by hand, like the `setup-dotnet` version list next to it. A comment at the matrix and one at `MainTargets` in `Directory.Build.props` point at each other. Layer 1 also corrects the comment above `timeout-minutes` in `ci.yml`, which describes both frameworks running in one leg; the value changes only in layer 4.

### Large CRLs

`TestTimestampAuthority.BuildRevocationList` uses `CertificateRevocationListBuilder`, whose `Build` takes about 7 s for 230,000 entries. `TestTimestampAuthority` already encodes CRLs directly with `AsnWriter` and signs them with the root's key in `BuildSignedCrl`, which the constructor already reaches through `BuildCrlWithExtensions` for roots without `cRLSign`. Start from that encoder instead of adding a second one, make it fast enough for 230,000 entries, and match the version, signature algorithm, issuer, validity and extensions that the builder emits today (production checks the CRL number among the extensions in `CertificateRevocationListScope`). Use the direct encoder at least for large entry counts; small CRLs may keep the builder. The remaining ~20 s of the 16 MB test are in the production path that stores, verifies and resolves the evidence; record the breakdown in the pull request so the maintainer can decide on a separate issue.

### Isolated child processes

`IsolatedConventionTestProcess` (shared, linked into every test project) starts `dotnet test` for one `[Explicit]` test. Its child runs the assembly's `MongoAssemblySetup`, which starts a new replica-set container. Pass the parent's connection string to the child through an environment variable (new), and let the child's setup and `MongoDbTestContainer` (one copy per test project) use it instead of starting a container; the child must not dispose a container it does not own. Tests currently read `MongoDbContainer.GetConnectionString()` from the container instance, so the isolated tests need a way to obtain the connection string without one. Non-integration isolated tests (`FieldNameStabilityTests`, `MongoLockDocumentTests`, `MongoQueueItemTests`, `MongoMigrationHistoryItemTests`, `OutboxSerializationSetupTests`) start no container and stay as they are.

Two isolated cases, `Queue_RetentionUnderCamelCaseConvention_IsolatedCase` and `Outbox_RetentionUnderCamelCaseConvention_IsolatedCase`, set `ttlMonitorSleepSecs` to 1 with `setParameter`, which is server-wide. On the parent's container that would change the server in the middle of the run, depending on test order. Start the `Chaos.Mongo.Tests` and `Chaos.Mongo.Outbox.Tests` containers with `--setParameter ttlMonitorSleepSecs=1` instead, remove the command from the two cases, and check that no test in those projects relies on the 60 s default.

### Fixed delays

The known fixed delays: `IntegrityAnchoringIntegrationTests` (two 3 s delays and a 1 s delay), `AnchorCandidateSourceTests` (two 2 s delays), the 6 s delay in `TestMigrations` that outlasts the lease in `MongoMigrationConcurrencyTests`, the 2 s delay in `MongoQueueIntegrationTests.Queue_WithEmptyQueue_DoesNotCrash`, and the delays in `MongoHelperExtensionsIntegrationTests` and `MongoQueueRetryIntegrationTests`. Classify each as coordination or negative-assertion window before changing it.

### Databases and tmpfs

Most fixtures name a database `<Prefix>_<Guid>` per test and build a service provider in `[SetUp]`; `LedgerStoreContext` does the same for the EventStore integrity tests and is disposed in the fixtures' `[TearDown]`, which never drops its database. The end-of-run check can live in each project's `MongoAssemblySetup` before the container is stopped. The check runs only in the process that owns the container; a child process using the parent's container skips it. `RestrictedRoleIntegrationTests` starts its own container with the fixed database `RestrictedRoleTestDb` and is outside the shared container's check. The mongo image declares `/data/db` as a volume; a Testcontainers tmpfs mount at that path replaces it.

### Parallelism

Use `[assembly: Parallelizable(ParallelScope.Fixtures)]` and a `LevelOfParallelism` suited to the four vCPUs of a hosted runner, which also runs the other projects concurrently after layer 1. Audit for process-wide state: `Chaos.Mongo.EventStore.Tests` has 9 files that register `BsonClassMap`, conventions or serializers, one that sets an environment variable, and about 10 fixed database names; `Chaos.Mongo.Tests` and `Chaos.Mongo.Outbox.Tests` have fewer. Also audit state that is server-wide on the shared container, such as `setParameter` and fail points; the one fail point in `ChangeStreamAnchorCandidateSourceTests` is scoped to an application name. Prefer making a fixture isolated (unique names, no global registration) over `[NonParallelizable]` where that is reasonable. Validate with the local runs the criteria list, which cover all frameworks and all three images.
