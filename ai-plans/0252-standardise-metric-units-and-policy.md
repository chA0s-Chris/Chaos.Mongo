# Standardise Metric Units and Complete the Compatibility Policy

## Rationale

[#252](https://github.com/chA0s-Chris/Chaos.Mongo/issues/252): the two diagnostics meters disagree on duration
units. `MongoQueueDiagnostics` (`src/Chaos.Mongo/Queues/MongoQueueDiagnostics.cs`) records its three histograms in
milliseconds, while `IntegrityAnchoringDiagnostics`
(`src/Chaos.Mongo.EventStore/Integrity/IntegrityAnchoringDiagnostics.cs`) records seconds, following OpenTelemetry's
convention for duration instruments. `docs/compatibility-policy.md` freezes metric names and tags for 1.x but says
nothing about units, and is silent on three other API-evolution questions that keep coming up: whether a public
interface can gain members, whether a public result enum can gain values, and whether adding a `required` member is
breaking. This change standardises on seconds, documents units as part of the frozen metric contract, and closes the
three policy gaps so both are settled once rather than reopened per PR.

## Acceptance Criteria

- [ ] `MongoQueueDiagnostics`'s three duration histograms (`ProcessingDuration`, `ProcessingQueueAge`,
      `LockRecoveryAge`) register unit `"s"` and record seconds, matching `IntegrityAnchoringDiagnostics`.
- [ ] Unit tests assert, for all five duration histograms across both meters (`ProcessingDuration`,
      `ProcessingQueueAge`, `LockRecoveryAge`, `IntegrityAnchoringDiagnostics`'s `AnchorDelay` and
      `RevocationConfirmationDelay`), that the published `Instrument.Unit` is `"s"` and that a known, deterministic
      input `TimeSpan` is recorded as the equal number of seconds (not the old millisecond value for the three queue
      histograms; a regression guard for the two anchoring histograms, which already record seconds).
- [ ] `docs/queues.md`'s Diagnostics section contains a metrics table (instrument, type, what it measures) listing
      all seven `MongoQueueMetrics.Instruments`, with histogram units stated, in the same shape as the anchoring
      metrics table in `docs/event-store.md`.
- [ ] `docs/compatibility-policy.md`'s stored-data contract bullet names units alongside metric names and tags as
      frozen.
- [ ] `docs/compatibility-policy.md` states that a public interface only grows through members with a default
      implementation; a member without one is breaking.
- [ ] `docs/compatibility-policy.md` states that a public enum used as a result type may gain new values in a minor
      release, and that consumers must not assume its value set is closed.
- [ ] `docs/compatibility-policy.md` states that adding a `required` member to an existing public type is a breaking
      change.
- [ ] `docs/migrating-from-0.x.md` gains an entry for the queue metric unit change, naming the affected instruments
      and the concrete consequence for dashboards/alerts built against the old millisecond values.
- [ ] `dotnet build -c Release Chaos.Mongo.slnx` produces no warnings and the full test suite passes.

## Technical Details

### Metric unit change

In `src/Chaos.Mongo/Queues/MongoQueueDiagnostics.cs`:

- `ProcessingDurationHistogram` (line 12), `QueueAgeHistogram` (line 29) and `RecoveredLockAgeHistogram` (line 34):
  change the `CreateHistogram` unit argument from `"ms"` to `"s"` and reword each description from "in
  milliseconds" to "in seconds".
- `RecordProcessingSucceeded` (line 57) and `RecordLockRecovered` (line 43): change
  `processingDuration.TotalMilliseconds` / `queueAge.TotalMilliseconds` / `lockAge.TotalMilliseconds` to
  `.TotalSeconds`.

`IntegrityAnchoringDiagnostics` already uses `"s"` and `.TotalSeconds` throughout; it is unchanged.

The structured log messages in `src/Chaos.Mongo/Queues/MongoQueueSubscription.cs` (the `{ProcessingDurationMs}` /
`{QueueAgeMs}` entries around lines 307–312 and 383–390) keep logging milliseconds and are not touched: log message
text is explicitly outside the compatibility policy's scope (see "Not covered"), and the issue's proposal is about
metrics, not logs.

### Test coverage

- `tests/Chaos.Mongo.Tests/Queues/QueueMetricsCollector.cs`: add the published instrument's `Unit` to
  `QueueMetricMeasurement` (captured from `MeterListener.InstrumentPublished`/the measurement callback's
  `Instrument` parameter) so tests can assert on it alongside the existing `InstrumentName`, `Value` and `Tags`.
- `tests/Chaos.Mongo.Tests/Queues/MongoQueueSubscriptionTests.cs`,
  `ProcessQueueItemAsync_WithRecoveredLock_EmitsRecoveryAndSuccessDiagnostics` (around lines 618–684): this test
  already fixes `now`, `createdUtc = now - 5 minutes` and `recoveredLockUtc = now - 2 minutes` through the mocked
  `TimeProvider`, which deterministically produces `queueAge = 300s` and `recoveredLockAge = 120s`. Extend its
  assertions to check `Unit == "s"` on the `ProcessingQueueAge`, `LockRecoveryAge` and `ProcessingDuration`
  measurements, and `Value == 300.0` / `Value == 120.0` on the first two. `ProcessingDuration`'s value in this test
  comes from `Stopwatch.GetElapsedTime`, not the mocked clock, so its value is not asserted here.
- New test (e.g. in `tests/Chaos.Mongo.Tests/Queues/MongoQueueDiagnosticsTests.cs`): call the internal
  `MongoQueueDiagnostics.RecordProcessingSucceeded` directly (friend access via the existing
  `InternalsVisibleTo="Chaos.Mongo.Tests"` in `Chaos.Mongo.csproj`) with a known, non-trivial `processingDuration`
  `TimeSpan` (e.g. 2.5 seconds), using `QueueMetricsCollector` to assert the `ProcessingDuration` measurement's
  `Unit == "s"` and `Value == 2.5`. This bypasses `Stopwatch` to give the duration conversion a deterministic,
  exact-value regression test, closing the gap the subscription test's non-deterministic timer leaves.
- `tests/Chaos.Mongo.EventStore.Tests/Integration/AnchoringMetricsCollector.cs`: add the published instrument's
  `Unit` to its measurement tuple the same way as `QueueMetricsCollector`, so anchoring tests can assert on it.
- New test (e.g. in `tests/Chaos.Mongo.EventStore.Tests/Integrity/IntegrityAnchoringDiagnosticsTests.cs`): call the
  internal `IntegrityAnchoringDiagnostics.RecordAnchorDelay` and `RecordRevocationConfirmationDelay` directly
  (friend access via the existing `InternalsVisibleTo="Chaos.Mongo.EventStore.Tests"` in
  `Chaos.Mongo.EventStore.csproj`) with a known `TimeSpan` each, using `AnchoringMetricsCollector` to assert
  `Unit == "s"` and the exact recorded seconds value for both. These two histograms already record seconds and are
  unchanged by this plan, but the issue requires unit/value test coverage for every duration metric, and no such
  coverage exists today; the existing Testcontainers-based `IntegrityAnchoringIntegrationTests` only check
  measurement presence and threshold comparisons, not published unit or an exact value, so a factor-of-1000
  regression there would currently go undetected. A direct call avoids depending on the full anchoring pipeline's
  wall-clock timing.

### Documentation

- `docs/queues.md`, "## Diagnostics" (lines 170–174): add a table with columns `Instrument | Type | Measures`,
  listing all seven `MongoQueueMetrics.Instruments` members (`chaos.mongo.queue.lock.recovered` Counter,
  `...lock.recovery_age` Histogram (s), `...processing.duration` Histogram (s), `...processing.failed` Counter,
  `...processing.queue_age` Histogram (s), `...processing.succeeded` Counter, `...published` Counter), mirroring
  the anchoring metrics table in `docs/event-store.md` (around lines 996–1004).
- `docs/compatibility-policy.md`, "Stored data" (line 17): extend "metric names and tags are contract" to "metric
  names, tags and units are contract".
- `docs/compatibility-policy.md`, "Public API" (lines 7–10): add the three new rules as prose/bullets consistent
  with the page's existing tone — default-implemented-only interface growth, non-exhaustive handling of public
  result enums, and `required` member addition being breaking. These are the exact rules from the issue's proposal;
  no further design decision is needed here.
- `docs/migrating-from-0.x.md`: add a new `##` section (placed near the existing "Queue and outbox option renames"
  section, consistent with that file's one-section-per-breaking-change structure) naming the three affected
  instruments and stating that dashboards, alert thresholds and queries built against the old millisecond values
  must be rescaled or rebuilt against the new unit.
