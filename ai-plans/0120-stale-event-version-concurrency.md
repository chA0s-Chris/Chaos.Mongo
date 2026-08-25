# AppendEventsAsync throws ArgumentException instead of MongoConcurrencyException for stale event versions

> Issue: [#120](https://github.com/chA0s-Chris/Chaos.Mongo/issues/120)

## Rationale

`MongoEventStore.AppendEventsAsync` pre-validates event versions against the read model outside the transaction and rejects everything that is not `aggregate.Version + 1` with `ArgumentException`. Because the read model is replaced inside the appending transaction, a writer that loses a race sees the already-advanced version and receives `ArgumentException`, while a writer that passes pre-validation before the winner commits reaches the unique `(AggregateId, Version)` index and receives the documented `MongoConcurrencyException`. The exception type therefore depends on interleaving, and the common case surfaces an optimistic-concurrency conflict as an input error.

Pre-validation must distinguish an already-consumed version from a genuine caller-side version gap, so the exception type reflects the cause rather than the timing.

## Acceptance Criteria

- [x] Appending events whose first version is greater than zero and not greater than the aggregate's current persisted version throws `MongoConcurrencyException`.
- [x] Such a stale append throws `MongoDuplicateEventException` instead when at least one submitted event ID already exists in the events collection, so idempotent retries stay distinguishable from concurrency conflicts.
- [x] Appending events whose first version exceeds the aggregate's current version by more than one still throws `ArgumentException`.
- [x] Appending events whose first version is less than one throws `ArgumentException` regardless of aggregate state.
- [x] Events after the first must be sequential relative to the preceding submitted event and otherwise throw `ArgumentException`, independent of whether the first version was stale.
- [x] A batch targeting more than one aggregate throws `ArgumentException` even when its first version is stale.
- [x] Writers that pass pre-validation and collide inside the transaction continue to receive `MongoConcurrencyException` or `MongoDuplicateEventException` from the existing duplicate-key translation.
- [x] Integration tests cover stale-version conflict, stale idempotent retry, version gap, non-positive version, malformed batch after a stale first version, and mixed aggregates, and existing tests asserting the previous mapping are updated.
- [x] `IEventStore.AppendEventsAsync` XML documentation and `docs/event-store.md` describe when each exception is thrown, including the two conflict-detection paths.

## Technical Details

Version pre-validation lives in `MongoEventStore.AppendEventsAsync` (`src/Chaos.Mongo.EventStore/MongoEventStore.cs`, step 2) and currently interleaves the aggregate-ID check with the sequential-version check in one loop. Validate all aggregate IDs before reaching any version verdict so a mixed-aggregate batch stays an `ArgumentException`.

Check events 2..n against the preceding submitted event first: a malformed batch is a caller error that no reload can fix, so it must never surface as a retryable conflict. Only then judge the batch's first version `v` against `aggregate.Version` from the read-model load:

- `v < 1` → `ArgumentException` (invalid input; guards the fresh-aggregate case where `aggregate.Version` is `0`).
- `v <= aggregate.Version` → the version is already committed: probe, then `MongoConcurrencyException`.
- `v > aggregate.Version + 1` → `ArgumentException` (caller-side version gap).

The pre-validation `MongoConcurrencyException` message states the submitted version and the aggregate's current version, and stays distinguishable from the duplicate-key message so tests can target each detection path.

The duplicate probe runs only on the stale branch — a single query against the events collection for the submitted event IDs, outside the transaction — so the success path takes no extra round trip. Any hit yields `MongoDuplicateEventException`, matching the idempotency contract documented in `docs/event-store.md`. Events without a caller-supplied ID are assigned a new `Guid.CreateVersion7()` earlier in the method and can never match.

A read model missing while events exist (rebuild, manual deletion) leaves `aggregate.Version` at `0`, so a live version is classified as a gap and `ArgumentException` is thrown, as today. The unique index remains the authoritative conflict detector.

Pre-validation precedes the `BulkWriteOptimizationEnabled` branch, so both append paths inherit the change without separate handling.

`AppendEventsAsync_DuplicateVersion_ThrowsArgumentException` (`tests/Chaos.Mongo.EventStore.Tests/Integration/EventStoreIntegrationTests.cs`) asserts exactly the behavior being inverted and must be rewritten. `AppendEventsAsync_RaceCondition_ThrowsMongoConcurrencyException` inserts directly into the events collection and bypasses the read model, so it keeps exercising the duplicate-key path unchanged.

This changes two exception mappings and is breaking for existing catch lists: a conflicting append that previously threw `ArgumentException` now throws `MongoConcurrencyException`, and an idempotent retry of an already-committed event now throws `MongoDuplicateEventException`. The changelog is generated by release-drafter from PR labels, so the pull request needs the `breaking` label rather than a manual `CHANGELOG.md` edit.
