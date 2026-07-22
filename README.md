# Chaos.Mongo

[![GitHub License](https://img.shields.io/github/license/chA0s-Chris/Chaos.Mongo?style=for-the-badge)](https://github.com/chA0s-Chris/Chaos.Mongo/blob/main/LICENSE)
[![NuGet Version](https://img.shields.io/nuget/v/Chaos.Mongo?style=for-the-badge)](https://www.nuget.org/packages/Chaos.Mongo)
[![NuGet Downloads](https://img.shields.io/nuget/dt/Chaos.Mongo?style=for-the-badge)](https://www.nuget.org/packages/Chaos.Mongo)
[![GitHub last commit](https://img.shields.io/github/last-commit/chA0s-Chris/Chaos.Mongo?style=for-the-badge)](https://github.com/chA0s-Chris/Chaos.Mongo/commits/)
[![GitHub Actions Workflow Status](https://img.shields.io/github/actions/workflow/status/chA0s-Chris/Chaos.Mongo/ci.yml?style=for-the-badge)](https://github.com/chA0s-Chris/Chaos.Mongo/actions/workflows/ci.yml)

Chaos.Mongo provides MongoDB building blocks for .NET applications: dependency-injection setup, migrations, distributed locking, queues, transactions, event sourcing, and the transactional outbox pattern.

## Packages

| Package | Purpose | Documentation |
| --- | --- | --- |
| [`Chaos.Mongo`](https://www.nuget.org/packages/Chaos.Mongo) | Core connection, configuration, migrations, locking, queues, and transaction helpers | [Package README](src/Chaos.Mongo/README.md) |
| [`Chaos.Mongo.EventStore`](https://www.nuget.org/packages/Chaos.Mongo.EventStore) | Aggregate-based event sourcing, event persistence, repositories, and checkpoints | [Package README](src/Chaos.Mongo.EventStore/README.md) |
| [`Chaos.Mongo.Outbox`](https://www.nuget.org/packages/Chaos.Mongo.Outbox) | Transactional outbox with at-least-once background delivery | [Package README](src/Chaos.Mongo.Outbox/README.md) |

`Chaos.Mongo.EventStore` and `Chaos.Mongo.Outbox` are separate packages built on `Chaos.Mongo`; their features are not included in the core package by default.

## Installation

Install the core package:

```bash
dotnet add package Chaos.Mongo
```

Add either extension package when needed:

```bash
dotnet add package Chaos.Mongo.EventStore
dotnet add package Chaos.Mongo.Outbox
```

Targets .NET 8, .NET 9, and .NET 10.

## Quick start

```csharp
using Chaos.Mongo;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddMongo("mongodb://localhost:27017", "myDatabase");

var app = builder.Build();
app.Run();
```

Resolve `IMongoHelper` to access the configured database and mapped collections. Continue with [Getting Started](docs/getting-started.md) for connection and collection setup.

## Documentation

| Topic | Description |
| --- | --- |
| [Getting Started](docs/getting-started.md) | Connection registration, collection mapping, and `IMongoHelper` |
| [Configuration](docs/configuration.md) | `MongoOptions`, configuration binding, and driver settings |
| [Database Migrations](docs/migrations.md) | Ordered, tracked database changes |
| [Database Configurators](docs/configurators.md) | Idempotent startup configuration |
| [Distributed Locking](docs/distributed-locking.md) | Leased locks for cross-instance coordination |
| [Message Queues](docs/queues.md) | Typed queues, retries, recovery, retention, and diagnostics |
| [Transactions](docs/transactions.md) | Transaction helpers and optional transaction support |
| [Index Management](docs/index-management.md) | Safe index creation and replacement |
| [Event Store](docs/event-store.md) | Event sourcing, aggregates, checkpoints, and concurrency |
| [Transactional Outbox](docs/transactional-outbox.md) | Atomic message persistence and background delivery |

## License

MIT License - see [LICENSE](./LICENSE) for more information.
