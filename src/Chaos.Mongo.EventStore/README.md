# Chaos.Mongo.EventStore

[![GitHub License](https://img.shields.io/github/license/chA0s-Chris/Chaos.Mongo?style=for-the-badge)](https://github.com/chA0s-Chris/Chaos.Mongo/blob/main/LICENSE)
[![NuGet Version](https://img.shields.io/nuget/v/Chaos.Mongo.EventStore?style=for-the-badge)](https://www.nuget.org/packages/Chaos.Mongo.EventStore)
[![NuGet Downloads](https://img.shields.io/nuget/dt/Chaos.Mongo.EventStore?style=for-the-badge)](https://www.nuget.org/packages/Chaos.Mongo.EventStore)
[![GitHub last commit](https://img.shields.io/github/last-commit/chA0s-Chris/Chaos.Mongo?style=for-the-badge)](https://github.com/chA0s-Chris/Chaos.Mongo/commits/)
[![GitHub Actions Workflow Status](https://img.shields.io/github/actions/workflow/status/chA0s-Chris/Chaos.Mongo/ci.yml?style=for-the-badge)](https://github.com/chA0s-Chris/Chaos.Mongo/actions/workflows/ci.yml)

Event sourcing for MongoDB with aggregate reconstruction, append-only event streams, optimistic concurrency, idempotency, checkpoints, and transactional callbacks.

## Installation

```bash
dotnet add package Chaos.Mongo.EventStore
```

## Quick start

Define an aggregate and event:

```csharp
using Chaos.Mongo.EventStore;

public sealed class OrderAggregate : Aggregate
{
    public string CustomerName { get; set; } = string.Empty;
}

public sealed class OrderCreated : Event<OrderAggregate>
{
    public string CustomerName { get; set; } = string.Empty;

    public override void Execute(OrderAggregate aggregate)
        => aggregate.CustomerName = CustomerName;
}
```

Register the core MongoDB services and the event store:

```csharp
services.AddMongo("mongodb://localhost:27017", "myDatabase")
    .WithEventStore<OrderAggregate>(eventStore => eventStore
        .WithEvent<OrderCreated>("OrderCreated")
        .WithCollectionPrefix("Orders"));
```

Append an event through `IEventStore<TAggregate>`:

```csharp
var orderId = Guid.NewGuid();
var version = await eventStore.GetExpectedNextVersionAsync(orderId);

var order = await eventStore.AppendEventsAsync(
[
    new OrderCreated
    {
        AggregateId = orderId,
        Version = version,
        CustomerName = "Ada"
    }
]);
```

## Package relationships

This package references [`Chaos.Mongo`](https://www.nuget.org/packages/Chaos.Mongo), which supplies the connection, dependency-injection, transaction, and configuration infrastructure. [`Chaos.Mongo.Outbox`](https://www.nuget.org/packages/Chaos.Mongo.Outbox) is optional and can be combined with event-store transactional callbacks when event changes must publish external messages reliably.

## Documentation

- [Complete Event Store documentation](https://github.com/chA0s-Chris/Chaos.Mongo/blob/main/docs/event-store.md)
- [Getting Started with Chaos.Mongo](https://github.com/chA0s-Chris/Chaos.Mongo/blob/main/docs/getting-started.md)
- [Transactions](https://github.com/chA0s-Chris/Chaos.Mongo/blob/main/docs/transactions.md)
- [Project overview](https://github.com/chA0s-Chris/Chaos.Mongo)
