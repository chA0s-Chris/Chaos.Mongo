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
| `WithCollectionPrefix(string prefix)`           | Sets collection name prefix                            | Aggregate type name |
| `WithCheckpoints(int interval)`                 | Enables checkpoints at specified interval              | Disabled            |
| `WithBulkWriteOptimization()`                   | Opts into the MongoDB 8+ client bulk-write append path | Disabled            |
| `WithMaxAppendRetries(int retries)`             | Additional attempts of a store-assigned append that conflicts with concurrent writers | 3 |
| `WithEventsCollectionSuffix(string suffix)`     | Sets events collection suffix                          | `_Events`           |
| `WithCheckpointCollectionSuffix(string suffix)` | Sets checkpoint collection suffix                      | `_Checkpoints`      |

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

## Exception Handling

| Exception                       | Cause                                                                                                  | Recommended Action                    |
|---------------------------------|--------------------------------------------------------------------------------------------------------|---------------------------------------|
| `MongoConcurrencyException`     | `expectedRevision` does not match; with explicit versions, a concurrent commit; with store-assigned versions, conflicts beyond the retry bound | Reload aggregate and retry            |
| `MongoDuplicateEventException`  | Event with same ID already exists                                                                      | Safe to ignore (idempotent)           |
| `MongoEventValidationException` | Event preconditions not met, or an observational event has no preceding state-changing event          | Fix the event data or aggregate state |
| `MongoEventStoreException`      | The stream contains events but the aggregate's read model is missing                                   | Restore or rebuild the read model     |
| `ArgumentException`             | Invalid input (empty events, mixed aggregates, mixed unset and explicit versions, versions below 1, version gaps, non-sequential batches) | Fix caller code                       |

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
