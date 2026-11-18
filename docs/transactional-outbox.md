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
  - [Publisher Factories](#publisher-factories)
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
  - [Publishing Within the Lock Lease](#publishing-within-the-lock-lease)
  - [Ordering](#ordering)
  - [Retention and Cleanup](#retention-and-cleanup)
- [Metrics](#metrics)
  - [Suggested Alerts](#suggested-alerts)
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
- **Durable retries**: the attempt count and next-attempt scheduling are stored in MongoDB
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
using Chaos.Mongo;
using Chaos.Mongo.Outbox;

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
        await _mongo.ExecuteInTransactionAsync(async (helper, session, cancellationToken) =>
        {
            var orders = helper.GetCollection<Order>();
            await orders.InsertOneAsync(session, order, cancellationToken: cancellationToken);

            await _outbox.AddMessageAsync(
                session,
                new OrderPlacedMessage
                {
                    OrderId = order.Id.ToString(),
                    CustomerName = order.CustomerName,
                    TotalAmount = order.TotalAmount
                },
                correlationId: order.Id.ToString(),
                cancellationToken: cancellationToken);
        });
    }
}
```

If the transaction commits, both the order and the outbox message are persisted. If the transaction aborts, neither is persisted.

## Core Concepts

### IOutbox

`IOutbox` is the write-side API:

```csharp
using MongoDB.Driver;

public interface IOutbox
{
    Task AddMessageAsync<TPayload>(
        IClientSessionHandle session,
        TPayload payload,
        string? correlationId = null,
        CancellationToken cancellationToken = default)
        where TPayload : class;
}
```

- `AddMessageAsync` inserts a message into the outbox collection
- The provided session must already have an active transaction
- `TPayload` must be registered via `WithMessage<TPayload>()`
- `TPayload` is a class or record that the MongoDB driver serializes as a document; a parameterless constructor is
  not required, so positional records work
- `correlationId` is optional and stored with the message for tracing

### IOutboxPublisher

`IOutboxPublisher` is the delivery abstraction you implement for your broker, queue, API, or webhook target:

```csharp
using Chaos.Mongo.Outbox;

public interface IOutboxPublisher
{
    Task PublishAsync(OutboxMessage message, CancellationToken cancellationToken = default);
}
```

- Throw an exception to signal a failed publish attempt
- The processor will persist the failure and schedule the next retry; with the default unlimited `MaxRetries` this
  continues indefinitely, or until `AttemptCount` exceeds a finite `MaxRetries` if one is configured
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
| `AttemptCount` | Number of times this message has been claimed for processing, not the number of failures. Every lock claim increments this, including a lease-expiry reclaim after a crash, so a message that succeeds on its very first claim has `AttemptCount == 1` |
| `NextAttemptUtc` | When the message becomes eligible for retry |
| `Error` | Last failure message |
| `IsLocked` | Indicates whether a processor currently owns the message |
| `LockedUtc` | When the current lock was acquired |
| `LockId` | Ownership token used to prevent stale processors from overwriting newer results |

The outbox creates the messages it stores. To build one in a unit test of your publisher, use
`OutboxMessage.Create(...)`; see [Testing](testing.md#testing-an-outbox-publisher).

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
using Chaos.Mongo;
using Chaos.Mongo.Outbox;

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
        .WithLockLeaseTime(TimeSpan.FromMinutes(5))
        .WithRetentionPeriod(TimeSpan.FromDays(7))
        .WithAutoStartProcessor());
```

`WithOutbox` registers:

- `IOutbox` as a singleton
- `IOutboxProcessor` as a singleton
- Your `IOutboxPublisher` implementation or factory
- `OutboxConfigurator` for index creation
- `IOutboxConfiguratorRunner`
- `OutboxHostedService` when `WithAutoStartProcessor()` is enabled

### Publisher Factories

Use the factory overload of `WithPublisher` when the publisher needs services or
values from the container that a type registration cannot supply:

```csharp
using Chaos.Mongo;
using Chaos.Mongo.Outbox;
using Microsoft.Extensions.Options;

services.AddMongo("mongodb://localhost:27017", "myDatabase")
    .WithOutbox(o => o
        .WithMessage<OrderPlacedMessage>("OrderPlaced")
        .WithPublisher(
            sp => new BrokerPublisher(
                sp.GetRequiredService<IBrokerClient>(),
                sp.GetRequiredService<IOptions<BrokerOptions>>().Value.Topic),
            ServiceLifetime.Scoped));
```

The factory runs lazily when the processor resolves the publisher, not when
`WithOutbox` is called, so the configure delegate and its registration checks still
run eagerly. The lifetime defaults to transient and decides which provider the
factory receives:

- **Transient** and **scoped** factories run with the processor's per-batch scope,
  so they can resolve scoped services. Both run once per nonempty batch.
- A **singleton** factory runs once with the root provider and must not resolve
  scoped services.

The factory works the same way for `WithOutbox<TOutbox>`. A type and a factory replace
each other: the last `WithPublisher` call wins. A factory that returns `null` causes
publisher resolution to fail with an `InvalidOperationException` that names the outbox.

### Multiple Typed Outboxes

Use marker types to select independent destinations. Markers supply identity only;
they are never instantiated and need no public constructor.

```csharp
using Chaos.Mongo;
using Chaos.Mongo.Outbox;

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

public sealed class NotificationsOutbox { }
public sealed class AuditOutbox { }
```

`WithOutbox<TOutbox>` registers:

- Keyed `OutboxConfigurator`, `MongoOutbox` and `OutboxProcessor` for index creation and
  processing, keyed by an internal registration token for this destination — not by
  `typeof(TOutbox)`
- `IOutbox<TOutbox>` as an unkeyed singleton that wraps the keyed `MongoOutbox` above
- `IOutboxProcessor<TOutbox>` as an unkeyed singleton that wraps the keyed `OutboxProcessor`
  above; the closed generic type is what selects the destination when you resolve it
- Your `IOutboxPublisher` implementation or factory
- The shared `IOutboxConfiguratorRunner`, which also initializes every other registered outbox
- `OutboxHostedService` when `WithAutoStartProcessor()` is enabled, shared with every other auto-started outbox

Resolve `IOutbox<NotificationsOutbox>` and `IOutbox<AuditOutbox>` through constructor
injection or the service provider. Each destination owns its collection, message
registry, publisher, processor, batch size, polling interval, lock lease time,
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
using Chaos.Mongo.Outbox;

await services.GetRequiredService<IOutboxConfiguratorRunner>().RunConfiguratorsAsync(cancellationToken);
var processor = services.GetRequiredService<IOutboxProcessor<AuditOutbox>>();
await processor.StartAsync(cancellationToken);
// Later, when this destination should stop:
await processor.StopAsync(cancellationToken);
```

Here `services` is an `IServiceProvider`. The runner initializes **all** registered
outboxes, including ones without automatic startup. Enabling
`MongoOptions.RunConfiguratorsOnStartup` also initializes all outboxes. Without
either option, initialize manually before starting a processor. Automatic startup
alone initializes only enabled destinations. Starting or stopping one processor
does not affect another; manual processors are the caller's lifecycle responsibility.
Host and manual shutdown behave the same way for every destination; see the shutdown behavior described under
[Processor Startup](#processor-startup). Processing and lifecycle diagnostics carry `OutboxIdentity` (marker type
name or `Default`) and `CollectionName`.

Use the same compatible MongoDB session to enqueue atomically to multiple destinations:

```csharp
using Chaos.Mongo.Outbox;

var notifications = services.GetRequiredService<IOutbox<NotificationsOutbox>>();
var audit = services.GetRequiredService<IOutbox<AuditOutbox>>();
using var session = await mongo.Client.StartSessionAsync(cancellationToken: cancellationToken);
session.StartTransaction();
await notifications.AddMessageAsync(session, payload, cancellationToken: cancellationToken);
await audit.AddMessageAsync(session, payload, cancellationToken: cancellationToken);
await session.CommitTransactionAsync(cancellationToken);
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
| `WithPublisher<TPublisher>()` | Required¹ | Registers the publisher implementation; default lifetime is transient and an overload accepts `ServiceLifetime` |
| `WithPublisher(Func<IServiceProvider, IOutboxPublisher>, ServiceLifetime)` | Required¹ | Registers a publisher factory; lifetime defaults to transient (see [Publisher Factories](#publisher-factories)) |
| `WithMessage<TPayload>(string? discriminator = null)` | Required | Registers a payload type; discriminator defaults to the class name |
| `WithCollectionName(string)` | `"Outbox"` | Sets the outbox collection name |
| `WithMaxRetries(int)` | Unlimited | Retries allowed after the first delivery attempt (`N` allows `N + 1` total attempts) before a message becomes `Failed`; every lock claim counts as an attempt, not only a failed publish. Omit to allow unlimited attempts, bounded only by the backoff cap |
| `WithRetryBackoff(TimeSpan initialDelay, TimeSpan maxDelay)` | `5s`, `5m` | Configures exponential retry backoff |
| `WithBatchSize(int)` | `100` | Maximum eligible messages fetched per polling batch |
| `WithPollingInterval(TimeSpan)` | `5s` | Delay between polls when the batch is not full |
| `WithProcessingFilter(FilterDefinition<OutboxMessage>)` | None | Adds a MongoDB constraint to message selection and atomic claiming |
| `WithLockLeaseTime(TimeSpan)` | `5m` | When a locked message becomes reclaimable |
| `WithRetentionPeriod(TimeSpan)` | Disabled | Creates a TTL index for processed messages, and for failed messages too if `WithExpireFailedMessages()` is also configured |
| `WithExpireFailedMessages()` | Disabled | Includes `Failed` messages in the TTL cleanup configured by `WithRetentionPeriod(...)`; without it, `Failed` messages are retained indefinitely |
| `WithAutoStartProcessor()` | Disabled | Starts the processor automatically via hosted service |

¹ One of the two `WithPublisher` forms is required. Calling either one replaces any earlier publisher configuration.

### Processing Filter

Workers sharing an outbox can restrict which messages they process:

```csharp
using Chaos.Mongo;
using Chaos.Mongo.Outbox;
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

The optional filter configured through `WithProcessingFilter` is ANDed with the existing
pending-state, retry-schedule, and lock predicates. MongoDB applies it before the batch is
sorted and limited, then rechecks it when atomically claiming each selected message.
Excluded messages consume no batch slots or retries and retain their state, retry schedule,
and lock fields. They can be delivered later by a processor whose filter includes them.

Configure the filter at startup through `WithProcessingFilter`. Runtime policy refresh is
not supported, and the filter and its captured values must not be mutated after
configuration. Repeated builder calls replace the previous filter; passing null to the
builder throws. Omitting the call preserves normal eligibility.

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

**Shutdown behavior**, identical for the default outbox and every typed outbox, and whether triggered by host
shutdown or a manual `StopAsync` call:

- Every automatic processor is canceled concurrently; a slow or stuck one does not delay another destination's
  cancellation, and the host's own shutdown token bounds how long it waits for all of them together.
- A publish already in flight when canceled is not treated as a failure: the processor releases the message's lock
  instead of recording an error, so the message becomes immediately eligible for redelivery by any processor.
- A publish that already succeeded is not discarded. Marking it `Processed` is not bound by the caller's
  cancellation token while the processor is still running; once a stop is requested, that write gets a short grace
  period (3 seconds) to finish. If it does not finish in time, the lock is released for redelivery instead, the same
  as for an in-flight publish, rather than leaving the message stuck locked.
- Failure bookkeeping (`Error`, the retry backoff) is written without a cancellation token at all, so a publish that
  fails during shutdown still records its retry or final failure instead of being silently dropped. `AttemptCount`
  itself was already incremented atomically at claim time, before the publish ever ran.

With auto-start disabled, you can manage the processor manually:

```csharp
using Chaos.Mongo.Outbox;

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
using Chaos.Mongo;
using Chaos.Mongo.Outbox;

services.AddMongo("mongodb://localhost:27017", "myDatabase")
    .WithOutbox(o => o
        .WithPublisher<NotificationsPublisher>()
        .WithMessage<OrderPlacedMessage>("OrderPlaced")
        .WithMessage<OrderShippedMessage>("OrderShipped"));
```

- The discriminator is written to `OutboxMessage.Type`
- If you omit the discriminator, the payload class name is used
- Two payload types of one outbox must not share a discriminator: `WithOutbox` throws an `InvalidOperationException`
  naming the discriminator and both types. The same discriminator may be used in different outboxes
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

`MaxRetries` is the number of retries allowed after the first delivery attempt: a configured value of `N` allows
`N + 1` total delivery attempts before a message becomes permanently failed. Every lock claim counts as a
delivery attempt, not only a failed `PublishAsync` call — the same meaning as a queue's `MaxRetries`. By default
`MaxRetries` is unlimited: a message is retried indefinitely, with the exponential backoff below capped at
`RetryBackoffMaxDelay`, and it is never marked `Failed` purely for exhausting an attempt budget. Call
`WithMaxRetries(N)` to restore a finite budget.

The claim itself atomically decides, from the message's own `AttemptCount` before the update, whether to grant
this claim a new attempt or to terminalize the message directly:

- **Granted**: `AttemptCount` increments and the lock fields are set, exactly like any other claim. With a finite
  `MaxRetries`, this happens whenever the existing `AttemptCount` is still `<= MaxRetries`, including for a message
  reclaimed after a crash — this is what makes a crash between claim and `PublishAsync` still count toward the
  budget instead of letting a poison message cycle forever. With the unlimited default, every claim is granted.
- **Terminal, without granting a lock**: only possible with a finite `MaxRetries`; when the existing `AttemptCount`
  already exceeds it, the message transitions straight to `Failed` without ever calling `PublishAsync`. This is
  only reached for a message whose last granted attempt was never resolved by `PublishAsync` throwing or
  succeeding — for example a crash.

When `PublishAsync` throws for a granted attempt:

- `Error` is updated with the last exception message
- if the already-known `AttemptCount` still allows a retry, `NextAttemptUtc` is set using exponential backoff and
  the message remains `Pending`
- otherwise the message is marked `Failed` immediately and `FailedUtc` is set

### Locking and Stale Lock Recovery

Claiming a message sets:

- `AttemptCount` incremented (the grant branch above)
- `IsLocked = true`
- `LockedUtc = now`
- `LockId = <new token>`

Completion and failure updates match on `LockId`. This prevents an older processor from overwriting the state after another processor has reclaimed the same message.

If a processor crashes while holding a lock, another processor can reclaim the message once `LockedUtc` is older
than the configured `LockLeaseTime`. That reclaim is itself a claim and counts toward `MaxRetries` the same way,
whether it is granted another attempt or terminalizes the message directly.

### Publishing Within the Lock Lease

Each message is claimed right before its `IOutboxPublisher.PublishAsync` call, and the claim's lease is not renewed
while the publisher runs. **`PublishAsync` must complete well within `WithLockLeaseTime(...)`.** A publish that runs
past the lease lets another processor reclaim the message and publish it again while the first publish is still in
progress. The late first publish then cannot mark the message `Processed`, because its `LockId` no longer matches,
and the second attempt counts toward `MaxRetries`.

The `CancellationToken` passed to `PublishAsync` signals processor shutdown, not lease expiry. Bound the publish
yourself, for example with the broker client's own timeout, and choose a lease well above that bound. Downstream
consumers must tolerate duplicates in any case; see [Delivery Semantics](#delivery-semantics).

### Ordering

The processor queries eligible pending messages ordered by `NextAttemptUtc`, then `LockedUtc`, then ascending `_id`.

In practice, this means messages whose scheduled retry time is due earlier are considered first, reclaimed stale locks are ordered by their lock time, and `_id` only provides approximate insertion ordering among messages with the same scheduling and lock state.

Strict global ordering is not guaranteed because:

- `ObjectId` is not a global sequence across concurrent writers
- retries can defer older failed messages while newer messages continue
- multiple processor instances can process messages concurrently

If downstream systems require strict ordering, enforce it outside the outbox.

### Retention and Cleanup

When `WithRetentionPeriod(...)` is configured, the outbox creates a TTL index on `ProcessedUtc`, allowing MongoDB
to delete processed messages automatically after the configured retention period.

`Failed` messages are **not** expired by default: a short broker outage should not cost you the only record of a
permanently failed message. Call `WithExpireFailedMessages()` alongside `WithRetentionPeriod(...)` to also create a
TTL index on `FailedUtc`, restoring the previous behavior of expiring failed messages after the same retention
period.

If retention is not configured, messages are kept indefinitely and managed TTL indexes are removed. If retention
is configured without `WithExpireFailedMessages()`, any existing `FailedUtc` TTL index is removed and `Failed`
messages are kept indefinitely while `Processed` messages continue to expire normally.

## Metrics

The outbox processor emits runtime metrics through the `System.Diagnostics.Metrics` meter named by
`OutboxMetrics.MeterName` (`Chaos.Mongo.Outbox`). Instrument and tag names are available through
`OutboxMetrics.Instruments` and `OutboxMetrics.Tags`. Add the meter name to your OpenTelemetry (or other
`MeterListener`) configuration to collect them. The instruments are:

| Instrument | Type | Measures |
|---|---|---|
| `chaos.mongo.outbox.published` | Counter | Messages that were published and reached `Processed`. |
| `chaos.mongo.outbox.publish.failed` | Counter | Failed publish attempts. |
| `chaos.mongo.outbox.publish.terminal_failed` | Counter | Messages that permanently failed and reached `Failed`. |
| `chaos.mongo.outbox.publish.duration` | Histogram (s) | Duration of the `PublishAsync` call of messages that reached `Processed`. |
| `chaos.mongo.outbox.message.age` | Histogram (s) | Time from the creation of a message (`CreatedUtc`) to the moment it reached `Processed`, including retries and backoff. |

Every instrument carries one tag, `outbox.destination`, whose value is the outbox's collection name
(`WithCollectionName`, default `Outbox`). The collection name is unique per outbox, so a series always belongs to
exactly one outbox, including when several typed outboxes are registered.

How the instruments count:

- `published`, `publish.duration` and `message.age` are recorded together, once per message, when the state update to
  `Processed` succeeds. Their counts are therefore equal. A publish whose finalization failed, timed out or lost the
  lock is not counted; the redelivery that completes the message records it later. `publish.duration` covers the
  `PublishAsync` call of that delivery only, not earlier attempts. A message creation time in the future (clock skew
  of the writer) is recorded as an age of zero.
- `publish.failed` counts every attempt that is resolved as a failure: a `PublishAsync` call that throws, and a
  publish whose finalization failed with an error. It does not count a processor shutdown that releases the lock, a
  finalization that timed out, or a finalization that lost the lock to another processor.
- `publish.terminal_failed` counts each message once, when it transitions to `Failed`. The terminal attempt counts
  both `publish.failed` and `publish.terminal_failed`; a message terminalized by a claim without a further
  `PublishAsync` call counts only `publish.terminal_failed`. Terminal failures need a finite `MaxRetries`; with the
  unlimited default this instrument stays at zero.

### Suggested Alerts

Translate these conditions into your monitoring backend, per `outbox.destination`:

- **Publish failures**: alert when the rate of `publish.failed` stays above zero for several minutes. This usually
  means that the publisher or the broker is unavailable, or that a message is rejected repeatedly. Failed attempts
  are retried, so they are not lost messages: a failed attempt can precede a later success of the same message,
  and `publish.failed` also counts a finalization failure after a successful publish.
- **Terminal failures**: alert on any increase of `publish.terminal_failed`. Those messages are parked in `Failed`,
  are not expired by default (see [Retention and Cleanup](#retention-and-cleanup)) and need operator action.
  Only this instrument counts messages that were given up on.
- **Delivery lag and backlog**: alert when a high percentile (for example p95) of `message.age` exceeds the delay
  your consumers tolerate, or when the rate of `published` drops to zero while your application still writes
  messages. A growing backlog shows up as growing message age once those messages are delivered.

`message.age` and `published` only observe messages that were delivered. A processor that is stopped, or a backlog that
is stalled completely, emits nothing and appears as an absent or flat series rather than as a high age. Pair the lag
alert with an alert on the absence of `published` data while messages are being written, or with an
application-side count of pending messages. The outbox does not provide a backlog gauge.

## Event Store Integration

The event store exposes an `onBeforeCommit` callback that runs inside the same transaction as the event append. That makes it a good place to enqueue outbox messages:

```csharp
await _eventStore.AppendEventsAsync(
    [new OrderCreatedEvent { /* ... */ }],
    onBeforeCommit: async (session, aggregate, helper, cancellationToken) =>
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
            cancellationToken: cancellationToken);
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
- Alert on `publish.terminal_failed` and on delivery lag; see [Suggested Alerts](#suggested-alerts).
