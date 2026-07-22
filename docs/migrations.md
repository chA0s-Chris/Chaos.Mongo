# Database Migrations

Migrations provide ordered, version-controlled changes to a MongoDB database. Chaos.Mongo records applied migrations and coordinates execution across application instances with a distributed lock.

## Creating a migration

Implement `IMongoMigration` with a stable, sortable ID:

```csharp
using Chaos.Mongo;
using Chaos.Mongo.Migrations;
using MongoDB.Driver;

public sealed class AddUserIndexes : IMongoMigration
{
    public string Id => "20250126001_AddUserIndexes";
    public string? Description => "Add indexes to users collection";

    public async Task ApplyAsync(
        IMongoHelper mongoHelper,
        IClientSessionHandle? session = null,
        CancellationToken cancellationToken = default)
    {
        var collection = mongoHelper.GetCollection<User>();
        var index = new CreateIndexModel<User>(
            Builders<User>.IndexKeys.Ascending(user => user.Email),
            new CreateIndexOptions { Unique = true });

        await collection.Indexes.CreateOneOrUpdateAsync(
            index,
            cancellationToken: cancellationToken);
    }
}
```

If a migration performs data operations and `session` is non-null, pass the session to those operations so they participate in the migration transaction.

## Registration

Register migrations individually:

```csharp
services.AddMongo("mongodb://localhost:27017", "myDatabase")
    .WithMigration<AddUserIndexes>()
    .WithMigration<AddOrderIndexes>();
```

Or discover them from the calling assembly:

```csharp
services.AddMongo("mongodb://localhost:27017", "myDatabase")
    .WithMigrationAutoDiscovery();
```

Pass explicit assemblies when migrations live elsewhere:

```csharp
services.AddMongo("mongodb://localhost:27017", "myDatabase")
    .WithMigrationAutoDiscovery([typeof(AddUserIndexes).Assembly]);
```

## Execution

Enable automatic execution during hosted application startup:

```csharp
services.AddMongo(options =>
{
    options.Url = new MongoUrl("mongodb://localhost:27017/myDatabase");
    options.ApplyMigrationsOnStartup = true;
});
```

For explicit control, resolve the runner:

```csharp
public sealed class MigrationService(IMongoMigrationRunner migrationRunner)
{
    public Task RunAsync(CancellationToken cancellationToken = default)
        => migrationRunner.RunMigrationsAsync(cancellationToken);
}
```

## Behavior

- Migrations run in ordinal order by `Id`.
- Applied migrations are recorded in the configured migration history collection, `_migrations` by default.
- A distributed lock ensures only one application instance runs migrations at a time.
- Migrations use transactions when available unless `UseTransactionsForMigrationsIfAvailable` is disabled.

## Recommendations

- Use sortable timestamp-based IDs such as `YYYYMMDDXX_Description`.
- Make migrations idempotent and safe to retry.
- Keep each migration focused on one logical change.
- Pass the supplied session to database operations when present.
- Test migrations against representative copies of production data.

See [Configuration](configuration.md) for migration collection, lock, lease, and transaction options.
