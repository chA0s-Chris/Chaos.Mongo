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
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        var result = await migrationRunner.RunMigrationsAsync(cancellationToken);
        if (result.Skipped)
        {
            // The migration lock could not be acquired within MigrationLockWaitTimeout.
            // Decide whether to retry, wait, or fail — this method does not do that for you.
        }
    }
}
```

## Behavior

- Migrations run in ordinal order by `Id`. The `MongoMigrationRunner` constructor throws `ArgumentException` at
  startup if two or more registered migrations share the same `Id`.
- Applied migrations are recorded in the configured migration history collection, `_migrations` by default.
- During hosted startup, migrations run before configurators, so a configurator cannot create an index before a
  migration that cleans up the data it depends on. See [Configurators](configurators.md#startup-execution).
- A distributed lock ensures only one application instance runs migrations at a time. By default,
  `RunMigrationsAsync` waits for the lock instead of skipping immediately, bounded by
  `MigrationLockWaitTimeout` (30 minutes by default). Set it to `TimeSpan.Zero` to make a single attempt and
  report a skipped run immediately when the lock is held, or to `null` to wait indefinitely.
- `RunMigrationsAsync` returns a `MongoMigrationResult` describing whether the run was skipped
  (`MongoMigrationResult.Skipped`) and which migration IDs were applied (`MongoMigrationResult.AppliedMigrationIds`).
  A skipped run never throws; callers that invoke `RunMigrationsAsync` directly must check `Skipped` themselves and
  decide how to react. `MongoHostedService`, however, fails hosted startup when migrations are enabled and the run
  is skipped, unless `MigrationLockWaitTimeout` is `TimeSpan.Zero` — because letting startup continue would run
  configurators and the rest of the host against a schema another instance may still be migrating.
- While a migration runs, the lock's lease is renewed in the background once half of its remaining lifetime has
  elapsed, so a migration (or a batch of migrations) that takes longer than `MigrationLockLeaseTime` keeps the lock
  instead of losing it. If the lease cannot be renewed — because another holder has taken over, or renewal otherwise fails —
  the in-progress migration is signaled for cancellation, is not recorded as applied, and its transaction (if any)
  is aborted. A migration that observes its cancellation token stops promptly; one that does not cannot be forcibly
  interrupted, and any non-transactional effects it already performed cannot be rolled back.
- Migrations use transactions when available unless `UseTransactionsForMigrationsIfAvailable` is disabled.

## Recommendations

- Use sortable timestamp-based IDs such as `YYYYMMDDXX_Description`.
- Make migrations idempotent and safe to retry.
- Keep each migration focused on one logical change.
- Pass the supplied session to database operations when present.
- Test migrations against representative copies of production data.

See [Configuration](configuration.md) for migration collection, lock, lease, and transaction options.
