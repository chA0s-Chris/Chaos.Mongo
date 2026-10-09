# Plan deviations: separate stream position from aggregate revision

> Issue: [#164](https://github.com/chA0s-Chris/Chaos.Mongo/issues/164)

Compares the implementation with [`0164-0-event-store-separate-stream-position.md`](0164-0-event-store-separate-stream-position.md).
No follow-up plans exist. Every acceptance criterion of the plan is met; the deviations below concern how
individual Technical Details were realized.

## Layer 1: Model and append semantics

### Stream-head read

- **Planned:** The stream head is read "by the same indexed point read `GetExpectedNextVersionAsync` performs",
  which loaded and deserialized the complete last event.
- **Implemented:** `ReadStreamHeadAsync` uses the same filter, sort and limit, but projects only `Version` and
  `Revision` (excluding `_id`) into a `BsonDocument`. `GetExpectedNextVersionAsync` now uses this projected read too.
  The element names come from the registered class map (`EventDocumentFields`).
- **Why:** Deserializing the whole head event made every append fail as soon as the last stored event could not be
  deserialized. For example, malformed `_integrity` data then surfaced as a `FormatException` instead of the
  `MongoEventStoreException` that the integrity tests expect from sealing. The head only needs the two numbers, and
  the projection also transfers less data.

### Aggregate `Version` and `Revision` during a batch

- **Planned:** Apart from revisions and checkpoints, appending state-changing events "otherwise behaves as today".
  Before, `Execute` saw the aggregate's pre-batch `Version` for every event of a batch, and `Version` was set once
  after the batch.
- **Implemented:** `Revision` and `Version` are updated on the aggregate after each state-changing event. A later event
  in the same batch therefore sees the values produced by the events before it. Stored documents and the final
  aggregate are unchanged.
- **Why:** An observational event's `Validate` in a mixed batch must observe the state, including the revision, that
  the preceding events produced. Updating the values per event keeps the state and its metadata consistent.

### Benchmark scenario matrix

- **Planned:** The observational scenario is valid only against existing streams, and the scenario matrix expresses
  that constraint through `ParamsSource`.
- **Implemented:** The former `[Params]` property `ExistingStream` was folded into `BenchmarkScenario`, and the matrix
  lists every valid scenario/stream combination explicitly. A scenario's display name now ends in `/NewStream` or
  `/ExistingStream`.
- **Why:** This is a direct consequence of the planned constraint, because BenchmarkDotNet cannot exclude one
  combination of two independent parameters. Benchmark results are no longer column-compatible with earlier runs: the
  separate `ExistingStream` column is gone.

## Layer 2: Store-assigned positions, revision-based concurrency and reconstruction

### Discriminator element name of the replay filter

- **Planned:** "The discriminator element name comes from
  `BsonSerializer.LookupDiscriminatorConvention(typeof(Event<TAggregate>)).ElementName`."
- **Implemented:** The element name is read from the convention of the registered event serializer
  (`BsonSerializer.LookupSerializer<Event<TAggregate>>()` as `IHasDiscriminatorConvention`). The planned call remains
  only as a fallback for a custom serializer that does not expose its convention.
- **Why:** The class map of `Event<TAggregate>` registers its hierarchical discriminator convention lazily, the first
  time its serializer needs it. `BsonSerializer.LookupDiscriminatorConvention` falls back to the scalar convention
  when none is registered yet, and caches that result for the type. The repository resolves the element name in its
  constructor, which can run before the first event is serialized. The planned call would then have switched every
  event of the process from a hierarchical `_t` array to a scalar `_t` (MongoDB.Driver 3.12.0, observed in the
  integration tests). The serializer path lets the class map register the hierarchical convention first.
  `ReconstructionIntegrationTests.Repository_CreatedBeforeFirstAppend_KeepsHierarchicalDiscriminators` guards against
  this regression.

### Observational discriminators

- **Planned:** The replay filter's discriminators are derived after registration through
  `BsonClassMap.LookupClassMap(type).Discriminator`.
- **Implemented:** `RegisterClassMaps` reads the discriminator from the already registered class map, without looking
  it up, and stores the result in the internal `MongoEventStoreOptions<TAggregate>.ObservationalEventDiscriminators`.
- **Why:** `LookupClassMap` freezes the class map during registration. The serialization setup deliberately avoids
  freezing consumer class maps. The registered class map yields the same discriminator, including one a consumer set
  on their own class map.

### Versions after a failed store-assigned append

- **Planned:** Not specified. Store-assigned positions are written to the events like generated IDs.
- **Implemented:** When a store-assigned append fails for any reason (validation, `expectedRevision` mismatch,
  exhausted retries, duplicate ID), every event's `Version` is reset to `0`. A successful append keeps the assigned
  positions. Generated IDs are kept in both cases, so idempotency still works across retries.
- **Why:** Otherwise a caller who retries with the same instances would silently switch to explicit-version mode, with
  stale positions, and get a `MongoConcurrencyException` instead of a new store-assigned attempt. This is documented
  on `IEventStore<TAggregate>.AppendEventsAsync` and in `docs/event-store.md`.

### Checkpoint indexes

- **Planned:** The configurator creates both nearest-checkpoint indexes on the checkpoint collection.
- **Implemented:** The indexes are created only when checkpoints are enabled.
- **Why:** Without checkpoints, creating the indexes would create an otherwise unused checkpoint collection.
  `EventStoreIndexContractIntegrationTests` covers both cases.

## Test adjustments not described in the plan

- `IntegrityChainIntegrationTests.AppendEventsAsync_MissingPredecessor_ThrowsMongoEventStoreExceptionAndPersistsNothing`
  used to delete the last event before the append. Positions now come from the stream head, so such an append is
  validated against the shortened stream and never reaches sealing. The test now deletes the predecessor through the
  internal `AfterStreamHeadRead` seam, after the head was read, which keeps coverage of the sealing safety check.
- `EventStoreIntegrationTests.AppendEventsAsync_VersionBelowOne_ThrowsArgumentException` uses version `-1` instead of
  `0`, because `0` requests store-assigned positions.
- The Moq-based proxies in `MongoEventStoreBulkWriteTests` and `MongoEventStoreQueryContractTests` accept finds
  projected into a `BsonDocument`, because the head read is projected.
