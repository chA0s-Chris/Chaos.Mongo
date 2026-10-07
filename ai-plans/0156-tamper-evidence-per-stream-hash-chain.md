# Tamper evidence: per-stream hash chain, canonical form and retroactive sealing

> Issue: [#156](https://github.com/chA0s-Chris/Chaos.Mongo/issues/156)

## Rationale

Event streams are plain MongoDB documents today. Anyone with write access can modify or delete events without leaving a trace. This is the foundation of the tamper-evidence feature ([#154](https://github.com/chA0s-Chris/Chaos.Mongo/issues/154)):

- Each protected event stores a hash over its stored BSON, chained to its predecessor, so modification, deletion, reordering and insertion become detectable.
- Events written before protection was enabled are sealed retroactively, so whole streams come under protection, not only events appended later.
- It also provides the realistic baseline for the anchor-selection benchmark in #157.

The work stays one issue because sealing reuses the chain rule, format and verification of the append path, and is meaningless without them. It is delivered as a two-layer stack:

1. The chain on append: format, write path, verification and benchmark.
2. Retroactive sealing: a background service with locking and concurrency concerns that deserves separate review.

Everything stays internal until #160 makes the feature public.

## Acceptance Criteria

### Layer 1: Chain on append

- [x] Integrity protection is opt-in per aggregate type through internal configuration, and nothing new is publicly reachable; a reflection-based test asserts that the new integrity types and members are not public.
- [x] With protection disabled, stored event documents and append behavior are unchanged, and the existing test suite passes.
- [x] With protection enabled, every appended event stores `_integrity` with format version, algorithm (SHA-256), previous hash, hash and seal mode `Append`.
- [x] Version 1 chains from the genesis value; every later version chains from its predecessor's stored hash.
- [x] Both append paths (default and bulk write) persist byte-identical event documents for identical input.
- [x] The hash recomputed from the stored raw bytes, with `_integrity` removed at byte level, equals the hash computed at write time. This holds for `Int64` values within `Int32` range, GUIDs, decimals, nested documents and `CreatedUtc` values with sub-millisecond ticks.
- [x] Appending to a protected stream whose predecessor event is missing fails with `MongoEventStoreException`, and nothing is persisted.
- [x] Concurrent appends to the same stream produce a linear chain.
- [x] An internal stream verification recomputes the chain from raw documents and reports the first broken version for modified, deleted, reordered and inserted events.
- [x] A member mapped to the reserved element name `_integrity`, or (with protection enabled, so unprotected registrations stay unchanged) a pre-registered `Event<TAggregate>` class map without the integrity member, fails at registration with a clear error.
- [x] Automated tests cover the behavior above.
- [x] `EventStoreAppendBenchmarks` compares protection disabled and enabled for new streams and for appends to existing streams (version > 1), on both append paths.

### Layer 2: Retroactive sealing

- [ ] A background sealing sweep, registered only for protected aggregate types, seals every event without `_integrity` with seal mode `Retroactive`. This covers events written before protection was enabled and events written while it was disabled.
- [ ] Retroactive sealing appends only the `_integrity` element. The remaining stored bytes stay identical, and the resulting chain passes the internal stream verification.
- [ ] The sweep runs one pass on startup and then periodically (default 24 hours, configurable), on exactly one instance at a time.
- [ ] An interrupted pass resumes from its persisted position instead of starting over.
- [ ] Sealing progress (streams checked and sealed, pass start and completion times) is queryable through an internal API.
- [ ] An append to a stream with at most one sealing chunk of unsealed predecessors seals them in the same transaction before appending. With more, the append fails with `MongoEventStoreException` without persisting anything, and the stream is caught up by the sweep.
- [ ] Concurrent sealing of the same stream by the sweep and an append produces one consistent chain.
- [ ] Automated tests cover the behavior above, including a stream longer than one sealing chunk and an append to a stream with more unsealed predecessors than one chunk.

## Technical Details

### Configuration and visibility

- New internal `MongoEventStoreBuilder<TAggregate>.WithIntegrityProtection()` and a corresponding internal flag on `MongoEventStoreOptions<TAggregate>`. The sweep interval is configured there too.
- New `src/Chaos.Mongo.EventStore/Properties/InternalsVisibleTo.cs`, following `src/Chaos.Mongo/Properties/InternalsVisibleTo.cs`. It is wrapped in `#if !NUGET_RELEASE` and grants access to `Chaos.Mongo.EventStore.Tests` and `Chaos.Mongo.EventStore.Benchmarks`.

### Storage and mapping

- New `internal EventIntegrity? Integrity { get; set; }` on `Event<TAggregate>`.
- In `MongoEventStoreSerializationSetup.RegisterClassMaps`, map it explicitly to the element `_integrity` with ignore-if-null.
- It is mapped regardless of whether protection is enabled. BSON class maps are process-global, so stores with and without protection share them, and documents with `_integrity` must deserialize in both cases.
- Collision detection inspects the registered event class maps, including consumer-registered ones that `RegisterClassMaps` skips. It throws a clear error instead of relying on the driver's lazy freeze-time error.
- Because class-map registration is process-global and irreversible, tests for these registration errors use dedicated aggregate and event types that no other test registers.

Draft format, version 1 (illustrative; may still change until #160):

```text
_integrity: { FormatVersion: 1, Algorithm: "SHA-256", PreviousHash: BinData, Hash: BinData, SealMode: "Append" | "Retroactive" }
```

### Chain and canonical form

- `Hash = SHA-256(canonical ‖ PreviousHash)`.
- `canonical` is the event document's BSON without the `_integrity` element.
- The genesis value is SHA-256 over a fixed domain-separation tag, the UTF-8 `AggregateType` value, a zero byte, and the `AggregateId` in big-endian RFC 4122 byte order.
- Write path:
  1. Both paths in `MongoEventStore.AppendEventsAsync` always serialize events to `BsonDocument`, also with protection disabled. The bulk path already does; the default path changes from typed `InsertManyAsync` to inserting `BsonDocument`s into the same collection. Unit tests that stub only `GetCollection<Event<TAggregate>>` (e.g. in `MongoEventStoreBulkWriteTests`) must be adjusted for the `BsonDocument` collection, without introducing new Moq usage (see `tests/AGENTS.md`).
  2. With protection enabled, hash `ToBson()` of that document.
  3. Append `_integrity` as the last element.
  4. Insert.
- The class-map serializer writes the discriminator `_t` before `_id`, but the server always stores `_id` first. Events are therefore serialized with `_id` moved to the front, which leaves stored documents unchanged and makes the server store the bytes exactly as sent. A test asserts that the write-time hash equals the recomputation from the stored bytes. (Corrected during implementation; the draft wrongly assumed `_id` is written first.)
- Duplicate-key translation must keep working for `BsonDocument` inserts; existing tests cover it.
- Verification and sealing read `RawBsonDocument` and remove the top-level `_integrity` element at byte level (cut the element bytes and correct the length prefix). They never decode and re-encode. This byte-level helper needs its own focused tests: element absent, first, middle or last.
- **No `CreatedUtc` truncation.** BSON already stores milliseconds, and the hash covers the serialized bytes. Deviation from the issue text, agreed during planning; #156 is updated accordingly.

### Append with protection

- Inside the transaction, read the predecessor (version `firstVersion − 1`) as a point read on the existing `(AggregateId, Version)` index, projecting `_integrity`.
- A missing predecessor raises `MongoEventStoreException`.
- An unsealed predecessor:
  - Layer 1: raises `MongoEventStoreException` (interim behavior).
  - Layer 2: is sealed in the same transaction first, up to one sealing chunk (safety net; see "Sealing a stream").
- Concurrent appends are already serialized by the unique `(AggregateId, Version)` index.
- `ExecuteInTransaction` relies on the driver's `WithTransactionAsync`, which reruns the whole callback on `TransientTransactionError`, including write conflicts. The callback must therefore stay side-effect-free outside the session.

### Sealing sweep (Layer 2)

- **Hosted service.** Follows the repository pattern of `IHostedLifecycleService`, with `StartAsync`/`StopAsync` as no-ops and the work loop in a component with its own cancellation, as in `OutboxHostedService`/`OutboxProcessor`. It is registered in `MongoBuilderExtensions.WithEventStore` only when protection is enabled.
- **Locking.** `IMongoHelper.TryAcquireLockAsync` with one lock per events collection, extended via `TryExtendAsync` during long passes. If the lock is lost, the pass stops at the last committed position.
- **Finding unsealed streams.**
  - A pass enumerates stream heads from the events collection, not from the read model (which may be missing or stale), using only the existing `(AggregateId, Version)` index.
  - Its cost grows with the number of streams, never the number of events. An integration test verifies this with `explain()`.
  - Reliable shape: from the cursor, read the next `AggregateId` greater than the cursor (`find` + sort + `limit(1)`), then read that stream's head with sort `{ Version: -1 }` + `limit(1)`. That's two index point reads per stream.
  - A `$group`-based distinct scan is only possible with a sort matching the index direction (`{ AggregateId: -1, Version: -1 }`, with a descending cursor).
- **Invariant:** if a stream's head is sealed, all of its events are sealed. Appends seal their predecessors first, and sealing proceeds in version order. So one head check per stream suffices.
- **Sealing a stream.**
  - Continue from the last sealed event (or genesis) in version order.
  - Each event gets `$set: { _integrity }` with the filter `{ _id, _integrity: { $exists: false } }`.
  - The sweep seals in bounded chunks, one transaction per chunk (internal default chunk size 1,000 events, configurable).
  - The append safety net uses the same bound. Within it, it seals the unsealed prefix in the append transaction. Beyond it, the append fails with `MongoEventStoreException` ("stream not yet sealed") instead of risking a transaction that exceeds `transactionLifetimeLimitSeconds`.
  - A concurrent seal inside a transaction surfaces as a write conflict (`TransientTransactionError`); the driver reruns the callback, which then reads the events as already sealed.
  - A zero match count is not retried by the driver and should not occur under snapshot isolation. If it does, it is treated as a concurrent modification: the append fails with `MongoConcurrencyException`, and the caller retries per the existing optimistic-concurrency contract. The sweep re-reads the stream and continues.
- **State document.** A sweep state document per aggregate type (new collection `{CollectionPrefix}_IntegrityState`, via an internal suffix option analogous to `EventsCollectionSuffix`/`CheckpointCollectionSuffix`) persists:
  - the cursor (last processed `AggregateId`)
  - the counters
  - the pass timestamps

  The internal progress API reads it. Its location is a draft and may move to the module-level anchoring configuration introduced in #158.
- **Anchor reference.** The reference from retroactively sealed events to their sealing anchor is added in #158.

### Benchmarks

- Extend `benchmarks/Chaos.Mongo.EventStore.Benchmarks/EventStoreAppendBenchmarks.cs` with protection on/off and new versus existing streams via `ParamsSource`.
- The current benchmark always creates new aggregates, so the predecessor read is only measured by appending to pre-seeded streams.
- Follow `benchmarks/AGENTS.md`: keep container start-up and seeding outside the measured operation.

### Stack Design

1. `0156-tamper-evidence-per-stream-hash-chain-append`: integrity format, mapping, canonical form, chained append on both write paths, internal verification and benchmark. Depends on trunk.
2. `0156-tamper-evidence-per-stream-hash-chain-sealing`: background sealing sweep, append safety net and sealing progress. Depends on the append layer and replaces its interim error for unsealed predecessors.
