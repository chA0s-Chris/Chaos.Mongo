# Event Store

Event sourcing capabilities for MongoDB, built on top of `Chaos.Mongo`.

## Table of Contents

- [Installation](#installation)
- [Overview](#overview)
- [Quick Start](#quick-start)
- [Core Concepts](#core-concepts)
  - [Aggregates](#aggregates)
  - [Events](#events)
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
- **Read Models**: Automatically maintained read models updated within the same transaction as events
- **Concurrency Control**: Optimistic concurrency via unique compound index on `(AggregateId, Version)`
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
    long Version { get; set; }
    DateTime CreatedUtc { get; set; }
}
```

**`Aggregate` Base Class:**
```csharp
public class MyAggregate : Aggregate
{
    // Your aggregate state
    public string Name { get; set; } = string.Empty;
}
```

The `Version` property tracks the number of events applied. `CreatedUtc` is set automatically from the first event's timestamp.

### Events

Events represent facts that have occurred. They are immutable and append-only.

**`Event<TAggregate>` Base Class:**

| Property | Description |
|----------|-------------|
| `Id` | Unique event identifier (used for idempotency) |
| `AggregateId` | The aggregate this event belongs to |
| `Version` | Monotonically increasing version per aggregate |
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

### Event Store

`IEventStore<TAggregate>` is the primary interface for working with events:

```csharp
public interface IEventStore<TAggregate> where TAggregate : class, IAggregate, new()
{
    Task<long> GetExpectedNextVersionAsync(Guid aggregateId, CancellationToken ct = default);

    Task<TAggregate> AppendEventsAsync(
        IEnumerable<Event<TAggregate>> events,
        Func<IClientSessionHandle, TAggregate, IMongoHelper, CancellationToken, Task>? onBeforeCommit = null,
        CancellationToken ct = default);

    IAsyncEnumerable<Event<TAggregate>> GetEventStream(
        Guid aggregateId,
        long fromVersion = 0,
        long? toVersion = null,
        CancellationToken ct = default);
}
```

- **`GetExpectedNextVersionAsync`**: Returns the next expected version for an aggregate (highest existing version + 1, or 1 if new)
- **`AppendEventsAsync`**: Validates and persists events within a transaction, returns the updated aggregate as a commit-time snapshot
- **`GetEventStream`**: Returns events for an aggregate, optionally bounded by version range

### Aggregate Repository

`IAggregateRepository<TAggregate>` provides access to aggregate state:

```csharp
public interface IAggregateRepository<TAggregate> where TAggregate : class, IAggregate, new()
{
    Task<TAggregate?> GetAsync(Guid aggregateId, CancellationToken ct = default);

    Task<TAggregate?> GetAtVersionAsync(Guid aggregateId, long version, CancellationToken ct = default);

    IMongoCollection<TAggregate> Collection { get; }
}
```

- **`GetAsync`**: Returns the current read model (updated on each `AppendEventsAsync`)
- **`GetAtVersionAsync`**: Reconstructs aggregate state at a specific version (uses checkpoints if available)
- **`Collection`**: Direct access to the MongoDB collection for advanced queries

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
- A checkpoint is created every N versions during `AppendEventsAsync`
- `GetAtVersionAsync` loads the nearest checkpoint and replays remaining events
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

### Optimistic Concurrency

The event store uses a unique compound index on `(AggregateId, Version)` to prevent concurrent writes:

1. Process A reads aggregate at version 5, prepares event with version 6
2. Process B reads aggregate at version 5, prepares event with version 6
3. Process A commits successfully
4. Process B fails with `MongoConcurrencyException`

**Handling Concurrency Conflicts:**

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

| Exception | Cause | Recommended Action |
|-----------|-------|-------------------|
| `MongoConcurrencyException` | Another process inserted an event with the same version | Reload aggregate and retry |
| `MongoDuplicateEventException` | Event with same ID already exists | Safe to ignore (idempotent) |
| `MongoEventValidationException` | Event preconditions not met | Fix the event data or aggregate state |
| `ArgumentException` | Invalid input (empty events, mixed aggregates, non-sequential versions) | Fix caller code |

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

- **Handle `MongoConcurrencyException`**: Implement retry logic for concurrent updates
- **Use idempotent event IDs**: Generate event IDs deterministically when possible for safe retries
- **Keep transactions short**: The event store validates outside the transaction to minimize lock time
