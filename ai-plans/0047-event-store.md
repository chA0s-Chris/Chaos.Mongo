# 0047 — MongoDB Event Store

## Rationale

Extend `Chaos.Mongo` with a new project `Chaos.Mongo.EventStore` that provides event sourcing capabilities backed by MongoDB. The event store allows appending domain events to per-aggregate-type collections, replaying event streams to rebuild aggregate state, and maintaining up-to-date read models within the same transaction as event writes. Concurrency is handled via a unique compound index on `(AggregateId, Version)`, and optional periodic checkpoints reduce the cost of rebuilding aggregates with many events.

## Acceptance Criteria

- [x] New project `Chaos.Mongo.EventStore` exists, references `Chaos.Mongo`, and is included in the solution
- [x] `IAggregate` interface with `Id` (Guid), `Version` (Int64), and `CreatedUtc` (DateTime) properties
- [x] `Aggregate` base class implementing `IAggregate`
- [x] `Event<TAggregate>` base class with `Id`, `CreatedUtc`, `AggregateType`, `AggregateId`, `Version`, and abstract `Execute(TAggregate)` method
- [x] Exception types in `Errors` namespace: `MongoEventStoreException` (base), `MongoConcurrencyException` (version conflict), `MongoDuplicateEventException` (event ID conflict), `MongoEventValidationException` (event cannot be applied to aggregate state)
- [x] `IEventStore<TAggregate>` interface with `GetExpectedNextVersionAsync`, `AppendEventsAsync` (with optional transactional callback), and `GetEventStream` methods
- [x] `IAggregateRepository<TAggregate>` interface with `GetAsync`, `GetAtVersionAsync`, and `Collection` property for direct MongoDB access
- [x] `MongoEventStore<TAggregate>` implementation using `IMongoHelper.ExecuteInTransaction` for transaction handling
- [x] Each aggregate type uses its own events collection and its own read-model collection
- [x] Unique compound index on `(AggregateId, Version)` in each events collection to enforce concurrency
- [x] `AppendEventsAsync` validates events by applying them to the aggregate outside the transaction, then persists within a transaction
- [x] Duplicate-key errors are caught and rethrown as `MongoConcurrencyException` (version conflict) or `MongoDuplicateEventException` (event ID conflict)
- [x] MongoDB discriminators are configured at runtime for each event type so polymorphic serialization works correctly
- [x] Event discriminator names are customizable via `WithEvent<T>(string? discriminator)` and default to the class name (not full name) if not specified
- [x] `GuidSerializer` with `GuidRepresentation.Standard` is registered during initialization if not already present (guard against duplicate registration)
- [x] User-facing configuration API (builder pattern on `MongoBuilder`) to register aggregate types and their event types
- [x] Optional checkpoint support: periodic snapshot read models stored every N versions, configurable per aggregate type
- [x] Checkpoint documents use composite `CheckpointId` record struct as `_id`, configured via `BsonClassMap` (not attributes)
- [x] Transactional callback support: `AppendEventsAsync` accepts optional `onBeforeCommit` callback for side-effects (e.g., transactional outbox)
- [x] Automated tests written (46 integration and unit tests using Testcontainers)

## Technical Details

### Project Structure

`Chaos.Mongo.EventStore` lives in `src/Chaos.Mongo.EventStore` and depends on `Chaos.Mongo`. Tests go in `tests/Chaos.Mongo.EventStore.Tests`. Both projects and the solution file already exist as scaffolds.

### Core Types

**`IAggregate`** — Interface for all aggregate root types, allowing custom implementations beyond the base class.

- `Guid Id { get; set; }` — The aggregate identifier.
- `Int64 Version { get; set; }` — The version of the aggregate after the last applied event.
- `DateTime CreatedUtc { get; set; }` — Timestamp when the aggregate was first created.

**`Aggregate`** — Abstract base class implementing `IAggregate`. Must have a parameterless constructor. Configure `Id` as `BsonId` via class map in code (not via attribute) to keep the base class free of MongoDB dependencies.

The recommended pattern is that the first event for any aggregate should be a creation event (e.g., `OrderCreatedEvent`) that initializes the aggregate's required state.

**`Event<TAggregate>`** — Abstract base class for domain events. Constrained to `where TAggregate : class, IAggregate, new()`.

- `Guid Id` — Unique event identifier. Configure as `BsonId` via class map in code. Used for idempotency: if a caller retries an append operation with the same event ID, the duplicate is detected and rejected. MongoDB automatically creates a unique index on the `_id` field.
- `DateTime CreatedUtc` — Timestamp; set automatically by the event store on append if not provided.
- `String AggregateType` — Discriminator string for the aggregate type; set automatically by the event store.
- `Guid AggregateId` — The aggregate this event belongs to.
- `Int64 Version` — Monotonically increasing version per aggregate; set by the caller.
- `abstract void Execute(TAggregate aggregate)` — Applies this event's changes to the given aggregate instance. May throw `MongoEventValidationException` if the aggregate's current state does not permit the operation.

**Exceptions** (in `Errors` namespace)

- `MongoEventStoreException` — Base exception for all event store errors.
- `MongoConcurrencyException` — Thrown when a duplicate-key error occurs on the `(AggregateId, Version)` compound index. Another process inserted an event for that aggregate version. The caller should retry with a new version.
- `MongoDuplicateEventException` — Thrown when a duplicate-key error occurs on the event `_id` field. The event was already processed (idempotency). The caller typically does nothing.
- `MongoEventValidationException` — Thrown when an event cannot be applied because the aggregate's state does not permit it. Events throw this from `Execute()` when preconditions aren't met. No events are persisted when this occurs.

### Interface: `IEventStore<TAggregate>`

Use a generic interface rather than a non-generic `IEventStore` so each aggregate type gets its own DI registration and its own strongly-typed event store instance.

```csharp
Task<Int64> GetExpectedNextVersionAsync(Guid aggregateId, CancellationToken ct)
Task<Int64> AppendEventsAsync(
    IEnumerable<Event<TAggregate>> events,
    Func<IClientSessionHandle, IMongoHelper, CancellationToken, Task>? onBeforeCommit = null,
    CancellationToken ct = default)
IAsyncEnumerable<Event<TAggregate>> GetEventStream(Guid aggregateId, Int64 fromVersion, Int64? toVersion, CancellationToken ct)
```

- `GetExpectedNextVersionAsync` — Queries the events collection for the highest `Version` for the given `AggregateId` and returns `maxVersion + 1` (or `1` if no events exist). **Important:** This returns the *expected* next version based on current state, not a reserved slot. Concurrent callers may receive the same value; only the first to insert will succeed (enforced by the unique index).
- `AppendEventsAsync` — Validates events by applying them to the aggregate in memory first. If validation succeeds, persists within a transaction: inserts events, upserts read model, creates checkpoint if needed, and invokes optional `onBeforeCommit` callback. Returns the new version after the last inserted event. The optional callback enables transactional side-effects like inserting into a transactional outbox.
- `GetEventStream` — Returns events for an aggregate ordered by `Version`, optionally bounded by `fromVersion`/`toVersion`.

### Interface: `IAggregateRepository<TAggregate>`

Separate from the event store, a repository provides access to aggregate state:

```csharp
Task<TAggregate?> GetAsync(Guid aggregateId, CancellationToken ct)
Task<TAggregate?> GetAtVersionAsync(Guid aggregateId, Int64 version, CancellationToken ct)
IMongoCollection<TAggregate> Collection { get; }
```

- `GetAsync` — Returns the current read model for the aggregate, or `null` if not found. Sets `CreatedUtc` from the first event if the aggregate exists.
- `GetAtVersionAsync` — Reconstructs the aggregate state at a specific version. Uses checkpoints if available (loads nearest checkpoint ≤ target version, then replays remaining events). Returns `null` if the aggregate doesn't exist or has no events up to that version.
- `Collection` — Exposes the underlying `IMongoCollection<TAggregate>` for advanced queries on the read model.

The repository reads from the read-model and checkpoint collections but does not write to them. It provides a clean separation: `IEventStore` is for appending events; `IAggregateRepository` is for querying aggregate state.

### Collections

For a given aggregate type `TAggregate`:

- **Events collection**: `IMongoDatabase.GetCollection<Event<TAggregate>>(name)` — name derived from configuration (e.g., `"Orders_Events"`). Default suffix: `_Events`.
- **Read-model collection**: `IMongoDatabase.GetCollection<TAggregate>(name)` — e.g., `"Orders"`. Uses the collection prefix directly.
- **Checkpoint collection** (optional): `IMongoDatabase.GetCollection<CheckpointDocument<TAggregate>>(name)` — e.g., `"Orders_Checkpoints"`. Default suffix: `_Checkpoints`.

**`CheckpointDocument<TAggregate>`** wraps the aggregate state with a composite ID:

- `CheckpointId Id` — Composite record struct `CheckpointId(Guid AggregateId, Int64 Version)` configured as `BsonId` via `BsonClassMap` (not attribute).
- `TAggregate State` — The full aggregate state at that version.

Use `IMongoHelper.Database` to access collections directly by name rather than through `IMongoHelper.GetCollection<T>()`, since the event store manages its own collection naming.

### Concurrency and Idempotency via Unique Indexes

**Idempotency:** Event `Id` is configured as `BsonId`, so MongoDB automatically creates a unique index on `_id`. Duplicate events with the same ID result in `MongoDuplicateEventException`.

**Concurrency:** A unique compound index named `AggregateIdWithVersionUnique` on `{ AggregateId: 1, Version: 1 }` in each events collection is the core concurrency mechanism. Two concurrent processes calling `GetExpectedNextVersionAsync` may get the same version, but only the first `InsertMany` will succeed. The second will fail with a duplicate-key error, wrapped as `MongoConcurrencyException`.

Index creation happens via `MongoEventStoreConfigurator`, an `IMongoConfigurator` registered automatically when the user configures an aggregate. The index name is exposed via `IndexNames.AggregateIdWithVersionUnique` for reference.

### AppendEventsAsync Implementation

`AppendEventsAsync` validates events before persisting, keeping transactions short:

**Validation Phase** (outside transaction):
1. Set `AggregateType` and `CreatedUtc` on each event (if not already set).
2. Load the current read model from the database, or create a new instance via parameterless constructor if none exists. Set `CreatedUtc` on new aggregates.
3. Apply each event via `event.Execute(aggregate)`. Events may throw `MongoEventValidationException` if the aggregate's state does not permit the operation.
4. Update the aggregate's `Version` property to match the last event's version.

**Persistence Phase** (inside transaction via `IMongoHelper.ExecuteInTransaction`):
1. Insert the events into the events collection.
2. Upsert the aggregate document in the read-model collection.
3. If checkpoints are enabled and `newVersion % checkpointInterval == 0`, insert a checkpoint document.
4. If `onBeforeCommit` callback is provided, invoke it for additional transactional operations.
5. Commit the transaction. If a duplicate-key error occurs, wrap and rethrow as `MongoConcurrencyException` or `MongoDuplicateEventException`.

### Discriminator Configuration

Since an events collection contains multiple concrete event types (e.g., `OrderCreatedEvent`, `OrderShippedEvent`) all deriving from `Event<TAggregate>`, MongoDB needs discriminators to correctly serialize/deserialize.

Use `BsonClassMap.RegisterClassMap<T>()` at runtime during event store configuration. The builder API (see below) collects all event types for an aggregate and registers their class maps and discriminators before the event store is used.

Use a scalar discriminator based on the type name (or a user-specified string) so the `_t` field in MongoDB contains a readable value. Users should be encouraged to provide explicit discriminator names to avoid breaking changes when class names or namespaces change.

### Guid Serialization

Since `Guid` is used for aggregate and event IDs, the MongoDB driver requires explicit `GuidRepresentation` configuration. The event store must ensure a `GuidSerializer` is registered with `GuidRepresentation.Standard` (the preferred representation).

During event store initialization, check if a `GuidSerializer` is already registered (via `BsonSerializer.LookupSerializer<Guid>()`). If not, register one with `GuidRepresentation.Standard`. Since `BsonSerializer.RegisterSerializer()` is not idempotent, guard against duplicate registrations.

The user may have already configured their own `GuidSerializer` via `MongoOptions.ConfigureClientSettings` or globally — respect their configuration if present.

### Configuration / DI Registration

Extend `MongoBuilder` with `WithEventStore<TAggregate>` extension method (in `MongoBuilderExtensions`):

```csharp
services.AddMongo(connectionString)
    .WithEventStore<OrderAggregate>(es => es
        .WithEvent<OrderCreatedEvent>("OrderCreated")      // discriminator name; optional, defaults to class name
        .WithEvent<OrderShippedEvent>("OrderShipped")
        .WithCollectionPrefix("Orders")                    // optional; defaults to aggregate type name
        .WithCheckpoints(interval: 100)                    // optional; disabled by default
        .WithEventsCollectionSuffix("_Events")             // optional; default is "_Events"
        .WithCheckpointCollectionSuffix("_Checkpoints"));  // optional; default is "_Checkpoints"
```

**`MongoEventStoreBuilder<TAggregate>`** provides fluent configuration:
- `WithEvent<TEvent>(string? discriminator)` — Registers an event type with optional discriminator
- `WithCollectionPrefix(string prefix)` — Sets collection name prefix
- `WithCheckpoints(int interval)` — Enables checkpoints at specified version interval
- `WithEventsCollectionSuffix(string suffix)` — Sets events collection suffix
- `WithCheckpointCollectionSuffix(string suffix)` — Sets checkpoint collection suffix

**`MongoEventStoreOptions<TAggregate>`** stores configuration (made public for testing/inspection).

This registers:
- `IEventStore<OrderAggregate>` as scoped (backed by `MongoEventStore<OrderAggregate>`)
- `IAggregateRepository<OrderAggregate>` as scoped (backed by `MongoAggregateRepository<OrderAggregate>`)
- `MongoEventStoreOptions<OrderAggregate>` as singleton
- `MongoEventStoreConfigurator<OrderAggregate>` as `IMongoConfigurator` for index creation
- BsonClassMap registrations via `MongoEventStoreSerializationSetup` for `Event<TAggregate>`, all registered event types, `Aggregate`, `CheckpointDocument<TAggregate>`, and `CheckpointId`
- `GuidSerializer` with `GuidRepresentation.Standard` (if not already registered)

### Checkpoints

A checkpoint is a snapshot of an aggregate's state at a specific version. When enabled:

- A checkpoint collection (e.g., `"Orders_Checkpoints"`) stores `CheckpointDocument<TAggregate>` documents with composite `CheckpointId { AggregateId, Version }` as `_id`.
- During `AppendEventsAsync`, after updating the read model, if `newVersion % checkpointInterval == 0`, insert a checkpoint.
- `IAggregateRepository.GetAtVersionAsync` uses checkpoints: loads nearest checkpoint ≤ target version, then replays remaining events.
- `GetEventStream` itself doesn't use checkpoints (it returns raw events).

Checkpoint creation happens within the same transaction as event insertion and read-model update.

### What Not to Build (For Now)

The following features are explicitly out of scope for this initial implementation but are marked as optional enhancements for future plans:

#### Projection/Subscription System (Optional — Future Enhancement)

**Not included in this plan.** A projection system would allow building read models from events across one or more aggregate types, similar to the existing `IMongoQueue` infrastructure.

**Potential design:**
- Projections tail events collections (via MongoDB change streams or polling) and invoke projection handlers
- Register via builder API: `builder.WithEventStoreProjection<CustomerOrdersProjection>(p => p.SubscribeTo<OrderAggregate>()...)`
- Each projection handler implements `IEventProjection<TEvent>` with `HandleAsync(TEvent, CancellationToken)`
- Projections track their position (last processed event) for resumption after restarts
- Leverage existing `IMongoConfigurator` pattern for projection collection indexes

**Complexity:** Medium. The queue infrastructure provides a template, but event ordering across aggregates and handling late-arriving events adds complexity.

**Recommendation:** Phase 2 enhancement — this unlocks the real power of event sourcing and is the natural next step after the core event store is stable.

#### Global Event Ordering Across Aggregate Types (Optional — Future Enhancement)

**Not included in this plan.** The current design uses per-aggregate-type collections with per-aggregate versioning. Global ordering would enable temporal queries across all event types.

**Potential design:**
- Single global events collection with all events from all aggregate types, OR
- Add a `GlobalSequence` field to events, populated via a distributed counter (MongoDB `$inc` on a counter document)
- Enables queries like "all events between timestamps X and Y" for audit logs or debugging

**Trade-offs:**
- **Pro:** Simplifies certain projection scenarios, provides comprehensive audit trail
- **Con:** Single collection becomes a bottleneck; harder to partition/shard; conflicts with per-aggregate-type collection design
- **Alternative:** Keep per-aggregate collections but add optional global sequence field

**Complexity:** Medium-high if done properly (distributed counter, handling contention).

**Recommendation:** Optional — only implement if specific use cases demand it. The per-aggregate design is cleaner for most scenarios.

#### Event Schema Versioning / Upcasting (Optional — Future Enhancement)

**Not included in this plan.** Long-lived event-sourced systems need to handle schema evolution as event types change over time.

**Potential design:**
- Events include a `SchemaVersion` field or version suffix (e.g., `OrderCreatedEvent_v2`)
- Upcasters transform old event versions to new ones during deserialization
- Register via builder API: `es.WithUpcaster<OrderCreatedEvent_v1, OrderCreatedEvent_v2>(v1 => new v2 { ... })`
- Event store automatically applies upcaster chains (v1→v2→v3) when loading events
- Could be lazy (upcast on read) or eager (background migration job)

**Complexity:** Medium. Core mechanism is straightforward, but handling chains of upcasters and ensuring backward compatibility requires careful design.

**Recommendation:** Phase 3 enhancement — add this before the first production deployment, or at least before the first schema change is needed in a production system.

#### Advanced Snapshotting Strategies

**Not included in this plan.** Only simple interval-based checkpoints are supported. Advanced strategies (e.g., adaptive checkpointing based on event count, time-based snapshots, or on-demand snapshots) are not implemented.

**Recommendation:** Evaluate need based on real-world usage patterns. The simple interval-based approach should suffice for most scenarios.
