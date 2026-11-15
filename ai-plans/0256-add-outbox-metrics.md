# Add Outbox Metrics

## Rationale

[#256](https://github.com/chA0s-Chris/Chaos.Mongo/issues/256): the transactional outbox emits no metrics, while
queues (`MongoQueueMetrics`, meter `Chaos.Mongo.Queues`) and integrity anchoring (`IntegrityMetrics`) do. Operators
cannot alert on publish failures, a growing delivery lag, or messages reaching `Failed`, which are the signals that
matter most for an outbox. Metric names, tags and units are frozen once shipped (see
`docs/compatibility-policy.md`), but adding a meter is additive, so this does not block 1.0.

This change adds an outbox meter with five instruments (published, failed attempts, terminal failures, publish
duration, message age), each tagged by destination, following the structure of `MongoQueueDiagnostics` and
`MongoQueueMetrics`. Durations use seconds (`"s"`), the unit standardised by #252 (`ai-plans/0252-*`, already
merged: `MongoQueueDiagnostics` and `IntegrityAnchoringDiagnostics` both record seconds today). It also documents the
metrics, with suggested alerts, in `docs/transactional-outbox.md` and lists them in `docs/compatibility-policy.md`.

## Acceptance Criteria

- [x] A new public `OutboxMetrics` class in `Chaos.Mongo.Outbox` exposes `MeterName`, a nested `Instruments` class
      and a nested `Tags` class with the exact names listed under Technical Details, and its members are added to
      `src/Chaos.Mongo.Outbox/PublicAPI.Unshipped.txt`.
- [x] The processor emits all five instruments through a new internal `OutboxDiagnostics` class, under meter
      `Chaos.Mongo.Outbox`, tagged with `outbox.destination` set to the outbox's configured collection name
      (`OutboxOptions.CollectionName`), the one discriminator registration guarantees to be distinct per outbox.
- [x] Each of the five instruments is covered by a test asserting its instrument name, its tags (key and value) and
      its published `Instrument.Unit` (`"s"` for the two histograms, no unit for the counters, as for the existing
      queue and anchoring counters), together with the recorded value (exact value for histograms).
- [x] A registration-level test shows that outboxes stay distinguishable: two typed outboxes whose marker types share the
      same simple `Name` (declared in different namespaces) and have distinct collections, and the untyped outbox next
      to a typed outbox whose marker is named `Default`, each emit under their own `outbox.destination` value, with
      no measurement merged into another outbox's series.
- [x] Processor-level tests cover the emission points: a successful publish increments `published` and records
      duration and message age once each; a throwing publisher increments `publish.failed` and no `published`; a
      permanent failure (exhausted `MaxRetries`, through a failed publish and through a claim that terminalizes
      directly) increments `publish.terminal_failed` exactly once; a shutdown cancellation, a lost-ownership
      finalization and a lock-release-after-timeout record neither `published` nor `publish.failed`.
- [x] `docs/transactional-outbox.md` has a Metrics section (with a table-of-contents entry) containing a table of
      the five instruments (name, type/unit, what it measures), the tag, and suggested alerts for publish failures,
      terminal failures and delivery lag/backlog, including the limit that the age histogram only observes messages
      that were delivered.
- [x] `docs/compatibility-policy.md` lists the outbox metrics (meter, instrument names, types/units and tag) as
      frozen contract and points to the queue and anchoring metric tables for the other two meters.
- [x] `dotnet build -c Release Chaos.Mongo.slnx` produces no warnings, and the full test suite passes with at least
      95% merged line coverage (`tests/AGENTS.md`), evidenced by a single `bash ./build.sh Test` run started from a
      clean `artifacts/test-coverage`.

## Technical Details

### Public contract (new components)

New file `src/Chaos.Mongo.Outbox/OutboxMetrics.cs`, mirroring `src/Chaos.Mongo/Queues/MongoQueueMetrics.cs` (public,
static, XML-documented, `const String` members; every member also goes into `PublicAPI.Unshipped.txt` in the same
form as the queue entries in `src/Chaos.Mongo/PublicAPI.Shipped.txt`). Exact values:

| Member | Value | Type / unit |
|---|---|---|
| `MeterName` | `Chaos.Mongo.Outbox` | |
| `Instruments.Published` | `chaos.mongo.outbox.published` | Counter, no unit |
| `Instruments.PublishFailed` | `chaos.mongo.outbox.publish.failed` | Counter, no unit |
| `Instruments.PublishTerminalFailed` | `chaos.mongo.outbox.publish.terminal_failed` | Counter, no unit |
| `Instruments.PublishDuration` | `chaos.mongo.outbox.publish.duration` | Histogram, `"s"` |
| `Instruments.MessageAge` | `chaos.mongo.outbox.message.age` | Histogram, `"s"` |
| `Tags.Destination` | `outbox.destination` | |

These strings freeze on release, so the reviewers should treat them as the decision point. The single tag
`outbox.destination` carries `OutboxOptions.CollectionName`. Registration guarantees exactly this value is distinct
per outbox: `MongoBuilderExtensions.Register` rejects two outboxes using the same collection ("both use collection ...",
~line 106-110). It does not make `OutboxOptions.Identity` unique: Identity is `markerType?.Name ?? "Default"` and
registration only rejects the same marker *type* (~line 97-100), so markers sharing a simple name in different
namespaces, constructed generic markers and a marker named `Default` all produce equal identities. Using Identity as
the tag would silently merge distinct outboxes into one series, defeating per-destination alerts. The tag value is
therefore the collection name, in line with the existing `queue.collection` tag; the tag key stays `outbox.destination`
as the issue asks ("tagged by destination"). `OutboxOptions.Identity`, the `OutboxIdentity` log scope and registration
behavior are not changed, and no second tag (`outbox.identity`, `error.type`, result) is added; failure causes belong in
logs. Docs must say that the destination is the collection name.

### Emission points (`src/Chaos.Mongo.Outbox/OutboxProcessor.cs`, plus new internal `OutboxDiagnostics.cs`)

`OutboxDiagnostics` mirrors `MongoQueueDiagnostics` (static `Meter`, instruments, `Record*` methods taking the
destination and values, `TagList` creation) and is `internal`, reached by tests through the existing
`InternalsVisibleTo="Chaos.Mongo.Outbox.Tests"`. Semantics, which the docs must state:

- **`published`, `publish.duration`, `message.age`** are recorded together in `MarkProcessedAsync`, only when the
  state update to `Processed` actually modified the message (`ModifiedCount > 0`). They therefore count messages that
  reached `Processed` exactly once per transition, never a publish whose finalization failed, timed out or lost
  ownership; the redelivery that completes it records them later. Histogram counts equal the counter.
- **`publish.duration`** is the elapsed time of the `publisher.PublishAsync` call alone, measured with
  `_timeProvider.GetTimestamp()`/`GetElapsedTime(...)` (not `Stopwatch`) so tests can advance `FakeTimeProvider`
  from a test publisher and assert an exact value; it is carried into `MarkProcessedAsync`.
- **`message.age`** is the time from `OutboxMessage.CreatedUtc` to the same `now` used for `ProcessedUtc`, i.e. total
  delivery latency including retries and backoff. Clamp to zero when `CreatedUtc` is in the future (writer clock
  skew), so the histogram never records a negative value.
- **`publish.failed`** is recorded in `HandleFailureAsync` before its update is issued, so one attempt resolved as a
  failure counts once: for a throwing `PublishAsync` and for a finalization exception (the two existing callers). It
  is not recorded for shutdown cancellation (`TryReleaseLockAsync` paths), finalization timeout, or lost ownership of
  a successfully published message.
- **`publish.terminal_failed`** is recorded exactly where a message transitions to `Failed`: the permanent-failure
  branch of `HandleFailureAsync` after the update reports `ModifiedCount > 0`, and the `claimed.State ==
  OutboxMessageState.Failed` branch in `ProcessMessageAsync` (claim terminalized directly, no publisher call, so no
  `publish.failed`). The terminal attempt in the first case counts both `publish.failed` and
  `publish.terminal_failed`. Terminal failures are only possible with a finite `MaxRetries`; with the unlimited
  default the instrument stays at zero, which docs should state.
- Counters carry no unit and histograms `"s"`, consistent with `MongoQueueDiagnostics` after #252.

Metric recording must not be able to throw into the processing path beyond what `Counter.Add`/`Histogram.Record`
already guarantee, and must not change query, claim or retry behavior; no change to persisted fields or indexes.

### Tests

Place tests in `tests/Chaos.Mongo.Outbox.Tests`, following `tests/AGENTS.md` (hand-crafted doubles for new tests, no
nested classes, `Method_Scenario_ExpectedResult`, FluentAssertions, integration tests under `Integration/` with
`[Category("Integration")]`).

- New `OutboxMetricsCollector` test helper modelled on `tests/Chaos.Mongo.Tests/Queues/QueueMetricsCollector.cs`
  (a `MeterListener` on `OutboxMetrics.MeterName`, recording instrument name, value, tags and `Instrument.Unit`).
  `OutboxDiagnostics` uses one static meter and measurements arrive on the threads of parallel tests, so the collector
  must be thread-safe (a lock or `ConcurrentQueue`, not a bare `List<T>`) and every assertion filters on a unique
  `outbox.destination` value, i.e. a unique `OutboxOptions.CollectionName` per test.
- Direct tests of the internal `OutboxDiagnostics.Record*` methods for exact name/tag/unit/value of all five
  instruments (precedent: `MongoQueueDiagnosticsTests`).
- Processor-level sociable tests for the emission semantics above, in the style of `OutboxProcessorTests` and
  `OutboxProcessorTerminalClaimTests` (existing `FakeTimeProvider` and collection fakes), covering success, failed
  attempt, both terminal paths, and the "does not record" cases. At least one Testcontainers-based integration test
  in `Integration/` exercises a real publish success and a real terminal failure end to end through the registered
  hosted processor: one case for the default (untyped) outbox and one for a typed outbox, each asserting the destination
  tag equals its collection name.
- The collision regression (see Acceptance Criteria) goes through the public registration API
  (`AddOutbox`/`AddOutbox<TMarker>` as used by existing typed-outbox tests), not through directly assigned internal
  options, so it exercises the real Identity derivation. The two same-named marker types are separate top-level types
  in different namespaces (no nested classes, per `tests/AGENTS.md`). It may use the processor-level fakes rather than a
  container; it asserts the emitted destination values differ and equal each outbox's collection name.
- Because tests assert names that freeze, assert against the literal strings, not only the `OutboxMetrics` constants.

### Documentation

- `docs/transactional-outbox.md`: new `## Metrics` section after `Processing Behavior` (before
  `Event Store Integration`), added to the table of contents. Same table shape as the queue and anchoring metric
  tables (`docs/queues.md` "Diagnostics", `docs/event-store.md` anchoring list): `Instrument | Type | Measures`, with
  histogram units. Then the `outbox.destination` tag and suggested alerts. Suggested alerts, as concrete
  conditions an operator can translate to their backend:
  - sustained `publish.failed` rate above zero, per destination (publisher or broker trouble);
  - any increase of `publish.terminal_failed` (messages parked in `Failed`, needing operator action; they are not
    expired by default);
  - high percentile (for example p95) of `message.age`, or `published` dropping to zero while the application still
    writes messages. State the limit explicitly: `message.age`/`published` only observe delivered messages, so a
    processor that is stopped or fully stalled emits nothing and shows up as an absent/flat series, not a high age;
    pair it with an absence alert or an application-side pending-message count. No backlog gauge is added by this
    change (not requested by the issue; it would need a periodic Mongo query).
  - Note the at-least-once caveat: a redelivered message is counted once when it reaches `Processed`, and
    `publish.failed` also counts a finalization failure after a successful publish, so a failed attempt can precede
    a later success of the same message; failed attempts are not lost messages, only `publish.terminal_failed` is.
  Optionally a one-line pointer from `Best Practices` and from `src/Chaos.Mongo.Outbox/README.md` if it has a
  suitable place; not required.
- `docs/compatibility-policy.md`: the "Stored data" bullet already makes "metric names, tags and units" contract but
  lists none. Add a short `## Metrics` section (before `Tamper-evidence format`) listing the outbox meter's
  instruments, types/units and tag, and referencing `docs/queues.md#diagnostics` and the anchoring table in
  `docs/event-store.md` for the other meters rather than duplicating them.
- `docs/migrating-from-0.x.md`: no entry; the change is additive (no behavior or signature change for existing
  consumers). `CHANGELOG.md` is generated at release and is not edited.

### Verification expectations

Applicable checks follow root `AGENTS.md`: run `./cleanup_code.sh` after the code change and inspect its diff, then
`./inspect_code.sh` (or `--base`) with all warning/error findings fixed, and `dotnet build -c Release Chaos.Mongo.slnx`
with no warnings. The complete suite and the coverage requirement (at least 95% merged line coverage per
`tests/AGENTS.md`) are established by one aggregate entry point, `bash ./build.sh Test` (needs Docker), which runs
every test project and target framework with XPlat coverage and merges the results; remove `artifacts/test-coverage`
first so the merge contains no earlier results. Do not additionally run a bare `dotnet test Chaos.Mongo.slnx` for the
same suite.

### Publication notes

Not implementation criteria: the PR must `Closes #256`, and its description should state the frozen metric names and
tag, that the change is additive, and the `published`/`publish.failed` semantics (counted at `Processed`
transition / per failed attempt) so reviewers of the PR can challenge them before they freeze.
