# Keep anchoring and sealing running past a malformed event document

## Rationale

[Issue #236](https://github.com/chA0s-Chris/Chaos.Mongo/issues/236) reports that reading an event's identity during
anchoring and sealing uses throwing BSON conversions. `ChangeStreamAnchorCandidateSource.TryCreateCandidate`
(`.AsGuid` / `.ToInt64()` on the stream's aggregate identifier and version) and its recovery scan in `ScanAsync`
(the same `.AsGuid` conversion on the next stream's identifier) run against a single database-wide change stream
and a single per-run scan shared by every protected aggregate type, so one malformed document throws and stops
anchoring for all of them. `EventStreamSealingSweep<TAggregate>.FindNextStreamAsync` has the same throwing
conversion scoped to one aggregate type's sweep, so a malformed document there stops sealing for that type's
whole collection instead of just that one stream. Separately, `AnchorEncoding.EncodeLeaf` throws when a
candidate's `_integrity.Hash` is not 32 bytes; `EventIntegrityChain.TryRead` validates that `Hash` is generic
binary data but not its length, so a short hash reaches `EncodeLeaf` during `AnchoringRun.PrepareAsync` and
aborts the whole run after candidates were already read.

Tamper evidence is meant to hold against someone with database write access. Today, writing one malformed
document (wrong-typed `AggregateId`/`Version`, or a short `_integrity.Hash`) lets such a person stall anchoring
and/or sealing indefinitely, and the anchor delay metric can go silent exactly when this happens because the read
itself fails before it is recorded. The goal is to read candidates tolerantly, skip and report malformed
documents instead of throwing, and keep the anchor delay metric accurate while a malformed document exists.

## Acceptance Criteria

- [ ] A change-stream batch containing one malformed insert/update/replace (wrong-typed `AggregateId`, wrong-typed
      `Version`, or a short `_integrity.Hash`) is skipped; other candidates in the same batch, and later batches,
      are still returned and the batch is still acknowledged.
- [ ] The recovery scan's next-stream lookup skips a stream whose `AggregateId` field has an unexpected type and
      continues scanning the remaining streams of that aggregate type and the other registered types; a stream
      head whose `Version` field has an unexpected type is skipped the same way without stopping the scan.
- [ ] `EventStreamSealingSweep<TAggregate>.RunPassAsync` skips a stream whose `AggregateId` field has an
      unexpected type and continues the pass over the remaining streams of that collection, instead of the whole
      pass failing and retrying from the same point indefinitely.
- [ ] A candidate whose `_integrity.Hash` is not exactly 32 bytes is treated as malformed and skipped at the same
      point as a wrong-typed `AggregateId`/`Version`, instead of reaching `AnchorEncoding.EncodeLeaf`.
- [ ] Each skipped document produces one log entry identifying the collection and, when readable, the aggregate
      identifier, and is counted in a `Counter<Int64>` metric on the `Chaos.Mongo.EventStore.Integrity` meter, for
      both the anchoring candidate source and the sealing sweep. The anchoring metric is documented in
      `docs/event-store.md` alongside the existing anchor delay metrics; the sealing sweep's skip metric is
      documented next to `StreamsFailed` in the same file.
- [ ] `AnchoringRun`'s anchor delay (`chaos.mongo.eventstore.anchoring.delay` / `..delay_exceeded`) still reflects
      the oldest unread/unanchored candidate while a malformed document exists among newer candidates, instead of
      stopping once that document is reached, verified through `IntegrityAnchoringService.RunOnceAsync`/the
      `chaos.mongo.eventstore.anchoring.delay` histogram, not only through candidate delivery.
- [ ] A document skipped during anchoring/sealing because its `AggregateId` or `Version` could not be read still
      surfaces as a `MalformedEvent` finding from `CheckCompletenessAsync` after the skip. A document skipped
      because its `_integrity.Hash` is not exactly 32 bytes (its identity is otherwise readable) still fails
      `VerifyStreamAsync`/`VerifyEvent`'s hash check after the skip — it is not expected to become a
      `CheckCompletenessAsync`/`MalformedEvent` finding, since `CheckCompletenessAsync` classifies by readability
      of the identity, not by hash content. Neither skip hides the tampering from verification.
- [ ] Integration tests insert a raw document with a wrong-typed `AggregateId`, a wrong-typed `Version`, and a
      short `_integrity.Hash` (as applicable per scenario) alongside well-formed events of other streams, and
      assert that anchoring and sealing still make progress on the well-formed streams.
- [ ] The Release build (`dotnet build -c Release`) has no warnings, and the full test suite passes.

## Technical Details

### Tolerant reads of the aggregate identifier and version

Reuse the existing tolerant-read pattern from `CompletenessChecker.ReadSummary` (`src/Chaos.Mongo.EventStore/Integrity/CompletenessChecker.cs:234-245`):
an aggregate identifier is only valid when the stored value `is BsonBinaryData { SubType: BsonBinarySubType.UuidStandard } binary`, read with
`binary.ToGuid(GuidRepresentation.Standard)`; a version is validated with the existing `StoredVersion.TryRead` (`src/Chaos.Mongo.EventStore/Integrity/StoredVersion.cs`).

Add a small internal helper next to `StoredVersion`, e.g. `StoredAggregateId.TryRead(BsonValue value, out Guid aggregateId)`, implementing the
same check, so `ChangeStreamAnchorCandidateSource` and `EventStreamSealingSweep<TAggregate>` share one implementation instead of duplicating the
`BsonBinaryData` pattern match.

Use it to replace the throwing conversions:

- `ChangeStreamAnchorCandidateSource.TryCreateCandidate` (`fullDocument[type.AggregateIdElement].AsGuid` and `fullDocument[type.VersionElement].ToInt64()`,
  currently lines 147-148): on a failed read, return `null` (no candidate), the same outcome as today's "unsealed insert" case, and report the skip
  (see below) with the reason (bad aggregate identifier, bad version, or bad hash).
- `ChangeStreamAnchorCandidateSource.ScanAsync`'s stream head read (`head[type.VersionElement].ToInt64()`, currently line 454): on a failed read, do not
  add a candidate for this stream, but still advance `cursor`/`examined` exactly as the existing "no integrity data" branch does, so the scan still
  progresses past this stream.
- `ChangeStreamAnchorCandidateSource.ScanAsync`'s next-stream read (`next[type.AggregateIdElement].AsGuid`, currently line 444) and
  `EventStreamSealingSweep<TAggregate>.FindNextStreamAsync` (`next?[EventDocumentFields<TAggregate>.AggregateId].AsGuid`, currently line 222) both use
  this value as the next stream's identifier and as the cursor the *following* lookup excludes via the existing
  `{ AggregateId: { $gt: cursor } }` filter (`ProtectedStreamTypeOfT.NextStream` / `SealingSweepQueries<TAggregate>.NextStream`), sorted by
  `{ AggregateId: 1, Version: 1 }`. A document whose `AggregateId` cannot be read as a `Guid` has no value that can be safely substituted into that
  filter: MongoDB's query comparison operators (`$gt`/`$gte`/`$lt`/`$lte`), including inside `$expr`, compare the filter operand's own BSON
  type/value directly and do not reproduce `$sort`'s array-unwinding behavior — a document whose `AggregateId` is itself a BSON array sorts by its
  *minimum element* (ascending), not by the array value as a whole. A filter built from the malformed value's raw BSON content can therefore both
  wrongly exclude already-processed healthy streams and wrongly re-admit ones already passed, breaking forward progress, independently of whether
  the raw value is protected from expression evaluation (e.g. via `$literal`).

  Avoid comparing the malformed value at all. When the lookup's result is malformed, re-run the *same* `{ AggregateId: { $gt: <last known-good Guid
  cursor> } }` filter and sort, with an added `.Skip(n)` (`n` starting at 1, the count of consecutive malformed documents already observed
  immediately after that cursor), incrementing `n` and retrying until a document with a readable `AggregateId` is found or the query returns none.
  This relies only on the database's own `$sort` order — already correct for every BSON type the malformed value might hold, including arrays — and
  never on comparing the malformed value itself, so it needs no `$expr`/`$literal` handling and no claim about canonical BSON comparison order for
  arbitrary stored values. Only a successfully read `Guid` is ever assigned to the method's persisted cursor (`ScanPosition.Cursor` /
  `IntegritySealingState.Cursor` stay `Guid?`), which also resets `n` to zero for the next lookup; the skip count is local to one lookup call and is
  never persisted. `n` is bounded by the number of consecutive malformed documents immediately after the cursor (expected to be small, since each
  one requires a separate malicious/corrupt write), not by collection size, so the normal (no malformed document) case is an unchanged `Skip(0)`
  lookup.
- `SealingSweepQueries<TAggregate>.NextStream`/`ProtectedStreamType(<T>).NextStream` only build the `SweepQuery`'s filter/sort/projection; the two
  call sites (`ChangeStreamAnchorCandidateSource.ScanAsync:433-435`, `EventStreamSealingSweep<TAggregate>.FindNextStreamAsync:214-220`) each already
  apply `.Limit(1)` on the `IFindFluent` chain themselves (`events.Find(nextQuery.Filter).Sort(nextQuery.Sort).Project(nextQuery.Projection)
  .Limit(1)`). The retry only needs `.Skip(n)` added to that same chain at both call sites when re-running the lookup after a malformed result;
  `SweepQuery`, `NextStream`, and `SealingSweepQueries` need no change, and the malformed value is never passed into any filter.

### Malformed `_integrity.Hash`

`EventIntegrityChain.TryRead` (`src/Chaos.Mongo.EventStore/Integrity/EventIntegrityChain.cs:178`) validates that `Hash` is generic binary data but not
its length, so a short hash currently reaches `AnchorEncoding.EncodeLeaf` → `RequireHash` during `AnchoringRun.PrepareAsync`, well after candidates were
read and reduced to stream heads, and throws there. Validate `integrity.Hash.Length == AnchorEncoding.HashLength` at the same point as the other
malformed-field checks, in `TryCreateCandidate` and in `ScanAsync`'s stream head read, treating a short/long hash as malformed the same way as a
bad `AggregateId`/`Version`. `AnchorEncoding.RequireHash` keeps throwing as a defense-in-depth invariant check; after this change it should not be
reachable with a malformed candidate from these two call sites.

### Reporting skipped documents

Add a new metric to the anchoring meter (`src/Chaos.Mongo.EventStore/Integrity/IntegrityMetrics.cs` / `IntegrityAnchoringDiagnostics.cs`), e.g.
`chaos.mongo.eventstore.anchoring.malformed_candidates_skipped` (`Counter<Int64>`), tagged with the reason (`AggregateId`, `Version`, or `Hash`) the
same way `RevokedTimestampsCounter` is tagged with the authority name. `ChangeStreamAnchorCandidateSource` logs a warning identifying the collection
and, when it could be read, the aggregate identifier, then records the counter, for every skip in `TryCreateCandidate`, `ScanAsync`'s stream head read,
and its next-stream retry loop. Document the new instrument in `docs/event-store.md`'s existing anchoring metrics table (next to
`chaos.mongo.eventstore.anchoring.delay_exceeded`, at line 1002) and mention it in the "Failures" bullet there.

For the sealing sweep, `EventStreamSealingSweep<TAggregate>.RunPassAsync`'s catch block around `SealStreamAsync` already logs and increments
`IntegritySealingState.StreamsFailed` for a stream that cannot be sealed (`src/Chaos.Mongo.EventStore/Integrity/EventStreamSealingSweep.cs:103-119`);
keep incrementing and persisting `StreamsFailed` (via `SaveProgressAsync`) the same way when `FindNextStreamAsync`'s retry loop skips a document
whose `AggregateId` cannot be read, so the existing completion/status semantics (`IsCompleteWithoutFailures`) still cover this case. `StreamsFailed`
is a persisted pass-status counter, not a meter instrument, so it does not by itself satisfy the issue's "a log entry and a metric" requirement for
sealing: also record the same `malformed_candidates_skipped` counter from `IntegrityAnchoringDiagnostics` for this skip, tagged `AggregateId` the
same way the anchoring side is tagged (reuse the one shared instrument rather than adding a second one, since both sides report the same kind of
event). Log a warning identifying the collection and aggregate identifier (when readable) before incrementing, mirroring the existing
`SealStreamAsync` failure log. Update the existing documentation of `StreamsFailed` (`docs/event-store.md:1187`) to state that it also counts
documents whose identity could not be read, not only streams that failed while being sealed, and document the shared
`malformed_candidates_skipped` counter being emitted from both anchoring and sealing in the metrics table update above.

### Anchor delay while a malformed document exists

`AnchoringRun.LastReadOldestCandidateUtc` and `CandidateRead.OldestUtc`/`NewestUtc` (`src/Chaos.Mongo.EventStore/Integrity/AnchoringRun.cs:335-372`) are
computed from `batch.Candidates`, which already excludes documents `TryCreateCandidate` could not convert. Once the tolerant reads above stop
`ReadBatchAsync` from throwing on a malformed document, the oldest-candidate tracking used for the anchor delay metric (recorded by the container that
calls `AnchoringRun.RunAsync`) naturally keeps reflecting the oldest remaining readable candidate instead of stopping at the malformed one; no change
to the delay computation itself is needed beyond the tolerant reads making the run reach it.

### Tests

- `tests/Chaos.Mongo.EventStore.Tests/Integration/ChangeStreamAnchorCandidateSourceTests.cs` already uses a `MongoDbTestContainer` and inserts/mutates raw
  `BsonDocument` events (see `ReadBatchAsync_SealedEventReplaced_YieldsCandidate`). Add cases that insert a malformed raw document (wrong-typed
  `AggregateId`, wrong-typed `Version`, short `_integrity.Hash`) alongside a well-formed stream of another aggregate, both for a live change-stream
  batch and for a recovery scan (reusing the existing recovery-triggering helpers in that file), and assert the well-formed stream's candidate is
  still returned. Include a recovery-scan case with **two consecutive** malformed documents (any wrong-typed `AggregateId`, e.g. a BSON array or a
  BSON string) immediately after the cursor, followed by at least one healthy stream beyond them in aggregate-identifier order, to exercise the
  `Skip(n)`-based retry advancing past more than one malformed document (`n` reaching 2) before finding the next readable stream.
- `tests/Chaos.Mongo.EventStore.Tests/Integration/IntegritySealingIntegrationTests.cs` already covers `RunPassAsync` against a real sweep and database
  (see `RunPassAsync_MalformedIntegrityOnSealedPrefix_SkipsStreamAndSealsLaterStreams`, which asserts on `SealingPassOutcome`, `StreamsFailed`, and the
  other streams' sealed/verified state). Add an analogous case with a wrong-typed `AggregateId` on one stream, with at least one further healthy
  stream beyond it in aggregate-identifier order, and assert `RunPassAsync` still completes and seals the other streams, `StreamsFailed` is
  incremented, and the new `malformed_candidates_skipped` counter records a measurement for the sealing skip (using the existing metrics-collector
  pattern, e.g. `AnchoringMetricsCollector`). `tests/Chaos.Mongo.EventStore.Tests/Integrity/EventStreamSealingSweepTests.cs` keeps its existing
  in-memory `GetDelayAfter` unit tests unchanged; it has no database/sweep fixture and is not the right place for this case.
- `tests/Chaos.Mongo.EventStore.Tests/Integrity/AnchorEncodingTests.cs`: no change expected; `RequireHash` keeps its existing direct-unit-test coverage
  as the defense-in-depth check.
- `tests/Chaos.Mongo.EventStore.Tests/Integration/IntegrityAnchoringIntegrationTests.cs`: add a case through `IntegrityAnchoringService.RunOnceAsync`
  (reusing the `AnchoringMetricsCollector` pattern already used by `RunOnceAsync_FailingRunWhileAnchorAwaitsTimestamps_StillReportsTheDelay`) with an
  older well-formed stream, a malformed document (wrong-typed `AggregateId`), and a newer well-formed stream, asserting the
  `chaos.mongo.eventstore.anchoring.delay` histogram reflects the older well-formed stream's age rather than stopping at the malformed document, and
  a case where every candidate in a batch is malformed, asserting no invalid/sentinel delay measurement is recorded for that run.
- Add a case (in whichever of the files above already covers a wrong-typed `AggregateId`/`Version` scenario) asserting that after the skip,
  `CheckCompletenessAsync` still reports the skipped document as a `MalformedEvent` finding, so an unreadable identity does not hide the tampering
  from completeness verification.
- Add a separate case (in whichever of the files above already covers the malformed-`_integrity.Hash` scenario) asserting that after the skip,
  `VerifyStreamAsync`/`VerifyEvent` still fails the stream's hash check — not asserting a `CheckCompletenessAsync`/`MalformedEvent` finding for this
  case, since the identity remains readable — so a short hash does not hide the tampering from stream verification.
- Cover the new `StoredAggregateId` (or equivalently named) helper and the metric/log side effects with solitary tests where the existing sociable-test
  guidance in `tests/AGENTS.md` does not reach them (for example, a value of a BSON type that is binary but not UUID-standard-subtype).
