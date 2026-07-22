# Getting Started with Chaos.Mongo

`Chaos.Mongo` registers a MongoDB client, database, and helper APIs with Microsoft dependency injection. It also provides the foundation used by the Event Store and Transactional Outbox packages.

## Installation

```bash
dotnet add package Chaos.Mongo
```

## Basic setup

```csharp
using Chaos.Mongo;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddMongo("mongodb://localhost:27017", "myDatabase");

var app = builder.Build();
app.Run();
```

`AddMongo` returns a `MongoBuilder`, which is used to register migrations, configurators, queues, event stores, and the outbox.

## Connection setup

### Connection string

```csharp
services.AddMongo(
    connectionString: "mongodb://localhost:27017",
    databaseName: "myDatabase",
    configure: options =>
    {
        options.UseDefaultCollectionNames = true;
        options.ApplyMigrationsOnStartup = true;
    });
```

### Configuration

```json
{
  "MongoDB": {
    "Url": "mongodb://localhost:27017",
    "DefaultDatabase": "myDatabase",
    "ApplyMigrationsOnStartup": true,
    "RunConfiguratorsOnStartup": true
  }
}
```

```csharp
services.AddMongo(configuration, "MongoDB");
```

See [Configuration](configuration.md) for all available options.

### MongoUrl

```csharp
using MongoDB.Driver;

var mongoUrl = new MongoUrl("mongodb://localhost:27017/myDatabase");
services.AddMongo(mongoUrl);
```

## Collection type mapping

Map CLR types to collection names during registration:

```csharp
services.AddMongo(options =>
{
    options.Url = new MongoUrl("mongodb://localhost:27017/myDatabase");
    options.AddMapping<User>("users");
    options.AddMapping<Order>("orders");
    options.AddMapping<Product>("products");

    // Types without an explicit mapping use their type name.
    options.UseDefaultCollectionNames = true;
});
```

Resolve `IMongoHelper` to access a mapped collection:

```csharp
public sealed class UserRepository(IMongoHelper mongo)
{
    public async Task SaveAsync(User user, CancellationToken cancellationToken = default)
    {
        var collection = mongo.GetCollection<User>();
        await collection.InsertOneAsync(user, cancellationToken: cancellationToken);
    }
}
```

When `UseDefaultCollectionNames` is disabled, every requested type must have an explicit mapping.

## MongoDB helper

`IMongoHelper` exposes the configured client and database in addition to mapped collections:

```csharp
using MongoDB.Bson;

public sealed class DataService(IMongoHelper mongo)
{
    public async Task<BsonDocument> PingAsync(CancellationToken cancellationToken = default)
    {
        var command = new BsonDocument("ping", 1);
        return await mongo.Database.RunCommandAsync<BsonDocument>(
            command,
            cancellationToken: cancellationToken);
    }
}
```

Use `mongo.Client` when direct access to the underlying `IMongoClient` is required.

## Next steps

- [Configuration](configuration.md)
- [Database migrations](migrations.md)
- [Database configurators](configurators.md)
- [Distributed locking](distributed-locking.md)
- [Message queues](queues.md)
- [Transactions](transactions.md)
- [Index management](index-management.md)
