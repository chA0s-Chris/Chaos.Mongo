# Chaos.Mongo.Outbox

[![GitHub License](https://img.shields.io/github/license/chA0s-Chris/Chaos.Mongo?style=for-the-badge)](https://github.com/chA0s-Chris/Chaos.Mongo/blob/main/LICENSE)
[![NuGet Version](https://img.shields.io/nuget/v/Chaos.Mongo.Outbox?style=for-the-badge)](https://www.nuget.org/packages/Chaos.Mongo.Outbox)
[![NuGet Downloads](https://img.shields.io/nuget/dt/Chaos.Mongo.Outbox?style=for-the-badge)](https://www.nuget.org/packages/Chaos.Mongo.Outbox)
[![GitHub last commit](https://img.shields.io/github/last-commit/chA0s-Chris/Chaos.Mongo?style=for-the-badge)](https://github.com/chA0s-Chris/Chaos.Mongo/commits/)
[![GitHub Actions Workflow Status](https://img.shields.io/github/actions/workflow/status/chA0s-Chris/Chaos.Mongo/ci.yml?style=for-the-badge)](https://github.com/chA0s-Chris/Chaos.Mongo/actions/workflows/ci.yml)

A transactional outbox for MongoDB with typed payloads, at-least-once background delivery, retries, stale-lock recovery, and optional retention cleanup.

## Installation

```bash
dotnet add package Chaos.Mongo.Outbox
```

## Quick start

Define a payload and publisher:

```csharp
using Chaos.Mongo;
using Chaos.Mongo.Outbox;

public sealed class OrderPlaced
{
    public string OrderId { get; set; } = string.Empty;
}

public sealed class NotificationsPublisher : IOutboxPublisher
{
    public Task PublishAsync(
        OutboxMessage message,
        CancellationToken cancellationToken = default)
    {
        var payload = message.DeserializePayload<OrderPlaced>();
        return PublishToBrokerAsync(payload, cancellationToken);
    }

    private static Task PublishToBrokerAsync(
        OrderPlaced payload,
        CancellationToken cancellationToken) => Task.CompletedTask;
}
```

Register the core MongoDB services and outbox processor:

```csharp
services.AddMongo("mongodb://localhost:27017", "myDatabase")
    .WithOutbox(outbox => outbox
        .WithPublisher<NotificationsPublisher>()
        .WithMessage<OrderPlaced>("OrderPlaced")
        .WithAutoStartProcessor());
```

Write the business change and message in the same transaction:

```csharp
await mongo.ExecuteInTransaction(async (helper, session, cancellationToken) =>
{
    await orders.InsertOneAsync(session, order, cancellationToken: cancellationToken);
    await outbox.AddMessageAsync(
        session,
        new OrderPlaced { OrderId = order.Id.ToString() },
        correlationId: order.Id.ToString(),
        cancellationToken: cancellationToken);
});
```

MongoDB transaction support is required for atomic business and outbox writes. The processor must be started automatically or through `IOutboxProcessor` for messages to be delivered.

## Multiple destinations

Register marker-type outboxes with distinct collections, then inject their typed
writer and processor interfaces:

```csharp
services.AddMongo("mongodb://localhost:27017", "myDatabase")
    .WithOutbox<NotificationsOutbox>(o => o
        .WithCollectionName("Notifications")
        .WithMessage<OrderPlaced>("OrderNotification")
        .WithPublisher<NotificationsPublisher>(ServiceLifetime.Scoped)
        .WithAutoStartProcessor())
    .WithOutbox<AuditOutbox>(o => o
        .WithCollectionName("Audit")
        .WithMessage<OrderPlaced>("OrderAudit")
        .WithPublisher<AuditPublisher>(ServiceLifetime.Singleton));

public sealed class NotificationsOutbox { }
public sealed class AuditOutbox { }
```

Inject `IOutbox<NotificationsOutbox>` or `IOutbox<AuditOutbox>` to choose where to
write. Each destination has its own message registry, publisher, indexes, retry and
retention policies, filter, and processing loop. Marker types are never instantiated.
The same payload type may use different message discriminators in different outboxes.

The original `WithOutbox(...)`, `IOutbox`, and `IOutboxProcessor` remain available for
a default outbox alongside typed registrations. Duplicate markers and shared
collection names are rejected across the service collection, including conflicts
with the default outbox. Collection comparison is ordinal and case-sensitive;
all builders still default to `"Outbox"`, so configure distinct names explicitly.

Publishers implement `IOutboxPublisher` and resolve once per nonempty batch in a
fresh scope. Transient is the default lifetime; scoped publishers live for the batch,
and singleton publishers live for their destination's service provider lifetime.
The same implementation type registered for two outboxes still has independent
instances and lifetimes. Singleton publishers must not depend on scoped services.

Automatic startup initializes and starts every enabled processor. Successful index
initialization is shared with general MongoDB startup and cached for the
configurator's lifetime; failed or canceled attempts can be retried. For manual
outboxes, call `IOutboxConfiguratorRunner.RunAsync()` to initialize **all** outboxes
(or enable `MongoOptions.RunConfiguratorsOnStartup`), then control the destination
with `IOutboxProcessor<AuditOutbox>.StartAsync()` and `StopAsync()`. Manual processors
remain the caller's responsibility. Host shutdown signals all automatic processors
before waiting, bounded by the shutdown token. Logs carry the marker type name (or
`Default`) and collection.

Pass the same compatible, active MongoDB transaction session to both writers:

```csharp
await notifications.AddMessageAsync(session, orderPlaced, cancellationToken: token);
await audit.AddMessageAsync(session, orderPlaced, cancellationToken: token);
await session.CommitTransactionAsync(token);
```

The caller starts and owns this transaction; aborting rolls back both writes.
Destinations share the configured `IMongoHelper` database and publish independently
after commit, with at-least-once delivery and no cross-outbox ordering guarantee.


## Package relationships

This package references [`Chaos.Mongo`](https://www.nuget.org/packages/Chaos.Mongo), which provides MongoDB registration and transaction helpers. [`Chaos.Mongo.EventStore`](https://www.nuget.org/packages/Chaos.Mongo.EventStore) is optional and can add outbox messages through event-store transactional callbacks.

## Documentation

- [Complete Transactional Outbox documentation](https://github.com/chA0s-Chris/Chaos.Mongo/blob/main/docs/transactional-outbox.md)
- [Transactions](https://github.com/chA0s-Chris/Chaos.Mongo/blob/main/docs/transactions.md)
- [Getting Started with Chaos.Mongo](https://github.com/chA0s-Chris/Chaos.Mongo/blob/main/docs/getting-started.md)
- [Project overview](https://github.com/chA0s-Chris/Chaos.Mongo)
