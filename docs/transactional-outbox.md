# Chaos.Mongo.Outbox

Transactional outbox support for MongoDB, built on top of `Chaos.Mongo`.

## Table of Contents

- [Installation](#installation)
- [Overview](#overview)
- [Quick Start](#quick-start)
- [Core Concepts](#core-concepts)
  - [IOutbox](#ioutbox)
  - [IOutboxPublisher](#ioutboxpublisher)
  - [OutboxMessage](#outboxmessage)
  - [IOutboxProcessor](#ioutboxprocessor)
- [Configuration](#configuration)
  - [Registering the Outbox](#registering-the-outbox)
  - [Builder Options](#builder-options)
  - [Processor Startup](#processor-startup)
- [Writing Messages](#writing-messages)
  - [Transaction Requirement](#transaction-requirement)
  - [Message Type Registration](#message-type-registration)
  - [Correlation IDs](#correlation-ids)
- [Processing Behavior](#processing-behavior)
  - [Delivery Semantics](#delivery-semantics)
  - [Retries and Backoff](#retries-and-backoff)
  - [Locking and Stale Lock Recovery](#locking-and-stale-lock-recovery)
  - [Ordering](#ordering)
  - [Retention and Cleanup](#retention-and-cleanup)
- [Event Store Integration](#event-store-integration)
- [Best Practices](#best-practices)

## Installation

```bash
dotnet add package Chaos.Mongo.Outbox
```

## Overview

`Chaos.Mongo.Outbox` implements the transactional outbox pattern for MongoDB:

- **Atomic persistence**: outbox messages are inserted in the same MongoDB transaction as the business write
- **At-least-once delivery**: a background processor publishes pending messages to an external system
- **Typed payloads**: payloads are stored as `BsonDocument` but written and read as typed .NET classes
- **Durable retries**: retry count and next-attempt scheduling are stored in MongoDB
- **Crash recovery**: stale message locks can be reclaimed by another processor instance
- **Optional cleanup**: processed and failed messages can be removed automatically via TTL indexes

MongoDB transaction support is required. The outbox is intended for replica set or sharded deployments where multi-document transactions are available.

## Quick Start

### 1. Define Message Payloads

```csharp
public class OrderPlacedMessage
{
    public string CustomerName { get; set; } = string.Empty;
    public string OrderId { get; set; } = string.Empty;
    public decimal TotalAmount { get; set; }
}
```

### 2. Implement a Publisher

```csharp
using Chaos.Mongo.Outbox;

public class NotificationsPublisher : IOutboxPublisher
{
    public Task PublishAsync(OutboxMessage message, CancellationToken cancellationToken = default)
    {
        var payload = message.DeserializePayload<OrderPlacedMessage>();

        return PublishToBrokerAsync(
            topic: message.Type,
            body: payload,
            correlationId: message.CorrelationId,
            cancellationToken);
    }

    private static Task PublishToBrokerAsync(
        string topic,
        OrderPlacedMessage body,
        string? correlationId,
        CancellationToken cancellationToken) => Task.CompletedTask;
}
```

### 3. Register the Outbox

```csharp
using Chaos.Mongo;
using Chaos.Mongo.Outbox;

services.AddMongo("mongodb://localhost:27017", "myDatabase")
    .WithOutbox(o => o
        .WithPublisher<NotificationsPublisher>()
        .WithMessage<OrderPlacedMessage>("OrderPlaced")
        .WithMaxRetries(5)
        .WithPollingInterval(TimeSpan.FromSeconds(5))
        .WithAutoStartProcessor());
```

### 4. Write Data and Outbox Messages in One Transaction

```csharp
public class OrderService
{
    private readonly IMongoHelper _mongo;
    private readonly IOutbox _outbox;

    public OrderService(IMongoHelper mongo, IOutbox outbox)
    {
        _mongo = mongo;
        _outbox = outbox;
    }

    public async Task CreateOrderAsync(Order order)
    {
        await _mongo.ExecuteInTransaction(async (helper, session, ct) =>
        {
            var orders = helper.GetCollection<Order>();
            await orders.InsertOneAsync(session, order, cancellationToken: ct);

            await _outbox.AddMessageAsync(
                session,
                new OrderPlacedMessage
                {
                    OrderId = order.Id.ToString(),
                    CustomerName = order.CustomerName,
                    TotalAmount = order.TotalAmount
                },
                correlationId: order.Id.ToString(),
                cancellationToken: ct);
        });
    }
}
```

If the transaction commits, both the order and the outbox message are persisted. If the transaction aborts, neither is persisted.

## Core Concepts

### IOutbox

`IOutbox` is the write-side API:

```csharp
public interface IOutbox
{
    Task AddMessageAsync<TPayload>(
        IClientSessionHandle session,
        TPayload payload,
        string? correlationId = null,
        CancellationToken cancellationToken = default)
        where TPayload : class, new();
}
```

- `AddMessageAsync` inserts a message into the outbox collection
- The provided session must already have an active transaction
- `TPayload` must be registered via `WithMessage<TPayload>()`
- `correlationId` is optional and stored with the message for tracing

### IOutboxPublisher

`IOutboxPublisher` is the delivery abstraction you implement for your broker, queue, API, or webhook target:

```csharp
public interface IOutboxPublisher
{
    Task PublishAsync(OutboxMessage message, CancellationToken cancellationToken = default);
}
```

- Throw an exception to signal a failed publish attempt
- The processor will persist the failure, increment `RetryCount`, and schedule the next retry
- The publisher receives the full `OutboxMessage`, including `Type`, `CorrelationId`, and raw `Payload`

### OutboxMessage

`OutboxMessage` is the MongoDB document stored in the outbox collection.

| Property | Description |
|----------|-------------|
| `Id` | MongoDB `ObjectId` used for approximate insertion ordering |
| `Type` | Message discriminator registered via `WithMessage<TPayload>()` |
| `Payload` | Raw `BsonDocument` payload |
| `CorrelationId` | Optional correlation identifier |
| `CreatedUtc` | Time the message was inserted |
| `State` | `Pending`, `Processed`, or `Failed` |
| `ProcessedUtc` | When the message was successfully published |
| `FailedUtc` | When the message permanently failed |
| `RetryCount` | Number of failed publish attempts |
| `NextAttemptUtc` | When the message becomes eligible for retry |
| `Error` | Last failure message |
| `IsLocked` | Indicates whether a processor currently owns the message |
| `LockedUtc` | When the current lock was acquired |
| `LockId` | Ownership token used to prevent stale processors from overwriting newer results |

For typed access to the payload:

```csharp
var payload = message.DeserializePayload<OrderPlacedMessage>();
```

### IOutboxProcessor

`IOutboxProcessor` controls the background processor lifecycle:

```csharp
public interface IOutboxProcessor
{
    Task StartAsync(CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
}
```

- `StartAsync` begins polling for eligible messages
- `StopAsync` cancels polling and waits for the processing loop to stop
- If auto-start is enabled, the hosted service manages this automatically

## Configuration

### Registering the Outbox

```csharp
services.AddMongo("mongodb://localhost:27017", "myDatabase")
    .WithOutbox(o => o
        .WithPublisher<NotificationsPublisher>()
        .WithMessage<OrderPlacedMessage>("OrderPlaced")
        .WithMessage<OrderCancelledMessage>("OrderCancelled")
        .WithCollectionName("Outbox")
        .WithMaxRetries(5)
        .WithRetryBackoff(TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(5))
        .WithBatchSize(100)
        .WithPollingInterval(TimeSpan.FromSeconds(5))
        .WithLockTimeout(TimeSpan.FromMinutes(5))
        .WithRetentionPeriod(TimeSpan.FromDays(7))
        .WithAutoStartProcessor());
```

`WithOutbox` registers:

- `IOutbox` as a singleton
- `IOutboxProcessor` as a singleton
- Your `IOutboxPublisher` implementation
- `OutboxOptions`
- `OutboxConfigurator` for index creation
- `OutboxHostedService` when `WithAutoStartProcessor()` is enabled

### Builder Options

| Option | Default | Description |
|--------|---------|-------------|
| `WithPublisher<TPublisher>()` | Required | Registers the publisher implementation; default lifetime is transient and an overload accepts `ServiceLifetime` |
| `WithMessage<TPayload>(string? discriminator = null)` | Required | Registers a payload type; discriminator defaults to the class name |
| `WithCollectionName(string)` | `"Outbox"` | Sets the outbox collection name |
| `WithMaxRetries(int)` | `5` | Maximum failed attempts before a message becomes `Failed` |
| `WithRetryBackoff(TimeSpan initialDelay, TimeSpan maxDelay)` | `5s`, `5m` | Configures exponential retry backoff |
| `WithBatchSize(int)` | `100` | Maximum eligible messages fetched per polling batch |
| `WithPollingInterval(TimeSpan)` | `5s` | Delay between polls when the batch is not full |
| `WithLockTimeout(TimeSpan)` | `5m` | When a locked message becomes reclaimable |
| `WithRetentionPeriod(TimeSpan)` | Disabled | Creates TTL indexes for processed and failed messages |
| `WithAutoStartProcessor()` | Disabled | Starts the processor automatically via hosted service |

### Processor Startup

With auto-start enabled:

- the outbox hosted service runs the outbox configurator during startup
- required indexes are ensured before the processor starts polling
- the processor is stopped automatically during application shutdown
- an in-flight message may remain pending if shutdown cancels publish or finalization, and will be retried later

With auto-start disabled, you can manage the processor manually:

```csharp
public class OutboxAdminService
{
    private readonly IOutboxProcessor _processor;

    public OutboxAdminService(IOutboxProcessor processor)
    {
        _processor = processor;
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
        => _processor.StartAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken = default)
        => _processor.StopAsync(cancellationToken);
}
```

If you manage the processor yourself, ensure configurators have already run so the outbox indexes exist. The usual approach is to enable `MongoOptions.RunConfiguratorsOnStartup`.

## Writing Messages

### Transaction Requirement

The outbox is intentionally transaction-only.

```csharp
using var session = await mongoHelper.Client.StartSessionAsync(cancellationToken: cancellationToken);
session.StartTransaction();

await outbox.AddMessageAsync(
    session,
    new OrderPlacedMessage { OrderId = order.Id.ToString() },
    correlationId: order.Id.ToString(),
    cancellationToken: cancellationToken);

await session.CommitTransactionAsync(cancellationToken);
```

If the session is null, not in a transaction, or the payload type is not registered, `AddMessageAsync` throws an exception.

If you use `TryStartTransactionAsync()`, you must handle the `null` case yourself. The outbox does not fall back to best-effort non-transactional inserts.

### Message Type Registration

Each payload type must be registered ahead of time:

```csharp
.WithOutbox(o => o
    .WithPublisher<NotificationsPublisher>()
    .WithMessage<OrderPlacedMessage>("OrderPlaced")
    .WithMessage<OrderShippedMessage>("OrderShipped"));
```

- The discriminator is written to `OutboxMessage.Type`
- If you omit the discriminator, the payload class name is used
- Payload types are automatically registered with MongoDB BSON serialization when the outbox is configured

### Correlation IDs

Use `correlationId` to connect business operations, logs, traces, and downstream messages:

```csharp
await outbox.AddMessageAsync(
    session,
    new OrderPlacedMessage { OrderId = order.Id.ToString() },
    correlationId: order.Id.ToString(),
    cancellationToken: cancellationToken);
```

The publisher receives the same value through `message.CorrelationId`.

## Processing Behavior

### Delivery Semantics

The outbox provides **at-least-once delivery**.

That means:

- a committed outbox message will eventually be retried until it is processed or permanently failed
- a message may be published more than once
- downstream consumers should be idempotent

A duplicate publish can happen if a processor publishes successfully but crashes or loses ownership before the message state is updated to `Processed`.

### Retries and Backoff

When `PublishAsync` throws:

- `RetryCount` is incremented
- `Error` is updated with the last exception message
- `NextAttemptUtc` is set using exponential backoff
- the message remains `Pending` until retries are exhausted

Once the retry count reaches `MaxRetries`, the message is marked as `Failed` and `FailedUtc` is set.

### Locking and Stale Lock Recovery

Before publishing, the processor claims a message by setting:

- `IsLocked = true`
- `LockedUtc = now`
- `LockId = <new token>`

Completion and failure updates match on `LockId`. This prevents an older processor from overwriting the state after another processor has reclaimed the same message.

If a processor crashes while holding a lock, another processor can reclaim the message once `LockedUtc` is older than the configured `LockTimeout`.

### Ordering

The processor queries eligible pending messages in ascending `_id` order, which gives approximate insertion ordering within a single processor.

Strict global ordering is not guaranteed because:

- `ObjectId` is not a global sequence across concurrent writers
- retries can defer older failed messages while newer messages continue
- multiple processor instances can process messages concurrently

If downstream systems require strict ordering, enforce it outside the outbox.

### Retention and Cleanup

When `WithRetentionPeriod(...)` is configured, the outbox creates TTL indexes for:

- `ProcessedUtc`
- `FailedUtc`

This allows MongoDB to delete processed and permanently failed messages automatically after the configured retention period.

If retention is not configured, messages are kept indefinitely and managed TTL indexes are removed.

## Event Store Integration

The event store exposes an `onBeforeCommit` callback that runs inside the same transaction as the event append. That makes it a good place to enqueue outbox messages:

```csharp
await _eventStore.AppendEventsAsync(
    [new OrderCreatedEvent { /* ... */ }],
    onBeforeCommit: async (session, aggregate, helper, ct) =>
    {
        await _outbox.AddMessageAsync(
            session,
            new OrderPlacedMessage
            {
                OrderId = aggregate.Id.ToString(),
                CustomerName = aggregate.CustomerName,
                TotalAmount = aggregate.TotalAmount
            },
            correlationId: aggregate.Id.ToString(),
            cancellationToken: ct);
    });
```

This keeps event persistence and outbox persistence atomic without inserting outbox documents manually.

## Best Practices

- Keep publishers focused on transport concerns; perform business validation before writing to the outbox.
- Register explicit message discriminators so renaming a .NET class does not change the wire contract accidentally.
- Use correlation IDs consistently to simplify tracing across services.
- Assume duplicate delivery and make consumers idempotent.
- Enable retention if the outbox is operational data only; disable it if you need long-term auditing.
- Keep `RunConfiguratorsOnStartup` or `WithAutoStartProcessor()` enabled in production so indexes are not forgotten.
