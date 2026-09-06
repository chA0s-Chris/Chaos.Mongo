# Transactional Outbox

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
  - [Multiple Typed Outboxes](#multiple-typed-outboxes)
  - [Builder Options](#builder-options)
  - [Processing Filter](#processing-filter)
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

Persisting a message and delivering a message are separate concerns:
`IOutbox` writes the message atomically within your MongoDB transaction, while `IOutboxProcessor` is responsible for publishing pending messages.
To actually deliver messages, the processor must be running, either via `WithAutoStartProcessor()` or manual startup with `IOutboxProcessor.StartAsync()`.

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
| `Id` | MongoDB `ObjectId` used as a tie-breaker after `NextAttemptUtc` and `LockedUtc` when selecting eligible messages |
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

### Multiple Typed Outboxes

Use marker types to select independent destinations. Markers supply identity only;
they are never instantiated and need no public constructor.

```csharp
public sealed class NotificationsOutbox { }
public sealed class AuditOutbox { }

services.AddMongo("mongodb://localhost:27017", "myDatabase")
    .WithOutbox<NotificationsOutbox>(o => o
        .WithCollectionName("NotificationOutbox")
        .WithMessage<OrderPlacedMessage>("OrderNotification")
        .WithPublisher<NotificationsPublisher>(ServiceLifetime.Scoped)
        .WithMaxRetries(3)
        .WithAutoStartProcessor())
    .WithOutbox<AuditOutbox>(o => o
        .WithCollectionName("AuditOutbox")
        .WithMessage<OrderPlacedMessage>("OrderAudit")
        .WithPublisher<AuditPublisher>(ServiceLifetime.Singleton)
        .WithRetentionPeriod(TimeSpan.FromDays(30)));
```

Resolve `IOutbox<NotificationsOutbox>` and `IOutbox<AuditOutbox>` through constructor
injection or the service provider. Each destination owns its collection, message
registry, publisher, processor, batch size, polling interval, lock timeout,
processing filter, retry policy, and retention indexes. Sharing a payload type is
supported; its discriminator can differ between destinations without changing the
stored payload format. Registration validates messages against the destination's
own registry when enqueueing.

The existing `WithOutbox(...)`, `IOutbox`, and `IOutboxProcessor` remain the default
outbox and can coexist with typed registrations. Typed registrations do not populate
the untyped services. Duplicate marker registrations fail, even across different
`MongoBuilder` instances sharing one service collection. Collection names must be
distinct across all outboxes in the shared database, including the default outbox;
comparison is ordinal and case-sensitive. Every builder still defaults to `"Outbox"`,
so select explicit collection names when configuring multiple destinations. Errors
identify the conflicting outboxes and collection.

Publishers continue to implement `IOutboxPublisher`. Each destination resolves its
own configured implementation once per nonempty batch, within a fresh DI scope.
Transient is the default lifetime. Scoped publishers and their scoped dependencies
live for that batch; singleton publishers live for their destination's service
provider lifetime and must not depend on scoped services. Reusing the same publisher
implementation type across destinations creates separate publisher registrations,
including separate singleton instances. Typed publishers are resolved internally
for their destination; the untyped `IOutboxPublisher` represents the default outbox.

`WithAutoStartProcessor()` initializes and starts each enabled outbox. One shared
hosted service coordinates these processors. General MongoDB configurator startup
and outbox startup share each configurator's successful initialization, including
concurrent host startup. Failed or canceled initialization can be retried and does
not permit automatic processing to start. Successful initialization is cached for
the configurator's lifetime; restarting a processor does not recreate indexes.

For manual initialization and lifecycle control:

```csharp
await services.GetRequiredService<IOutboxConfiguratorRunner>().RunAsync(token);
var processor = services.GetRequiredService<IOutboxProcessor<AuditOutbox>>();
await processor.StartAsync(token);
// Later, when this destination should stop:
await processor.StopAsync(shutdownToken);
```

Here `services` is an `IServiceProvider`. The runner initializes **all** registered
outboxes, including ones without automatic startup. Enabling
`MongoOptions.RunConfiguratorsOnStartup` also initializes all outboxes. Without
either option, initialize manually before starting a processor. Automatic startup
alone initializes only enabled destinations. Starting or stopping one processor
does not affect another; manual processors are the caller's lifecycle responsibility.
Host shutdown signals every automatic processor before waiting for their completion,
so a delayed publisher cannot postpone another destination's cancellation. The host's
shutdown token bounds that wait. Processing and lifecycle diagnostics carry
`OutboxIdentity` (marker type name or `Default`) and `CollectionName`.

Use the same compatible MongoDB session to enqueue atomically to multiple destinations:

```csharp
var notifications = services.GetRequiredService<IOutbox<NotificationsOutbox>>();
var audit = services.GetRequiredService<IOutbox<AuditOutbox>>();
using var session = await mongo.Client.StartSessionAsync(cancellationToken: token);
session.StartTransaction();
await notifications.AddMessageAsync(session, payload, cancellationToken: token);
await audit.AddMessageAsync(session, payload, cancellationToken: token);
await session.CommitTransactionAsync(token);
```

The caller owns the transaction. Aborting it rolls back both writes, along with
other business writes in the same transaction. All outboxes use the existing
`IMongoHelper` database; there are no per-outbox connections or databases. After
commit, each processor publishes independently with at-least-once delivery and no
cross-outbox ordering guarantee. A slow or failing publisher does not occupy another
destination's processor.

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
| `WithProcessingFilter(FilterDefinition<OutboxMessage>)` | None | Adds a MongoDB constraint to message selection and atomic claiming |
| `WithLockTimeout(TimeSpan)` | `5m` | When a locked message becomes reclaimable |
| `WithRetentionPeriod(TimeSpan)` | Disabled | Creates TTL indexes for processed and failed messages |
| `WithAutoStartProcessor()` | Disabled | Starts the processor automatically via hosted service |

### Processing Filter

Workers sharing an outbox can restrict which messages they process:

```csharp
using MongoDB.Driver;

services.AddMongo("mongodb://localhost:27017", "myDatabase")
    .WithOutbox(o => o
        .WithPublisher<NotificationsPublisher>()
        .WithMessage<OrderPlacedMessage>("OrderPlaced")
        .WithMessage<OrderCancelledMessage>("OrderCancelled")
        .WithProcessingFilter(Builders<OutboxMessage>.Filter.In(
            message => message.Type, new[] { "OrderPlaced" }))
        .WithAutoStartProcessor());
```

The optional `OutboxOptions.ProcessingFilter` is ANDed with the existing pending-state,
retry-schedule, and lock predicates. MongoDB applies it before the batch is sorted and
limited, then rechecks it when atomically claiming each selected message. Excluded
messages consume no batch slots or retries and retain their state, retry schedule,
and lock fields. They can be delivered later by a processor whose filter includes them.

Configure the filter at startup through `WithProcessingFilter` or an `OutboxOptions`
initializer. Options are registered as a singleton; runtime policy refresh is not
supported, and the filter and its captured values must not be mutated after configuration.
Repeated builder calls replace the previous filter; passing null to the builder throws.
Omitting the setting (or leaving the options property null) preserves normal eligibility.

`WithMessage<TPayload>()` registers write-side discriminators and BSON serialization;
it does not restrict processor selection. `IOutboxPublisher` still handles delivery and
routing for claimed messages. Returning successfully from a publisher marks a message
processed, while throwing consumes a retry, so neither is a way to defer excluded messages.
The processing filter is not reapplied to completion, failure, or cancellation cleanup:
an owned claim can be finalized even if claiming changes a field used by the filter.

Built-in indexes stay unchanged. The discriminator filter above is not covered by the
polling index (`NextAttemptUtc`, `LockedUtc`, `_id`, with a pending-state partial filter).
Applications with a large excluded pending backlog may add their own index for their
filter and query workload. Server-side exclusion prevents batch starvation but does not
guarantee an inexpensive scan.

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

If the processor is not running, outbox messages remain persisted in `Pending` state and are not delivered.
This is expected behavior: the outbox guarantees atomic persistence on write, and a running processor is what provides eventual delivery.

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

- a committed outbox message eligible for a running processor will eventually be retried until it is processed or permanently failed
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

The processor queries eligible pending messages ordered by `NextAttemptUtc`, then `LockedUtc`, then ascending `_id`.

In practice, this means messages whose scheduled retry time is due earlier are considered first, reclaimed stale locks are ordered by their lock time, and `_id` only provides approximate insertion ordering among messages with the same scheduling and lock state.

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
