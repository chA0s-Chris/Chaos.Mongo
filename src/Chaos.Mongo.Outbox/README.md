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

## Package relationships

This package references [`Chaos.Mongo`](https://www.nuget.org/packages/Chaos.Mongo), which provides MongoDB registration and transaction helpers. [`Chaos.Mongo.EventStore`](https://www.nuget.org/packages/Chaos.Mongo.EventStore) is optional and can add outbox messages through event-store transactional callbacks.

## Documentation

- [Complete Transactional Outbox documentation](https://github.com/chA0s-Chris/Chaos.Mongo/blob/main/docs/transactional-outbox.md)
- [Transactions](https://github.com/chA0s-Chris/Chaos.Mongo/blob/main/docs/transactions.md)
- [Getting Started with Chaos.Mongo](https://github.com/chA0s-Chris/Chaos.Mongo/blob/main/docs/getting-started.md)
- [Project overview](https://github.com/chA0s-Chris/Chaos.Mongo)
