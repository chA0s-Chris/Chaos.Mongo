# 0081 — MongoDB 8 Bulk-Write Optimization for EventStore

> Issue: [#81](https://github.com/chA0s-Chris/Chaos.Mongo/issues/81)

## Rationale

Reduce the number of MongoDB round trips performed by `EventStore.AppendEventsAsync` by using MongoDB 8 client-level bulk writes for the built-in event, read-model, and optional checkpoint operations. The optimized path must be explicitly enabled, preserve the existing transaction and error semantics, and be supported by focused tests and representative benchmarks. Queue and Outbox remain outside this optimization because their current single-item processing flows do not provide useful operations to batch.

## Acceptance Criteria

- [x] EventStore exposes an explicit opt-in bulk-write configuration through its existing fluent builder, and the optimization is disabled by default
- [x] Enabling the optimization validates MongoDB 8 or later support before using the bulk-write path and fails clearly on unsupported servers without silently falling back
- [x] Capability detection preserves `AppendEventsAsync` cancellation semantics: each caller can cancel its wait promptly without permanently poisoning the shared capability cache or disrupting other callers
- [x] The optimized append path batches event inserts, the read-model upsert, and an optional checkpoint into an ordered client-level bulk write within the existing transaction
- [x] Existing append semantics are preserved, including aggregate validation, transactional callbacks, checkpoint behavior, concurrency conflicts, and duplicate-event error mapping. Event ID generation for events appended without an explicit `Id` is unified across both paths: because the bulk-write path serializes events itself and bypasses the driver's ID generation, the event store now assigns the ID during event normalization. This also changes the default append path, which previously relied on the driver's version 4 GUIDs and now produces version 7 GUIDs on .NET 9 and later
- [x] Focused automated tests cover supported and unsupported server versions, capability-detection failures and cancellation, cache reuse across aggregate types sharing one client, the optimized write shape, polymorphic event round trips, transactional behavior, and duplicate/concurrency failure modes
- [x] BenchmarkDotNet benchmarks use a statistically meaningful job to report latency and allocations for the real baseline and optimized append paths across single-event, multi-event, and checkpoint-producing workloads on MongoDB 8; a separate short-run mode may be provided for smoke testing, and no minimum speedup is required
- [x] User-facing documentation explains how to enable the optimization, its MongoDB version requirement, failure behavior, and how to run the benchmarks
- [x] An architecture-decision section in `docs/event-store.md` records the rationale for excluding MongoQueue and MongoOutbox from the current optimization, including the conditions under which a future batching-oriented redesign could change that assessment

## Technical Details

Extend `MongoEventStoreBuilder<TAggregate>` and `MongoEventStoreOptions<TAggregate>` with an opt-in setting that follows the existing `With*()` configuration pattern. Keep the legacy append path as the default so existing applications retain their current behavior and MongoDB compatibility.

In `MongoEventStore<TAggregate>`, validate server capability before entering the optimized persistence path. Cache successful capability detection once per `IMongoClient`, shared across EventStore instances and aggregate types, without retaining disposed clients indefinitely. Each append caller must be able to cancel its wait promptly without canceling or permanently poisoning the shared lookup for other callers. Surface a clear unsupported-version exception for servers older than MongoDB 8, and keep failed capability lookups observable and retryable.

Build an ordered client bulk-write request for the EventStore-owned operations: insert each event into the events collection, replace or upsert the aggregate read model, and insert a checkpoint when the configured interval requires one. Execute the request through the existing session and transaction helper. Keep `onBeforeCommit` inside the same transaction after the built-in writes, because arbitrary user callback operations cannot be folded safely into the prepared bulk request. Preserve the established translation of duplicate event IDs and aggregate-version conflicts into the EventStore exception types.

Add focused unit and MongoDB 8 integration coverage for configuration defaults, server gating, failed and canceled capability lookups, operation ordering and target collections, checkpoint and callback persistence, and duplicate/concurrency behavior. Verify that two aggregate types sharing one client reuse the same successful capability lookup, and that optimized writes preserve polymorphic event serialization and round-trip behavior.

Add a dedicated BenchmarkDotNet project under `benchmarks/` that references the production EventStore implementation and runs against a MongoDB 8 replica-set Testcontainer. Compare baseline and optimized appends across a small explicit scenario matrix covering one event without checkpoints, a representative event batch without checkpoints, and a checkpoint-producing batch. Use a statistically meaningful BenchmarkDotNet job for reported latency and allocation results; a separate short-run configuration may be available for smoke testing, and the work does not require a predetermined speedup. Container startup, dependency-injection setup, index creation, and one-time capability detection should remain outside the measured operation.

Document the public configuration and benchmark workflow in `docs/event-store.md`. Add an architecture-decision section there explaining that Queue and Outbox remain excluded because their current control flows interleave single-item database operations with user or external work, leaving no meaningful batch to collapse without a broader architectural redesign. The decision should also state the batching and performance evidence that would justify reconsidering those components in the future.
