# 0047 — MongoDB Event Store

## Rationale

Extend `Chaos.Mongo` with a new project `Chaos.Mongo.EventStore` that provides event sourcing capabilities backed by MongoDB. The event store allows appending domain events to per-aggregate-type collections, replaying event streams to rebuild aggregate state, and maintaining up-to-date read models within the same transaction as event writes. Concurrency is handled via a unique compound index on `(AggregateId, Version)`, and optional periodic checkpoints reduce the cost of rebuilding aggregates with many events.

## Acceptance Criteria

- [ ] New project `Chaos.Mongo.EventStore` exists, references `Chaos.Mongo`, and is included in the solution
- [ ] `Aggregate` base class with `Id` (Guid) and `Version` (Int64) properties
- [ ] `Event<TAggregate>` base class with `Id`, `CreatedUtc`, `AggregateType`, `AggregateId`, `Version`, and abstract `Execute(TAggregate)` method
- [ ] `EventStoreException` and `EventStoreConcurrencyException` (with `IsIdAffected`) exception types
- [ ] `IEventStore<TAggregate>` interface with `GetExpectedNextVersionAsync`, `AppendEventsAsync`, and `GetEventStream` methods
- [ ] `IRepository<TAggregate>` interface with `GetAsync` and `GetAtVersionAsync` methods for querying aggregate state
- [ ] `EventStore<TAggregate>` implementation that uses `IMongoHelper` for collection access and transactions
- [ ] Each aggregate type uses its own events collection and its own read-model collection
- [ ] Unique compound index on `(AggregateId, Version)` in each events collection to enforce concurrency
- [ ] `AppendEventsAsync` inserts events and updates the read model within a single transaction
- [ ] Duplicate-key `MongoWriteException` is caught and rethrown as `EventStoreConcurrencyException`
- [ ] MongoDB discriminators are configured at runtime for each event type so polymorphic serialization works correctly
- [ ] Event discriminator names are customizable via `WithEvent<T>(string? discriminator)` and default to the class name (not full name) if not specified
- [ ] `GuidSerializer` with `GuidRepresentation.Standard` is registered during initialization if not already present (guard against duplicate registration)
- [ ] User-facing configuration API (builder pattern on `MongoBuilder`) to register aggregate types and their event types
- [ ] Optional checkpoint support: periodic snapshot read models stored every N versions, configurable per aggregate type
- [ ] Automated tests are written (unit tests and integration tests using Testcontainers)

## Technical Details

### Project Structure

`Chaos.Mongo.EventStore` lives in `src/Chaos.Mongo.EventStore` and depends on `Chaos.Mongo`. Tests go in `tests/Chaos.Mongo.EventStore.Tests`. Both projects and the solution file already exist as scaffolds.

### Core Types

**`Aggregate`** — Abstract base class for all aggregate root types. Must have a parameterless constructor.

- `Guid Id` — The aggregate identifier. Configure as `BsonId` via class map in code (not via attribute) to keep the base class free of MongoDB dependencies.
- `Int64 Version { get; set; }` — The version of the aggregate after the last applied event. Mutable so the event store can update it after applying events. Named `Version` instead of `EventVersion` for brevity since it's unambiguous in this context.

The recommended pattern is that the first event for any aggregate should be a creation event (e.g., `OrderCreatedEvent`) that initializes the aggregate's required state.

**`Event<TAggregate>`** — Abstract base class for domain events.

- `Guid Id` — Unique event identifier. Configure as `BsonId` via class map in code. Used for idempotency: if a caller retries an append operation with the same event ID, the duplicate is detected and rejected. MongoDB automatically creates a unique index on the `_id` field.
- `DateTime CreatedUtc` — Timestamp; set automatically by the event store on append if not provided.
- `String AggregateType` — Discriminator string for the aggregate type; set automatically by the event store.
- `Guid AggregateId` — The aggregate this event belongs to.
- `Int64 Version` — Monotonically increasing version per aggregate; set by the event store.
- `abstract void Execute(TAggregate aggregate)` — Applies this event's changes to the given aggregate instance.

**Exceptions**

- `EventStoreException` — Base exception.
- `EventStoreConcurrencyException` — Thrown when a unique-index violation occurs during event insertion. `IsIdAffected` indicates whether the duplicate key error was on the event `_id` field (true = idempotency issue, event ID already exists) or on the `(AggregateId, Version)` compound index (false = concurrency issue, another process inserted an event for that aggregate version). When `IsIdAffected` is true, the caller typically does nothing (the event was already processed). When false, the caller should retry with a new version.

### Interface: `IEventStore<TAggregate>`

Use a generic interface rather than a non-generic `IEventStore` so each aggregate type gets its own DI registration and its own strongly-typed event store instance.

```
Task<Int64> GetExpectedNextVersionAsync(Guid aggregateId, CancellationToken ct)
Task<Int64> AppendEventsAsync(IEnumerable<Event<TAggregate>> events, CancellationToken ct)
IAsyncEnumerable<Event<TAggregate>> GetEventStream(Guid aggregateId, Int64 fromVersion, Int64? toVersion, CancellationToken ct)
```

- `GetExpectedNextVersionAsync` — Queries the events collection for the highest `Version` for the given `AggregateId` and returns `maxVersion + 1` (or `1` if no events exist). **Important:** This returns the *expected* next version based on current state, not a reserved slot. Concurrent callers may receive the same value; only the first to insert will succeed (enforced by the unique index).
- `AppendEventsAsync` — Within a transaction: inserts all events, then rebuilds/updates the read model by applying events to the current aggregate state, and upserts the aggregate document in the read-model collection. Returns the new version after the last inserted event.
- `GetEventStream` — Returns events for an aggregate ordered by `Version`, optionally bounded by `fromVersion`/`toVersion`. If checkpoints are enabled and a suitable checkpoint exists, streaming can begin from the checkpoint's version instead of `fromVersion = 0`.

### Interface: `IRepository<TAggregate>`

Separate from the event store, a repository provides access to aggregate state:

```
Task<TAggregate?> GetAsync(Guid aggregateId, CancellationToken ct)
Task<TAggregate?> GetAtVersionAsync(Guid aggregateId, Int64 version, CancellationToken ct)
```

- `GetAsync` — Returns the current read model for the aggregate, or `null` if not found.
- `GetAtVersionAsync` — Reconstructs the aggregate state at a specific version. Uses checkpoints if available (loads nearest checkpoint ≤ target version, then replays remaining events). Returns `null` if the aggregate doesn't exist or has no events up to that version.

The repository reads from the read-model and checkpoint collections but does not write to them. It provides a clean separation: `IEventStore` is for appending events; `IRepository` is for querying aggregate state.

### Collections

For a given aggregate type `TAggregate`:

- **Events collection**: `IMongoDatabase.GetCollection<Event<TAggregate>>(name)` — name derived from configuration or convention (e.g., `"OrderAggregate_Events"`).
- **Read-model collection**: `IMongoDatabase.GetCollection<TAggregate>(name)` — e.g., `"OrderAggregate"`.
- **Checkpoint collection** (optional): `IMongoDatabase.GetCollection<TAggregate>(name)` — e.g., `"OrderAggregate_checkpoints"`. Checkpoint documents contain the full aggregate state plus a `Version` field indicating up to which version they are valid.

Use `IMongoHelper.Database` to access collections directly by name rather than through `IMongoHelper.GetCollection<T>()`, since the event store manages its own collection naming.

### Concurrency and Idempotency via Unique Indexes

**Idempotency:** Event `Id` is configured as `BsonId`, so MongoDB automatically creates a unique index on `_id`. This prevents duplicate events with the same ID from being inserted.

**Concurrency:** Create a unique compound index on `{ AggregateId: 1, Version: 1 }` in each events collection. This is the core concurrency mechanism: two concurrent processes calling `GetExpectedNextVersionAsync` may get the same version, but only the first `InsertMany` will succeed. The second will fail with a duplicate-key error, which the implementation catches and wraps in `EventStoreConcurrencyException`.

Index creation should happen during event store initialization — either via an `IMongoConfigurator` registered automatically when the user configures an aggregate, or as part of the event store's first use. Prefer the configurator approach for consistency with the existing `Chaos.Mongo` patterns.

### Read-Model Update in Transaction

`AppendEventsAsync` implementation:

1. Start a transaction via `IMongoHelper.Client.StartSessionAsync()` + `session.WithTransactionAsync(...)`.
2. Insert the events into the events collection.
3. Load the current read model for the aggregate from the database, or create a new instance via parameterless constructor if none exists.
4. Keep the aggregate instance in memory and apply each new event via `event.Execute(aggregate)`. Do not re-fetch from the database between events.
5. Update the aggregate's `Version` property to match the last event's version.
6. Upsert the aggregate document in the read-model collection.
7. If checkpoints are enabled, check whether a new checkpoint should be created (i.e., `newVersion % checkpointInterval == 0`), and if so, insert a checkpoint document.
8. Commit the transaction. If a duplicate-key error occurs during event insertion, the transaction is aborted and `EventStoreConcurrencyException` is thrown.

### Discriminator Configuration

Since an events collection contains multiple concrete event types (e.g., `OrderCreatedEvent`, `OrderShippedEvent`) all deriving from `Event<TAggregate>`, MongoDB needs discriminators to correctly serialize/deserialize.

Use `BsonClassMap.RegisterClassMap<T>()` at runtime during event store configuration. The builder API (see below) collects all event types for an aggregate and registers their class maps and discriminators before the event store is used.

Use a scalar discriminator based on the type name (or a user-specified string) so the `_t` field in MongoDB contains a readable value. Users should be encouraged to provide explicit discriminator names to avoid breaking changes when class names or namespaces change.

### Guid Serialization

Since `Guid` is used for aggregate and event IDs, the MongoDB driver requires explicit `GuidRepresentation` configuration. The event store must ensure a `GuidSerializer` is registered with `GuidRepresentation.Standard` (the preferred representation).

During event store initialization, check if a `GuidSerializer` is already registered (via `BsonSerializer.LookupSerializer<Guid>()`). If not, register one with `GuidRepresentation.Standard`. Since `BsonSerializer.RegisterSerializer()` is not idempotent, guard against duplicate registrations.

The user may have already configured their own `GuidSerializer` via `MongoOptions.ConfigureClientSettings` or globally — respect their configuration if present.

### Configuration / DI Registration

Extend `MongoBuilder` with a new method (or provide an extension method from the `Chaos.Mongo.EventStore` namespace):

```csharp
builder.WithEventStore<OrderAggregate>(es =>
{
    es.WithEvent<OrderCreatedEvent>("OrderCreated")      // discriminator name; optional, defaults to class name
      .WithEvent<OrderShippedEvent>("OrderShipped")
      .WithCollectionPrefix("Orders")                    // optional; defaults to aggregate type name
      .WithCheckpoints(interval: 100);                   // optional; disabled by default
});
```

This registers:
- `IEventStore<OrderAggregate>` as a singleton in DI (backed by `EventStore<OrderAggregate>`)
- An `IMongoConfigurator` that creates the unique index on the events collection
- BsonClassMap registrations for `Event<OrderAggregate>`, `OrderCreatedEvent`, `OrderShippedEvent`, and `OrderAggregate`
- Event discriminators: `OrderCreatedEvent` → `"OrderCreated"`, `OrderShippedEvent` → `"OrderShipped"`
- `GuidSerializer` with `GuidRepresentation.Standard` (if not already registered)

### Checkpoints

A checkpoint is a snapshot of an aggregate's state at a specific version. When enabled:

- A checkpoint collection (e.g., `"OrderAggregate_Checkpoints"`) stores aggregate documents keyed by `(Id, Version)`.
- During `AppendEventsAsync`, after updating the read model, if `newVersion % checkpointInterval == 0`, insert a checkpoint.
- When rebuilding an aggregate (outside of the normal read-model path), the event store can load the nearest checkpoint ≤ the target version and replay only the remaining events.
- `GetEventStream` itself doesn't use checkpoints (it returns raw events), but a future or companion method for loading aggregate state can leverage them.

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
