# Event Store

Event sourcing capabilities for MongoDB, built on top of `Chaos.Mongo`.

## Table of Contents

- [Installation](#installation)
- [Overview](#overview)
- [Quick Start](#quick-start)
- [Core Concepts](#core-concepts)
  - [Aggregates](#aggregates)
  - [Events](#events)
  - [Stream Position and Revision](#stream-position-and-revision)
  - [Observational Events](#observational-events)
  - [Event Store](#event-store)
  - [Aggregate Repository](#aggregate-repository)
- [Configuration](#configuration)
  - [Registering an Event Store](#registering-an-event-store)
  - [Builder Options](#builder-options)
  - [Checkpoints](#checkpoints)
- [Working with Events](#working-with-events)
  - [Defining Events](#defining-events)
  - [Class Hierarchies](#class-hierarchies)
  - [Appending Events](#appending-events)
  - [Reading Events](#reading-events)
- [Concurrency and Idempotency](#concurrency-and-idempotency)
  - [Store-Assigned Versions](#store-assigned-versions)
  - [Optimistic Concurrency](#optimistic-concurrency)
  - [Idempotency](#idempotency)
- [Reconstructing Past States](#reconstructing-past-states)
- [Transactional Outbox Pattern](#transactional-outbox-pattern)
- [Benchmarking Bulk Writes](#benchmarking-bulk-writes)
- [Architecture Decision: Bulk-Write Scope](#architecture-decision-bulk-write-scope)
- [Tamper Evidence](#tamper-evidence)
  - [Threat Model, Guarantees and Limitations](#threat-model-guarantees-and-limitations)
  - [Enabling Protection](#enabling-protection)
  - [Anchoring](#anchoring)
  - [Verifying](#verifying)
  - [Erasing Personal Data](#erasing-personal-data)
  - [MongoDB Roles](#mongodb-roles)
- [Exception Handling](#exception-handling)
- [Best Practices](#best-practices)

## Installation

```bash
dotnet add package Chaos.Mongo.EventStore
```

## Overview

`Chaos.Mongo.EventStore` provides event sourcing capabilities backed by MongoDB:

- **Event Storage**: Append-only event streams per aggregate with automatic versioning
- **Observational Events**: Events that document something about an aggregate, such as a read access, without changing it
- **Read Models**: Automatically maintained read models updated within the same transaction as events
- **Concurrency Control**: Store-assigned positions with revision-based concurrency checks, or explicit positions
  protected by the unique compound index on `(AggregateId, Version)`
- **Idempotency**: Duplicate event detection via unique event IDs
- **Checkpoints**: Optional periodic snapshots to speed up aggregate reconstruction
- **Transactional Callbacks**: Execute additional operations (e.g., outbox messages) within the same transaction

> **Note on GUID Generation (.NET 9+):**  
> The examples in this documentation use `Guid.CreateVersion7()` instead of `Guid.NewGuid()`. Version 7 GUIDs are recommended for event sourcing because they:
> - **Are time-ordered**: GUIDs sort chronologically, improving MongoDB index performance and query efficiency
> - **Reduce index fragmentation**: Sequential IDs prevent B-tree index fragmentation, especially important for high-throughput event streams
> - **Improve locality**: Related events created close in time are stored close together on disk
> - **Maintain uniqueness**: Still globally unique like version 4 GUIDs, but with better database performance characteristics
>
> If you're using .NET 8 or earlier, use `Guid.NewGuid()` instead.

> **Note on generated event IDs:**
> If an event is appended with `Id` left unset, the event store generates one. The version depends on the target
> framework the library was built for: version 7 on .NET 9 and later, version 4 on .NET 8. This applies to both the
> default append path and the bulk-write path, so the two behave identically.
>
> The generated ID is assigned back to the event instance you passed in. Retrying an append with those same
> instances therefore reuses the ID and is deduplicated by the unique `_id` index, while constructing fresh event
> objects produces a new ID and appends them again. Set `Id` explicitly whenever you need idempotency to survive
> rebuilding the events.

## Quick Start

### 1. Define Your Aggregate

```csharp
using Chaos.Mongo.EventStore;

public class OrderAggregate : Aggregate
{
    public string CustomerName { get; set; } = string.Empty;
    public decimal TotalAmount { get; set; }
    public string Status { get; set; } = "Pending";
}
```

### 2. Define Your Events

```csharp
public class OrderCreatedEvent : Event<OrderAggregate>
{
    public string CustomerName { get; set; } = string.Empty;
    public decimal TotalAmount { get; set; }

    public override void Execute(OrderAggregate aggregate)
    {
        aggregate.CustomerName = CustomerName;
        aggregate.TotalAmount = TotalAmount;
        aggregate.Status = "Created";
    }
}

public class OrderShippedEvent : Event<OrderAggregate>
{
    public override void Execute(OrderAggregate aggregate)
    {
        if (aggregate.Status != "Created")
            throw new MongoEventValidationException("Order must be in Created status to ship");

        aggregate.Status = "Shipped";
    }
}
```

### 3. Register the Event Store

```csharp
services.AddMongo("mongodb://localhost:27017", "myDatabase")
    .WithEventStore<OrderAggregate>(es => es
        .WithEvent<OrderCreatedEvent>("OrderCreated")
        .WithEvent<OrderShippedEvent>("OrderShipped")
        .WithCollectionPrefix("Orders"));
```

### 4. Use the Event Store

```csharp
public class OrderService
{
    private readonly IEventStore<OrderAggregate> _eventStore;
    private readonly IAggregateRepository<OrderAggregate> _repository;

    public OrderService(
        IEventStore<OrderAggregate> eventStore,
        IAggregateRepository<OrderAggregate> repository)
    {
        _eventStore = eventStore;
        _repository = repository;
    }

    public async Task<OrderAggregate> CreateOrderAsync(string customer, decimal amount)
    {
        var orderId = Guid.CreateVersion7();
        var version = await _eventStore.GetExpectedNextVersionAsync(orderId);

        return await _eventStore.AppendEventsAsync(
        [
            new OrderCreatedEvent
            {
                Id = Guid.CreateVersion7(),
                AggregateId = orderId,
                Version = version,
                CustomerName = customer,
                TotalAmount = amount
            }
        ]);
    }

    public async Task<OrderAggregate?> GetOrderAsync(Guid orderId)
    {
        return await _repository.GetAsync(orderId);
    }
}
```

## Core Concepts

### Aggregates

An aggregate is a domain object that groups related data and enforces invariants. In event sourcing, aggregates are rebuilt by replaying their events.

**`IAggregate` Interface:**
```csharp
public interface IAggregate
{
    Guid Id { get; set; }
    long Version { get; set; }   // stream position of the last state-changing event
    long Revision { get; set; }  // number of state-changing events applied
    DateTime CreatedUtc { get; set; }
}
```

> **Breaking change:** `IAggregate` gained the `Revision` property. Aggregates deriving from `Aggregate` inherit
> it; aggregates implementing `IAggregate` directly must add `public long Revision { get; set; }`.

**`Aggregate` Base Class:**
```csharp
public class MyAggregate : Aggregate
{
    // Your aggregate state
    public string Name { get; set; } = string.Empty;
}
```

`Version` is the stream position of the last state-changing event and `Revision` counts the state-changing events
applied; see [Stream Position and Revision](#stream-position-and-revision). Both are maintained by the event store.
`CreatedUtc` is set automatically from the first event's timestamp.

### Events

Events represent facts that have occurred. They are immutable and append-only.

**`Event<TAggregate>` Base Class:**

| Property | Description |
|----------|-------------|
| `Id` | Unique event identifier (used for idempotency) |
| `AggregateId` | The aggregate this event belongs to |
| `Version` | Position of the event in its aggregate's stream; set by the caller, or `0` to let the event store assign it |
| `Revision` | Aggregate revision after this event (set automatically; caller-supplied values are overwritten) |
| `AggregateType` | Discriminator for the aggregate type (set automatically) |
| `CreatedUtc` | Timestamp (set automatically if not provided) |

**`Execute` Method:**

Each event must implement `Execute(TAggregate aggregate)` to apply its changes:

```csharp
public class ItemAddedEvent : Event<CartAggregate>
{
    public string ProductId { get; set; } = string.Empty;
    public int Quantity { get; set; }

    public override void Execute(CartAggregate aggregate)
    {
        // Validate preconditions
        if (aggregate.IsClosed)
            throw new MongoEventValidationException("Cannot add items to closed cart");

        // Apply changes
        aggregate.Items.Add(new CartItem(ProductId, Quantity));
    }
}
```

### Stream Position and Revision

Every event has two numbers:

- **`Version`** is the event's position in the stream. Positions start at 1 and have no gaps. The unique
  `(AggregateId, Version)` index, `GetEventStream`, `GetExpectedNextVersionAsync` and the concurrency checks operate on
  positions.
- **`Revision`** is the aggregate revision: the number of state-changing events up to and including this one. An
  [observational event](#observational-events) records the revision it observed.

The aggregate carries the same pair: `Version` is the position of its last state-changing event, and `Revision` is the
number of state-changing events applied to it. Without observational events, both are equal. With them, positions
advance faster than revisions:

| Position (`Version`) | Event | Event `Revision` | Aggregate after append |
|----------------------|-------|------------------|------------------------|
| 1 | `OrderCreated` | 1 | `Version` 1, `Revision` 1 |
| 2 | `OrderViewed` (observational) | 1 | unchanged |
| 3 | `OrderShipped` | 2 | `Version` 3, `Revision` 2 |
| 4 | `OrderViewed` (observational) | 2 | unchanged |

> **Breaking batch behavior:** after each state-changing event executes, the store updates the aggregate's `Version`
> and `Revision` before executing the next event. Earlier releases exposed the pre-batch `Version` to every event
> during append. An `Execute` implementation that reads `aggregate.Version` can therefore produce different final
> state even in a batch containing only state-changing events. Review such implementations when upgrading. Per-event
> metadata keeps append consistent with replay and lets observational validation see the preceding events' state.
> The store owns both values: anything an `Execute` or `Validate` implementation assigns to them is overwritten.

`AppendEventsAsync` validates positions against the stream head — the highest position in the events collection,
including observational events — rather than against the read model. Leave `Version` at `0` to let the event store
assign positions, or use `GetExpectedNextVersionAsync` to obtain the next position yourself.

### Observational Events

Derive from `ObservationalEvent<TAggregate>` for events that only document something about an aggregate, such as a read
access or an export. They become part of the ordered (and, with integrity protection, hash-chained) stream, but they do
not change the aggregate:

```csharp
public class OrderViewedEvent : ObservationalEvent<OrderAggregate>
{
    public string ViewedBy { get; set; } = string.Empty;

    // Optional: reject the event based on the observed state. Must not modify the aggregate.
    protected override void Validate(OrderAggregate aggregate)
    {
        if (aggregate.Status == "Deleted")
            throw new MongoEventValidationException("Deleted orders cannot be viewed");
    }
}
```

Register them like any other event with `WithEvent<OrderViewedEvent>("OrderViewed")`. `Execute` is sealed and calls
`Validate`, which accepts every aggregate by default; when it throws, nothing is persisted.

Appending observational events:

- inserts only the event documents (sealed when integrity protection is enabled) — a batch of only observational events
  writes neither the read model nor a checkpoint and returns the stored read model unchanged,
- leaves the aggregate's `Version` and `Revision` unchanged and records the observed revision on each event,
- can be mixed with state-changing events in one batch; revisions are assigned per event in order.

**First-event rule:** an observational event must observe an existing aggregate. If no state-changing event precedes it
in the stream or earlier in the same batch, the append fails with `MongoEventValidationException` and persists nothing.

Reconstruction excludes observational events on the server, so they are neither transferred nor replayed; see
[Reconstructing Past States](#reconstructing-past-states).

### Event Store

`IEventStore<TAggregate>` is the primary interface for working with events:

```csharp
public interface IEventStore<TAggregate> where TAggregate : class, IAggregate, new()
{
    Task<long> GetExpectedNextVersionAsync(Guid aggregateId, CancellationToken ct = default);

    Task<TAggregate> AppendEventsAsync(
        IEnumerable<Event<TAggregate>> events,
        long? expectedRevision = null,
        Func<IClientSessionHandle, TAggregate, IMongoHelper, CancellationToken, Task>? onBeforeCommit = null,
        CancellationToken ct = default);

    IAsyncEnumerable<Event<TAggregate>> GetEventStream(
        Guid aggregateId,
        long fromVersion = 0,
        long? toVersion = null,
        CancellationToken ct = default);
}
```

- **`GetExpectedNextVersionAsync`**: Returns the next expected position for an aggregate (highest existing position + 1, or 1 if new)
- **`AppendEventsAsync`**: Validates and persists events within a transaction, returns the updated aggregate as a commit-time snapshot.
  Leave `Version` at `0` to let the store assign positions; see [Concurrency and Idempotency](#concurrency-and-idempotency)

> **Breaking change:** `expectedRevision` was inserted as the second parameter of `AppendEventsAsync`. Callers that
> passed `onBeforeCommit` positionally must pass it by name (`onBeforeCommit: ...`) or supply `expectedRevision` first.
- **`GetEventStream`**: Returns events for an aggregate, optionally bounded by version range

### Aggregate Repository

`IAggregateRepository<TAggregate>` provides access to aggregate state:

```csharp
public interface IAggregateRepository<TAggregate> where TAggregate : class, IAggregate, new()
{
    Task<TAggregate?> GetAsync(Guid aggregateId, CancellationToken ct = default);

    Task<TAggregate?> GetAtVersionAsync(Guid aggregateId, long version, CancellationToken ct = default);

    Task<TAggregate?> GetAtRevisionAsync(Guid aggregateId, long revision, CancellationToken ct = default);

    IMongoCollection<TAggregate> Collection { get; }
}
```

- **`GetAsync`**: Returns the current read model (updated on each `AppendEventsAsync`)
- **`GetAtVersionAsync`**: Reconstructs aggregate state at a specific stream position (uses checkpoints if available and skips observational events)
- **`GetAtRevisionAsync`**: Reconstructs aggregate state at a specific revision; see [Reconstructing Past States](#reconstructing-past-states)
- **`Collection`**: Direct access to the MongoDB collection for advanced queries. Queries through it bypass the
  [legacy revision normalization](#documents-written-before-revisions).

> **Breaking change:** `IAggregateRepository<TAggregate>` gained `GetAtRevisionAsync`. Custom implementations,
> including hand-written test doubles, must implement it.

## Configuration

### Registering an Event Store

```csharp
services.AddMongo("mongodb://localhost:27017", "myDatabase")
    .WithEventStore<OrderAggregate>(es => es
        .WithEvent<OrderCreatedEvent>("OrderCreated")
        .WithEvent<OrderShippedEvent>("OrderShipped")
        .WithEvent<OrderCompletedEvent>()  // Uses class name as discriminator
        .WithCollectionPrefix("Orders")
        .WithCheckpoints(interval: 100)
        .WithBulkWriteOptimization()); // Optional: requires MongoDB 8.0+
```

### Builder Options

| Method                                          | Description                                            | Default             |
|-------------------------------------------------|--------------------------------------------------------|---------------------|
| `WithEvent<TEvent>(string? discriminator)`      | Registers an event type with optional discriminator    | Class name          |
| `WithEvent<TEvent>(string? discriminator, Action<EventRegistrationBuilder<TAggregate, TEvent>> configure)` | Registers an event type and marks its personal data for [tamper evidence](#tamper-evidence) | Class name |
| `WithCollectionPrefix(string prefix)`           | Sets collection name prefix                            | Aggregate type name |
| `WithCheckpoints(int interval)`                 | Enables checkpoints at specified interval              | Disabled            |
| `WithBulkWriteOptimization()`                   | Opts into the MongoDB 8+ client bulk-write append path | Disabled            |
| `WithMaxAppendRetries(int retries)`             | Additional attempts of a store-assigned append that conflicts with concurrent writers | 3 |
| `WithEventsCollectionSuffix(string suffix)`     | Sets events collection suffix                          | `_Events`           |
| `WithCheckpointCollectionSuffix(string suffix)` | Sets checkpoint collection suffix                      | `_Checkpoints`      |
| `WithIntegrityProtection(Action<IntegrityProtectionBuilder<TAggregate>>? configure)` | Enables [tamper evidence](#tamper-evidence) for the aggregate type | Disabled |

When bulk-write optimization is enabled, EventStore validates the connected server version before using the optimized
append path. If the server is older than MongoDB 8.0, append operations fail with a clear error instead of silently
falling back to the legacy behavior.

## Benchmarking Bulk Writes

The repository includes a BenchmarkDotNet harness for the EventStore append path:

```bash
dotnet run -c Release --project benchmarks/Chaos.Mongo.EventStore.Benchmarks/Chaos.Mongo.EventStore.Benchmarks.csproj -- --filter "*EventStoreAppendBenchmarks*"
```

This uses BenchmarkDotNet's default job, which is the one to use for reported results. For a fast smoke test while
iterating, append `--job short` — it runs far fewer iterations and its numbers are **not** statistically meaningful:

```bash
dotnet run -c Release --project benchmarks/Chaos.Mongo.EventStore.Benchmarks/Chaos.Mongo.EventStore.Benchmarks.csproj -- --filter "*EventStoreAppendBenchmarks*" --job short
```

What it measures:

- `AppendWithoutBulkWrite` — the current append path without `WithBulkWriteOptimization()`
- `AppendWithBulkWrite` — the opt-in MongoDB 8+ client bulk-write append path
- Multiple event batch sizes via BenchmarkDotNet parameters

What to expect:

Bulk writes reduce the number of round trips per append, not the work each one does. The latency gain therefore scales
with how many operations get collapsed — smallest for a single event without a checkpoint, largest when a checkpoint
insert makes it three collections. Allocation moves the other way: the saving is per append rather than per event, and
is therefore proportionally largest on small batches.

The benchmarks run against a local container, where a round trip costs microseconds against a per-append cost dominated
by transaction coordination and replica-set commit. This understates the optimization — treat local results as a
floor, and expect a larger gain against a remote replica set with real network latency.

Notes:

- The benchmark uses the real `MongoEventStore<TAggregate>` implementation from this repository.
- A MongoDB 8 replica set container is started automatically via Testcontainers, so Docker must be available locally.
- The single-event and medium-batch scenarios run without checkpoints; the checkpoint-forcing scenario exercises the
  three-collection EventStore write path.
- The single-observational-event scenario appends one observational event, which writes only the events collection.
  Observational events require a preceding state-changing event, so it runs against existing streams only.

## Architecture Decision: Bulk-Write Scope

The bulk-write optimization applies only to EventStore. An append already performs multiple writes that must succeed
atomically: event inserts, a read-model upsert, and sometimes a checkpoint insert. MongoDB 8 client bulk writes can
combine those built-in operations into one request while retaining the existing transaction boundary.

MongoQueue is excluded because its current processing loop is intentionally single-item and sequential. Database
operations are separated by user handler execution, and the default query limit is one, so there is no meaningful batch
to submit as one command. Reconsider Queue only if it gains genuine batch processing and measurements show database
round trips, rather than handler work, are the limiting factor.

MongoOutbox is excluded because messages are published and finalized independently, with external publish work and
per-message failure handling between database operations. Batching those state changes would weaken failure isolation
without matching the current control flow. Reconsider Outbox only if it is redesigned around batch claim/finalization
while preserving per-message ownership and retry semantics, and production measurements demonstrate a database
round-trip bottleneck.

MongoDB 8 support for same-collection bulk writes does not change either decision: the limiting factor is the absence of
multiple compatible operations at the same point in each workflow, not collection count.

### Checkpoints

Checkpoints are periodic snapshots of aggregate state that speed up reconstruction for aggregates with many events.

```csharp
.WithEventStore<OrderAggregate>(es => es
    .WithEvent<OrderCreatedEvent>("OrderCreated")
    .WithCheckpoints(interval: 100))  // Checkpoint every 100 events
```

When enabled:
- A checkpoint is created whenever an append's revision crosses a multiple of N:
  `floor(newRevision / N) > floor(oldRevision / N)`. A batch that skips over the exact multiple — for example from
  revision 2 to 4 with an interval of 3 — still creates one. A batch without state-changing events never does.
- Each checkpoint snapshots the state after the batch. It is keyed by the aggregate ID and the stream position of the
  last state-changing event (`_id.Version`) and stores the aggregate revision in `Revision`. Checkpoints therefore no
  longer sit on exact multiples of N.
- `GetAtVersionAsync` and `GetAtRevisionAsync` load the nearest checkpoint at or below the target and replay the
  remaining state-changing events
- The configurators create indexes on `(_id.AggregateId, _id.Version)` and `(_id.AggregateId, Revision)` that serve
  both nearest-checkpoint lookups
- Checkpoints are stored in a separate collection (e.g., `Orders_Checkpoints`)

### Collections Created

For an aggregate configured with prefix `"Orders"`:

| Collection | Purpose |
|------------|---------|
| `Orders` | Read model (current aggregate state) |
| `Orders_Events` | Event stream |
| `Orders_Checkpoints` | Periodic snapshots (if enabled) |
| `Orders_IntegrityState` | Progress of the retroactive sealing sweep (with [tamper evidence](#tamper-evidence)) |

With [tamper evidence](#tamper-evidence) anchoring, the module-level collections `IntegrityAnchors`, `IntegrityAnchorLeaves`,
`IntegrityAnchorEvidence`, `IntegrityAnchorEvidenceChunks` and `IntegrityChangeStreamState` (the change stream's resume
position) are added once for all aggregate types.

## Working with Events

### Defining Events

Events should be self-contained and include all data needed to apply changes:

```csharp
public class PriceChangedEvent : Event<ProductAggregate>
{
    public decimal OldPrice { get; set; }
    public decimal NewPrice { get; set; }
    public string Reason { get; set; } = string.Empty;

    public override void Execute(ProductAggregate aggregate)
    {
        aggregate.Price = NewPrice;
        aggregate.LastPriceChange = CreatedUtc;
    }
}
```

**Validation in Events:**

Throw `MongoEventValidationException` when preconditions aren't met:

```csharp
public override void Execute(OrderAggregate aggregate)
{
    if (aggregate.Status == "Cancelled")
        throw new MongoEventValidationException("Cannot modify cancelled order");

    // Apply changes...
}
```

### Class Hierarchies

Events and aggregates may derive from intermediate base classes. The event store registers their class maps along the
real class hierarchy, so members declared on an intermediate class are stored and read back like the type's own
members:

```csharp
public abstract class AuditedEvent : Event<OrderAggregate>
{
    public string ChangedBy { get; set; } = string.Empty;   // stored with every derived event
}

public class OrderRenamedEvent : AuditedEvent
{
    public string Name { get; set; } = string.Empty;

    public override void Execute(OrderAggregate aggregate) => aggregate.Name = Name;
}
```

The same applies to aggregates deriving from an intermediate class between them and `Aggregate`: inherited members
are part of the read model and of checkpoints.

**Discriminators:** `Event<TAggregate>` is the root of the event hierarchy, so events store an array of discriminators,
one per class map in the hierarchy. An intermediate class adds its own entry, for example
`["Event<OrderAggregate>", "AuditedEvent", "OrderRenamed"]`:

- An intermediate event type registered with `WithEvent` uses the discriminator configured there, regardless of the
  order of the `WithEvent` calls.
- An unregistered intermediate type uses the driver's default: the discriminator of a `[BsonDiscriminator]` attribute,
  otherwise the class name (for generic types including its type arguments, such as `AuditedEvent<String>`). Register
  intermediate types explicitly when their names may change.
- `ObservationalEvent<TAggregate>` itself does not appear in the array, so the discriminators of observational events
  deriving directly from it are `["Event<OrderAggregate>", "OrderViewed"]`. Intermediate classes between
  `ObservationalEvent<TAggregate>` and an observational event appear like any other intermediate class.
- Class maps registered before the event store, including those of intermediate types, are used unchanged.

During deserialization, the driver resolves the last discriminator among all types of the hierarchy. The registration
therefore rejects two types in an aggregate's event hierarchy that share a discriminator with an
`InvalidOperationException`; the driver would otherwise fail every read of that discriminator as ambiguous. This happens, for example, when `OrderPlacedV2 : OrderPlaced` is
registered as `"OrderPlaced"` while `OrderPlaced` is no longer registered and thus defaults to the same name. Resolve it
by registering the intermediate type with a distinct discriminator, such as `WithEvent<OrderPlaced>("OrderPlacedV1")`.

> **Earlier versions** built every event class map directly on `Event<TAggregate>` and every aggregate class map
> directly on `Aggregate`, so members declared on intermediate classes were silently neither stored nor read back.
> Stored documents are not rewritten: events stored with the shorter discriminator array still deserialize, and sealed
> events still verify. The data dropped by earlier versions cannot be recovered. Inherited event members were never
> stored, so replay cannot restore them, and existing read models and checkpoints regain inherited members only when
> later events set them. Until then, reconstruction from such a checkpoint can differ from a replay that starts at the
> beginning of the stream. Consumers who registered their own class maps before the event store were not affected.

> **Breaking change:** Registration now throws `InvalidOperationException` when two types in an aggregate's event
> hierarchy share a discriminator. This includes an unregistered intermediate type whose default name matches another
> event's discriminator, such as `OrderPlacedV2 : OrderPlaced` registered as `"OrderPlaced"`. Such configurations
> previously started; register the intermediate type with a distinct discriminator. Events and read models stored with
> inherited members contain elements that earlier versions do not map, so earlier versions fail to read them unless
> extra elements are ignored. Do not run earlier versions against the same collections after upgrading. An inherited
> member marked `[BsonRequired]` makes events, read models and checkpoints stored before the upgrade unreadable, because
> they lack its element. Remove the attribute, or rewrite or migrate the affected documents, before upgrading.

### Appending Events

The simplest way is to leave `Version` unset and let the event store assign the positions:

```csharp
public async Task<OrderAggregate> ShipOrderAsync(Guid orderId, long revisionShownToUser)
{
    return await _eventStore.AppendEventsAsync(
        [new OrderShippedEvent { Id = Guid.CreateVersion7(), AggregateId = orderId }],
        expectedRevision: revisionShownToUser);
}
```

Alternatively, set explicit positions:

```csharp
public async Task<OrderAggregate> ShipOrderAsync(Guid orderId)
{
    var version = await _eventStore.GetExpectedNextVersionAsync(orderId);

    return await _eventStore.AppendEventsAsync(
    [
        new OrderShippedEvent
        {
            Id = Guid.CreateVersion7(),
            AggregateId = orderId,
            Version = version
        }
    ]);
}
```

**Appending Multiple Events:**

```csharp
var version = await _eventStore.GetExpectedNextVersionAsync(orderId);

var aggregate = await _eventStore.AppendEventsAsync(
[
    new ItemAddedEvent { Id = Guid.CreateVersion7(), AggregateId = orderId, Version = version, ProductId = "P1" },
    new ItemAddedEvent { Id = Guid.CreateVersion7(), AggregateId = orderId, Version = version + 1, ProductId = "P2" },
    new ItemAddedEvent { Id = Guid.CreateVersion7(), AggregateId = orderId, Version = version + 2, ProductId = "P3" }
]);
// aggregate is a commit-time snapshot with all three events applied
```

### Reading Events

```csharp
// Get all events
await foreach (var evt in _eventStore.GetEventStream(aggregateId))
{
    Console.WriteLine($"Version {evt.Version}: {evt.GetType().Name}");
}

// Get events from version 5 onwards
await foreach (var evt in _eventStore.GetEventStream(aggregateId, fromVersion: 5))
{
    // Process event...
}

// Get events between versions 5 and 10
await foreach (var evt in _eventStore.GetEventStream(aggregateId, fromVersion: 5, toVersion: 10))
{
    // Process event...
}
```

## Concurrency and Idempotency

An append runs in one of two modes, chosen by the events' `Version`: either all versions are `0` (store-assigned) or
all are set explicitly. A batch that mixes both throws `ArgumentException`.

In both modes, `expectedRevision` states the aggregate revision the caller prepared the events against. When it is
supplied and differs from the aggregate's current revision, the append throws `MongoConcurrencyException` before
anything is executed or persisted. Because observational events do not change the revision, an `expectedRevision`
check is not disturbed by concurrent read-access logging.

### Store-Assigned Versions

When every event's `Version` is `0`, the event store assigns sequential positions after the stream head and writes them
back to the events. If another writer commits to the same stream concurrently — either a position is taken before the
transaction commits, or a state change is committed between reading the stream head and the read model — the append:

1. reloads the stream head and the read model and waits for a consistent pair,
2. re-checks `expectedRevision` against the fresh state,
3. executes the events again against the fresh aggregate, and
4. retries the transaction.

Both kinds of conflict share one bound on the additional attempts after the first, configured with
`WithMaxAppendRetries(int)` (default 3; `0` disables retries). When the bound is exhausted, the append throws
`MongoConcurrencyException` naming the bound, and the events' versions are reset to `0` so the same instances can be
appended again.

Consequences:

- `Execute` must be deterministic — it already has to be for in-memory validation, and it may now run once per attempt.
- `onBeforeCommit` runs again on retry. The effects of an earlier attempt were rolled back with its aborted transaction.
- Observational appends and state-changing appends with a matching `expectedRevision` succeed despite concurrently
  interleaved writes; only a changed revision fails the append.
- A duplicate event `Id` is an idempotency conflict, never retried, and surfaces as `MongoDuplicateEventException`.

### Optimistic Concurrency

With explicit versions, appends are never retried. The event store detects concurrent writes in two places, and both
report `MongoConcurrencyException`:

1. Process A reads aggregate at version 5, prepares event with version 6
2. Process B reads aggregate at version 5, prepares event with version 6
3. Process A commits successfully
4. Process B fails with `MongoConcurrencyException`

Whether Process B fails before or inside its transaction depends on timing, not on the kind of conflict:

- **Before the transaction** — Process B's append sees the stream head already at version 6 and rejects version 6 as already committed.
- **Inside the transaction** — Process B passed that check before Process A committed, and the unique compound index on `(AggregateId, Version)` rejects the insert.

Positions are compared with the stream head, which includes observational events, so an observational event that
takes position 6 conflicts with a state-changing event prepared for position 6 just the same. The head is read before
the read model, so a read model whose revision differs from the head's means another writer committed a state change in
between; the append fails with `MongoConcurrencyException` without persisting anything instead of executing against
state it did not validate.

A version *above* the stream head + 1 is a gap in the caller's own numbering rather than a conflict, so it throws `ArgumentException` and must not be retried. Resubmitting an event that is already stored throws `MongoDuplicateEventException`, whichever path detects it.

Before executing the events, the append also checks that the read model's revision matches the revision of the stream
head (see above). For explicit versions, a mismatch fails the append; store-assigned appends reload and retry instead.

**Handling Concurrency Conflicts with Explicit Versions:**

```csharp
public async Task UpdateWithRetryAsync(Guid aggregateId, Action<OrderAggregate> update)
{
    const int maxRetries = 3;

    for (int attempt = 0; attempt < maxRetries; attempt++)
    {
        try
        {
            var order = await _repository.GetAsync(aggregateId);
            var version = await _eventStore.GetExpectedNextVersionAsync(aggregateId);

            // Prepare event based on current state...
            var updated = await _eventStore.AppendEventsAsync([event]);
            return;
        }
        catch (MongoConcurrencyException) when (attempt < maxRetries - 1)
        {
            // Retry with fresh state
            await Task.Delay(TimeSpan.FromMilliseconds(50 * (attempt + 1)));
        }
    }

    throw new InvalidOperationException("Failed after max retries");
}
```

### Idempotency

Each event has a unique `Id`. If you retry an operation with the same event ID, the duplicate is detected:

```csharp
var eventId = Guid.CreateVersion7();  // Generate once, use for retries

try
{
    await _eventStore.AppendEventsAsync([new OrderCreatedEvent { Id = eventId, ... }]);
}
catch (MongoDuplicateEventException)
{
    // Event was already processed - safe to ignore
}
```

## Reconstructing Past States

`IAggregateRepository<TAggregate>` reconstructs historical states by replaying events:

```csharp
// The state after the 3rd state-changing event, regardless of how many observational events surround it
var atRevision = await _repository.GetAtRevisionAsync(orderId, revision: 3);

// The state as of stream position 10: everything at or below it
var atPosition = await _repository.GetAtVersionAsync(orderId, version: 10);
```

Both methods load the nearest checkpoint at or below the target — by `_id.Version` for positions, by `Revision` for
revisions — and replay only the state-changing events after it. Observational events are excluded on the server by the
concrete discriminator of each registered observational type, so they are neither transferred nor executed. The
discriminator of an unregistered type is not excluded, so an unknown event type, even one deriving from a registered
observational type, still fails during deserialization.

**Replay cost:** checkpoints are triggered by revisions, not positions. Many observational events between two
checkpoints therefore do not cause a new checkpoint, and reconstruction reads past them (the server skips them, but
still scans their index entries). If read-access logging produces long runs of observational events, choose the
checkpoint interval accordingly.

Events are indexed by position only, so `GetAtRevisionAsync` bounds its replay itself: when the target revision lies
below the stream's current revision, it first looks up the position of the first event above the target and replays
only up to that position. This costs a point read of the stream head plus an index scan from the checkpoint to that
event, instead of reading the rest of the stream.

**Checkpoints created before revisions existed** lack the `Revision` element. The position-based lookup of
`GetAtVersionAsync` still uses them, but `GetAtRevisionAsync` cannot match them and replays from an earlier checkpoint or
from the beginning instead. The result is correct either way; to restore the shortcut, backfill the element once:

```javascript
db.Orders_Checkpoints.updateMany(
  { Revision: { $exists: false } },
  [ { $set: { Revision: "$_id.Version" } } ])
```

Checkpoints are a derived cache, so dropping the checkpoint collection is an alternative; new checkpoints are written as
appends cross the interval.

## Documents Written Before Revisions

Events, read models and checkpoints stored before `Revision` existed lack the element and would deserialize with
`Revision == 0`. A non-empty stream always has a revision of at least 1, so `Revision == 0 && Version > 0` identifies
exactly these documents. Such streams contain only state-changing events, so every library read path — appends,
`GetEventStream`, `GetAsync` and `GetAtVersionAsync` — loads them with `Revision == Version` (for checkpoints, the
source is `_id.Version`).

The normalization is in memory only; no migration is needed and stored event documents are never rewritten. Adding an
element would change the bytes of sealed events and break their hash chain. Read models and checkpoints gain the
element as soon as a later append replaces or writes them. Direct queries through `IAggregateRepository.Collection` or
the raw collections bypass the normalization and see `Revision` 0 for documents that were not rewritten since.

## Transactional Outbox Pattern

Use the `onBeforeCommit` callback to enqueue outbox messages within the same transaction:

```csharp
await _eventStore.AppendEventsAsync(
    [new OrderCreatedEvent { ... }],
    onBeforeCommit: async (session, aggregate, helper, ct) =>
    {
        await _outbox.AddMessageAsync(
            session,
            new OrderPlacedMessage
            {
                OrderId = aggregate.Id,
                CustomerName = aggregate.CustomerName,
                TotalAmount = aggregate.TotalAmount
            },
            correlationId: aggregate.Id.ToString(),
            cancellationToken: ct);
    });
```

This ensures the outbox message is only persisted if the events are successfully committed.
Register the outbox separately via `WithOutbox(...)` and inject `IOutbox` into the service that appends events.

## Tamper Evidence

Tamper evidence makes it possible to prove to an independent third party, such as a court or an expert witness, that events were not modified, deleted, reordered or backdated since a given point in time. It is opt-in per aggregate type:

- Every event is sealed into a per-stream hash chain over its stored bytes, recorded in a reserved `_integrity` element.
- Events written before protection was enabled are sealed retroactively by a background sweep.
- A background anchoring run periodically builds a Merkle tree over the changed stream heads of all protected aggregate types and has its root timestamped by RFC 3161 timestamp authorities, typically eIDAS-qualified ones.
- Personal data can be erased without breaking the chain or any anchor.
- The evidence uses open standards, and the [specification](tamper-evidence-specification.md) defines it at byte level, so it can be verified without Chaos.Mongo: the repository contains a Python [reference verifier](../tools/tamper-evidence-verifier/verify.py) that works on `mongodump` exports with OpenSSL.

With protection disabled, stored documents and append behavior are unchanged.

### Threat Model, Guarantees and Limitations

The goal is tamper *evidence*, not prevention: anyone with write access to the database, including its administrators, can still change data, but not without leaving a trace. Mechanisms only the operator controls, such as secret keys, add no evidentiary value, because the operator is the party whose integrity is in question. Evidence therefore comes from the anchors' timestamps, issued by third parties.

| Change | Detected by |
|---|---|
| Modifying, inserting or reordering events, changing a seal mode | Stream verification, always |
| Deleting events followed by later events | Stream verification, always |
| Deleting the newest events of a stream, or a whole stream | Comparison with completed anchors, for anchored versions |
| Rewriting or rolling back a stream consistently | Comparison with completed anchors, for anchored versions; if the anchors were rebuilt as well, only a trusted head or anchors replicated outside the database |
| Backdating events | Anchors: an event existed no later than the token time of the first anchor that covers it |
| Deleting or changing anchor leaves, also together with the streams they cover | Anchor-chain verification, which recomputes every anchor's Merkle root from its leaves |
| Deleting the newest anchors together with the streams they cover | Only against a trusted head kept outside the database |
| Rebuilding or appending anchors consistently, for example to hide unanchored events from the completeness check | Only a trusted head kept outside the database, for anchors up to it, or anchors replicated when they completed: digests and roots involve no secret, and timestamp tokens (`VerifyEventAnchorAsync`) bound when an anchor existed, not who built it, so rebuilt anchors stamped again only show later token times |
| Changing a read model or checkpoint | Derived-data verification, by replay and compare |
| Erasing personal data without an erasure record | Stream verification reports it, without breaking the chain |

Limitations:

- An anchor covers events from its creation. The interval between anchoring runs is a window in which unanchored events can be changed undetectably, and events are only protected against truncation once anchored.
- `CreatedUtc` is asserted by the caller. The trustworthy time bound is the token time of the anchor that covers an event; the anchoring delay alert, not `CreatedUtc`, tells whether anchoring keeps up.
- Retroactively sealed events are protected since the anchor that first covers them, never since their creation. Before that anchor, an attacker who strips `_integrity` from a suffix of a stream can have it re-sealed; the `Append` → `Retroactive` transition this leaves is reported as an informational finding.
- The completion hook should replicate every anchor to WORM storage. Its sequence number and digest serve as the trusted head against which deleting the newest anchors becomes detectable.
- Renaming an aggregate class keeps its chains verifiable, because the genesis value uses the stored type name, but anchor leaves record the name configured when they were anchored, so the anchor coverage splits between the old and the new name.
- Removing, substituting or partly removing the confirming CRLs of a confirmed or revoked token is detected, but removing them together with resetting its stored revocation status is not. Replicate anchors and their evidence to WORM storage.
- Integrity of data outside MongoDB, such as blobs in object storage, is not covered. Include content hashes in your events.

### Enabling Protection

```csharp
services.AddMongo("mongodb://localhost:27017", "myDatabase")
    .WithEventStore<DocumentAggregate>(eventStore => eventStore
        .WithEvent<DocumentShared>("DocumentShared", e => e
            .WithPersonalData(x => x.RecipientEmail)
            .WithPersonalData(x => x.Recipient.Name))
        .WithEvent<DocumentArchived>("DocumentArchived")
        .WithCollectionPrefix("Documents")
        .WithIntegrityProtection(protection => protection
            .WithSealingSweepInterval(TimeSpan.FromHours(24))
            .WithSealingChunkSize(1000)));
```

**Mark personal data before enabling protection.** Each marked value is committed to with a random salt instead of being hashed itself, which is what makes erasure possible later. A value sealed while it was not marked can never be erased without breaking the chain. Marked members must be mapped and nullable; paths may traverse embedded documents but not arrays, and arrays and complex objects are marked and erased as a whole. Element names are resolved through the class maps, so `[BsonElement]` renames are respected.

**Register class maps, serializers and conventions before the event store.** Marking personal data resolves the class maps of the marked members' types when the event store is registered, which freezes them. Register custom class maps, serializers and convention packs for those types first.

**Do not pre-register checkpoint class maps.** With protection enabled, checkpoints record the chain hash at their version through the event store's own class map, so a `CheckpointDocument<TAggregate>` class map registered before the event store fails the registration.

**Existing events** are sealed retroactively by a background sweep that runs once at startup and then at the configured interval, on one instance at a time. It is resumable and reports its progress through `IEventStoreIntegrity<TAggregate>.GetSealingStatusAsync`. An append to a stream with unsealed events seals up to one chunk of them first; a stream with more fails to append until the sweep caught it up.

### Anchoring

Anchoring is configured once for all protected aggregate types:

```csharp
services.AddMongo("mongodb://localhost:27017", "myDatabase")
    .WithEventStore<DocumentAggregate>(eventStore => eventStore /* ... */.WithIntegrityProtection())
    .WithIntegrityAnchoring(anchoring => anchoring
        .WithRfc3161TimestampAuthority("qualified-tsa", new Uri("https://tsa.example/timestamp"), tsa =>
        {
            // On .NET 8, use new X509Certificate2("tsa-root.cer").
            tsa.TrustedRoots.Add(X509CertificateLoader.LoadCertificateFromFile("tsa-root.cer"));
            tsa.PolicyId = "1.2.3.4.5";
            tsa.HttpClientFactory = sp => sp.GetRequiredService<IHttpClientFactory>().CreateClient("tsa");
        })
        .WithInterval(TimeSpan.FromMinutes(5))
        .WithMinimumTimestampTokens(1)
        .WithDelayThreshold(TimeSpan.FromHours(1))
        .OnAnchorCompleted(async (sp, anchor, ct) =>
        {
            // Replicate the anchor, its tokens and its leaves to WORM storage.
            await foreach (var leaf in sp.GetRequiredService<AnchorLeafReader>().ReadLeavesAsync(anchor.Id, ct))
            {
                // ...
            }
        }));
```

- **Choosing a timestamp authority.** Use an authority whose tokens a court accepts, typically an eIDAS-qualified one, and configure its trusted roots and policy. Configure the roots explicitly rather than relying on the system trust store: verification compares the chain it builds with the chain stored with the token, and a trust store that changed over the years may lead to another path, for example through a cross-signed root, so a genuine token would no longer verify. No authority is configured by default, so anchoring causes no data flow or cost until you add one. Several authorities stamp each anchor for redundancy, for example in case one loses its qualified status; `WithMinimumTimestampTokens` sets how many valid tokens an anchor needs to complete. Authorities with an API other than RFC 3161 over HTTP are added with `WithTimestampAuthority` and must return RFC 3161 tokens. Anchoring checks every token before storing it, as it does for the built-in authority: one DER-encoded token over the anchor digest, signed with the returned chain's first certificate, a chain that names each next certificate's subject as its issuer and ends with a self-issued root, a time equal to the returned time to the millisecond, and a signer and algorithms that verification accepts. Names are compared after decoding, so an issuer name in another string type or ASCII case than the issuer's subject still links. A token that fails counts as a failed request. The chain's trust and the policy are checked at verification, so trust a custom authority's roots with `WithIntegrityVerification`.
- **Choosing the interval.** The interval (default 5 minutes) bounds both the window in which unanchored events are unprotected and the gap between `CreatedUtc` and the proven time. One timestamp is requested per interval regardless of event volume, about 8,600 per month at the default; intervals without changes request none.
- **Failures** of an authority never block appends: anchoring retries, and an anchor delay beyond the threshold is reported through logging and the `Chaos.Mongo.EventStore.Integrity` meter, whose instruments are:

  | Instrument | Type | Measures |
  |---|---|---|
  | `chaos.mongo.eventstore.anchoring.delay` | Histogram (s) | Age of the oldest unanchored candidate after a run. |
  | `chaos.mongo.eventstore.anchoring.delay_exceeded` | Counter | Runs after which the anchor delay exceeded its threshold. |
  | `chaos.mongo.eventstore.anchoring.revocation_confirmation_delay` | Histogram (s) | Age of the oldest token whose revocation status is unconfirmed. |
  | `chaos.mongo.eventstore.anchoring.revocation_confirmation_delay_exceeded` | Counter | Runs after which the revocation confirmation delay exceeded its threshold. |
  | `chaos.mongo.eventstore.anchoring.revoked_timestamps` | Counter | Tokens whose chain a confirming CRL lists as revoked at or before the token's time. |
- **Revocation confirmation.** The CRLs captured when a token is issued usually predate it. After a grace period (default 24 hours, `WithRevocationGracePeriod`), later anchoring runs fetch CRLs issued after the token's time and store them with the token. Tokens still unconfirmed beyond a threshold (default 72 hours, `WithRevocationConfirmationDelayThreshold`) are reported like the anchor delay. A token whose chain turns out to be revoked at or before its time is no evidence; its anchor's events stay protected by the next anchor, from that anchor's time.
- **Evidence collections.** Tokens, certificate chains and CRLs are stored once per distinct content in `IntegrityAnchorEvidence` and `IntegrityAnchorEvidenceChunks`, referenced by hash from the anchors in `IntegrityAnchors`; leaves and their inclusion proofs are stored in `IntegrityAnchorLeaves`. Evidence is only ever inserted, so the anchoring role needs no `update` or `remove` on it.

**Operating requirements.** Anchoring collects the changed stream heads from a database-wide change stream:

- It needs a replica set or a sharded cluster, as the event store's transactions do; a single-node replica set suffices.
- It needs MongoDB 5.1 or later, because the recovery scan takes its cluster time from a snapshot read.
- The oplog window must cover the longest expected anchoring downtime plus a full recovery scan. Otherwise anchoring falls back to scanning all stream heads, which takes longer with millions of streams; a scan that outlasts the window repeats and is logged as an error.
- The anchoring credentials need the `changeStream` and `find` actions on the database.
- MongoDB-compatible services, such as Amazon DocumentDB or Azure Cosmos DB, restrict change streams and are not supported without verification.
- On sharded clusters, `mongos` merges the shards' change streams, which adds server load.

### Verifying

Every protected aggregate type registers `IEventStoreIntegrity<TAggregate>`:

```csharp
var stream = await integrity.VerifyStreamAsync(documentId);
if (!stream.IsIntact)
{
    logger.LogError("Stream {Id} is broken at version {Version}: {Failure}", documentId, stream.FirstBrokenVersion, stream.Failure);
}

var anchor = await integrity.VerifyEventAnchorAsync(documentId, version);   // inclusion proof, digest and tokens
var derived = await integrity.VerifyDerivedDataAsync(documentId);           // read model and checkpoints by replay

await foreach (var finding in integrity.CheckCompletenessAsync(trustedHead)) // every event of the type anchored?
{
    // Unanchored, StreamDeleted, StreamTruncated, RetroactivelyProtected, Unsealed, MalformedEvent, AnchorChainBroken
}
```

- `VerifyStreamAsync` recomputes the chain from the stored bytes, reports the first broken version, and returns every version's hash, previous hash, seal mode, sealing anchor and personal-data status. The verified versions are also compared with the stream's leaves in completed anchors, whose inclusion proofs are checked, below the break for a broken chain. A leaf of the stream that cannot be read, or whose anchor cannot be read, breaks the stream as `AnchoredLeafInvalid`. The leaves are bound to the anchor digests by `IAnchorVerifier.VerifyAnchorChainAsync`, so run it as well. The per-version data is held in memory. Result equality compares the verdict; compare hashes and lists with `SequenceEqual`, because records compare byte arrays and collections by reference.
- `VerifyEventAnchorAsync` verifies the chain up to the anchored version, the leaf and its Merkle inclusion proof, the anchor digest and every stored token: its signature, imprint, certificate chain at the token's time and its revocation status, recomputed from the stored CRLs, whose signatures are verified, rather than read from the stored status.
- `VerifyDerivedDataAsync` reads the stream, the read model and the checkpoints in one snapshot session, which needs MongoDB 5.0 or later, so concurrent appends cause no false reports. It compares the states without `CreatedUtc`, which records the append time and cannot be derived from the events, and reports in `CreationTimeConsistent` whether the read model and the checkpoints agree on it.
- `CheckCompletenessAsync` is an audit operation over all streams of the type; its cost grows with events and leaves, and its memory with the largest stream and the number of completed anchors, about 8,600 small entries per month at the default interval. It verifies the chain of anchors first, optionally against a trusted head, and reports event documents whose identifier or version cannot be read as `MalformedEvent`; they count as missing, so their stream may also be reported as truncated or deleted. It reads through two server cursors at the pace you consume the findings: a pause longer than the server's cursor timeout (10 minutes by default), or a long run of streams without leaves, can let a cursor expire with `MongoCursorNotFoundException`. Consume the findings promptly and run the check again in that case.
- `IAnchorVerifier.VerifyAnchorChainAsync` verifies the chain of anchors of all types: sequence numbers, digests, every anchor's Merkle root recomputed from its stored leaves, and the completed status of every anchor but the newest and of every anchor up to a trusted head. Its cost grows with the number of leaves. It proves consistency only: timestamp tokens, which `VerifyEventAnchorAsync` verifies, bound when an anchor existed, not who built it, so detecting rebuilt anchors needs a trusted head or anchors replicated outside the database. The same holds for the coverage `CheckCompletenessAsync` reports.

Tokens are only valid when their authority's roots are trusted. The roots of RFC 3161 authorities configured for anchoring are trusted automatically; custom authorities, and containers that only verify, configure them with `WithIntegrityVerification`:

```csharp
services.AddMongo(auditConnectionString, "myDatabase")
    .WithEventStore<DocumentAggregate>(eventStore => eventStore /* same registration */
        .WithIntegrityProtection(protection => protection.WithoutSealingSweep()))
    .WithIntegrityVerification(verification => verification
        .WithTimestampAuthorityTrust("qualified-tsa", trust => trust.TrustedRoots.Add(tsaRoot))
        .WithRevocationGracePeriod(TimeSpan.FromHours(24)));
```

A container with read-only credentials must not seal, so it registers no sealing sweep with `WithoutSealingSweep()`; `GetSealingStatusAsync` still reports the progress of the sweep elsewhere. When it uses anchor collections with other names, `WithAnchorCollections` names them; in a container that also anchors, the names must match the anchoring configuration.

Only CRLs of issuers whose certificates are part of the stored chain can be verified. A token whose chain points to an indirect CRL issuer stays unconfirmed; it is still valid evidence.

### Erasing Personal Data

```csharp
using var scope = erasureServices.CreateScope();
var eraser = scope.ServiceProvider.GetRequiredService<IPersonalDataEraser<DocumentAggregate>>();
await eraser.ErasePersonalDataAsync(documentId, new ErasureRequest("GDPR-2026-0042", versions: [3], paths: ["RecipientEmail"]));
```

In one transaction, erasure sets the selected values to `null`, removes their salts, appends a `PersonalDataErased<TAggregate>` event that records them and the reference, and rebuilds the read model and the affected checkpoints by replay. The chain and every anchor stay verifiable, and verification distinguishes recorded erasures from values nulled without a record. The reference must not contain personal data itself.

- **Replay contract.** Event `Execute` implementations must handle `null` in marked members, because replay keeps working on erased events. Read-model fields written outside `Execute` are reset by an erasure.
- **Event streams** returned by `GetEventStream` contain the `PersonalDataErased<TAggregate>` event; it is observational and changes no state. Only the eraser appends it: `AppendEventsAsync` rejects one created elsewhere, so no erasure record can be forged through the event store. The guard applies to every event store, protected or not, so tooling that copies or migrates streams containing a past erasure must write their documents directly instead of appending them.
- Mapping a data subject to its streams, and erasing from backups, outbox messages, external blobs and logs, remain the application's responsibility.

### MongoDB Roles

Grant least privilege:

| Role | Permissions |
|---|---|
| Application | `insert` and `find` on the events collection; the usual permissions on the read-model and checkpoint collections; while the sealing sweep runs in it, `insert`, `update` and `find` on `{Prefix}_IntegrityState`, and `insert`, `update`, `remove` and `find` on the distributed lock collection, whose lock the sweep releases by removing it. A container with `WithoutSealingSweep()` needs none of these. |
| Retroactive sealing | The sweep's permissions above, and temporarily `update` on the events collection. |
| Anchoring | `changeStream` and `find` on the database; `insert`, `update`, `remove` and `find` on `IntegrityAnchors`, `IntegrityAnchorLeaves`, `IntegrityChangeStreamState` and the distributed lock collection; `insert` and `find` on the evidence collections. |
| Erasure | The application's permissions on the events, read-model and checkpoint collections, and `update` on the events collection, with separate credentials. Register its event stores with `WithoutSealingSweep()`, so it needs no sweep permissions. |
| Verification | `find` on the events, read-model and checkpoint collections, on `{Prefix}_IntegrityState`, which `GetSealingStatusAsync` reads, and on the anchor and evidence collections. |

MongoDB roles cannot restrict `update` to documents without `_integrity`, so sealing needs it temporarily:

1. Enable protection.
2. Let the sealing sweep seal all existing events.
3. Wait until `GetSealingStatusAsync` reports a pass that completed without failures (`IsCompleteWithoutFailures`). A pass also completes when it skipped streams it could not seal; it logs them and counts them in `StreamsFailed`. Fix those streams, for example from the logged errors and `CheckCompletenessAsync`'s `Unsealed` findings, and wait for the next pass.
4. Revoke `update`, leaving the application with insert and find on the events collection.

Erasure permanently needs `update`. Run it in a separate process or tool, or a separate dependency-injection container with its own connection and credentials, so the application role keeps insert and find only. Register that container's event stores with `WithIntegrityProtection(protection => protection.WithoutSealingSweep())`; otherwise its sweep fails for lack of permissions and logs errors.

## Exception Handling

| Exception                       | Cause                                                                                                  | Recommended Action                    |
|---------------------------------|--------------------------------------------------------------------------------------------------------|---------------------------------------|
| `MongoConcurrencyException`     | `expectedRevision` does not match; with explicit versions, a concurrent commit; with store-assigned versions, conflicts beyond the retry bound | Reload aggregate and retry            |
| `MongoDuplicateEventException`  | Event with same ID already exists                                                                      | Safe to ignore (idempotent)           |
| `MongoEventValidationException` | Event preconditions not met, or an observational event has no preceding state-changing event          | Fix the event data or aggregate state |
| `MongoEventStoreException`      | The stream contains events but the aggregate's read model is missing                                   | Restore or rebuild the read model     |
| `ArgumentException`             | Invalid input (empty events, mixed aggregates, mixed unset and explicit versions, versions below 1, version gaps, non-sequential batches, a `PersonalDataErased<TAggregate>` event not created by the eraser) | Fix caller code                       |

```csharp
try
{
    var aggregate = await _eventStore.AppendEventsAsync(events);
    // aggregate is a commit-time snapshot — read freely, but mutations won't persist
}
catch (MongoConcurrencyException ex)
{
    _logger.LogWarning(ex, "Concurrency conflict, retrying...");
    // Reload and retry
}
catch (MongoDuplicateEventException)
{
    _logger.LogInformation("Event already processed (idempotent)");
    // Continue normally
}
catch (MongoEventValidationException ex)
{
    _logger.LogError(ex, "Event validation failed");
    throw;  // Business logic error
}
```

## Best Practices

### Event Design

- **Make events immutable**: Once persisted, events should never change
- **Include all required data**: Events should be self-contained
- **Use explicit discriminators**: Avoid breaking changes when renaming classes
  ```csharp
  .WithEvent<OrderCreatedEvent>("OrderCreated")  // Explicit name
  ```
- **Version carefully**: If event schema changes, consider event upcasting strategies

### Aggregate Design

- **Keep aggregates focused**: One aggregate per bounded context concept
- **First event creates**: The first event should initialize all required state
- **Validate in events**: Use `MongoEventValidationException` for business rule violations

### Performance

- **Enable checkpoints for large aggregates**: Reduces replay time
  ```csharp
  .WithCheckpoints(interval: 100)
  ```
- **Use `GetAsync` for current state**: Reads from the maintained read model, not event replay
- **Use `Collection` for queries**: Direct MongoDB queries on the read model collection

### Concurrency

- **Prefer store-assigned versions with `expectedRevision`**: The event store retries position races itself, and
  observational events appended concurrently do not cause conflicts
- **Handle `MongoConcurrencyException`**: Reload the aggregate when its revision changed, and implement retry logic for
  explicit versions
- **Use idempotent event IDs**: Generate event IDs deterministically when possible for safe retries
- **Keep transactions short**: The event store validates outside the transaction to minimize lock time
