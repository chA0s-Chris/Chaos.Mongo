# Chaos.Mongo

[![GitHub License](https://img.shields.io/github/license/chA0s-Chris/Chaos.Mongo?style=for-the-badge)](https://github.com/chA0s-Chris/Chaos.Mongo/blob/main/LICENSE)
[![NuGet Version](https://img.shields.io/nuget/v/Chaos.Mongo?style=for-the-badge)](https://www.nuget.org/packages/Chaos.Mongo)
[![NuGet Downloads](https://img.shields.io/nuget/dt/Chaos.Mongo?style=for-the-badge)](https://www.nuget.org/packages/Chaos.Mongo)
[![GitHub last commit](https://img.shields.io/github/last-commit/chA0s-Chris/Chaos.Mongo?style=for-the-badge)](https://github.com/chA0s-Chris/Chaos.Mongo/commits/)
[![GitHub Actions Workflow Status](https://img.shields.io/github/actions/workflow/status/chA0s-Chris/Chaos.Mongo/ci.yml?style=for-the-badge)](https://github.com/chA0s-Chris/Chaos.Mongo/actions/workflows/ci.yml)

Core MongoDB infrastructure for .NET applications, including dependency-injection setup, collection mapping, migrations, database configurators, distributed locks, typed queues, index management, and transaction helpers.

## Installation

```bash
dotnet add package Chaos.Mongo
```

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

Resolve `IMongoHelper` to access a mapped collection:

```csharp
using MongoDB.Driver;

public sealed class UserRepository(IMongoHelper mongo)
{
    public async Task AddAsync(User user, CancellationToken cancellationToken = default)
    {
        var users = mongo.GetCollection<User>();
        await users.InsertOneAsync(user, cancellationToken: cancellationToken);
    }
}
```

## Related packages

- [`Chaos.Mongo.EventStore`](https://www.nuget.org/packages/Chaos.Mongo.EventStore) adds aggregate-based event sourcing.
- [`Chaos.Mongo.Outbox`](https://www.nuget.org/packages/Chaos.Mongo.Outbox) adds transactional message persistence and background delivery.

Both extension packages reference `Chaos.Mongo`; they are installed separately and are not part of this core package.

## Documentation

- [Getting Started](https://github.com/chA0s-Chris/Chaos.Mongo/blob/main/docs/getting-started.md)
- [Configuration](https://github.com/chA0s-Chris/Chaos.Mongo/blob/main/docs/configuration.md)
- [Database Migrations](https://github.com/chA0s-Chris/Chaos.Mongo/blob/main/docs/migrations.md)
- [Database Configurators](https://github.com/chA0s-Chris/Chaos.Mongo/blob/main/docs/configurators.md)
- [Distributed Locking](https://github.com/chA0s-Chris/Chaos.Mongo/blob/main/docs/distributed-locking.md)
- [Message Queues](https://github.com/chA0s-Chris/Chaos.Mongo/blob/main/docs/queues.md)
- [Transactions](https://github.com/chA0s-Chris/Chaos.Mongo/blob/main/docs/transactions.md)
- [Index Management](https://github.com/chA0s-Chris/Chaos.Mongo/blob/main/docs/index-management.md)
- [Project overview](https://github.com/chA0s-Chris/Chaos.Mongo)
