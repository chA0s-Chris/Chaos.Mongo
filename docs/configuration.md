# Configuration

`MongoOptions` controls the core `Chaos.Mongo` connection and startup behavior. Configure it directly, bind it from configuration, or combine either approach with the overloads described in [Getting Started](getting-started.md).

## MongoOptions

| Property                                  | Type                           | Default                  | Description                                                    |
|-------------------------------------------|--------------------------------|--------------------------|----------------------------------------------------------------|
| `Url`                                     | `MongoUrl?`                    | `null`                   | MongoDB connection URL; required                               |
| `DefaultDatabase`                         | `string?`                      | `null`                   | Default database name                                          |
| `CollectionTypeMap`                       | `Dictionary<Type, string>`     | Empty                    | Maps CLR types to collection names                             |
| `UseDefaultCollectionNames`               | `bool`                         | `true`                   | Uses the type name when no explicit collection mapping exists  |
| `ApplyMigrationsOnStartup`                | `bool`                         | `false`                  | Runs registered migrations during application startup          |
| `RunConfiguratorsOnStartup`               | `bool`                         | `false`                  | Runs registered configurators during application startup       |
| `UseTransactionsForMigrationsIfAvailable` | `bool`                         | `true`                   | Uses transactions for migrations when the server supports them |
| `LockCollectionName`                      | `string`                       | `"_locks"`               | Collection used for distributed locks                          |
| `MigrationHistoryCollectionName`          | `string`                       | `"_migrations"`          | Collection used for migration history                          |
| `MigrationsLockName`                      | `string`                       | `"ChaosMongoMigrations"` | Distributed lock used to coordinate migrations                 |
| `MigrationLockLeaseTime`                  | `TimeSpan`                     | 10 minutes               | Lease duration for the migration lock                          |
| `HolderId`                                | `string?`                      | Generated identifier     | Identifier for this application instance                       |
| `ConfigureClientSettings`                 | `Action<MongoClientSettings>?` | `null`                   | Customizes the MongoDB driver client settings                  |

## Binding from configuration

Given this `appsettings.json` section:

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

Register it by section name:

```csharp
services.AddMongo(configuration, "MongoDB");
```

## Advanced client configuration

Use `ConfigureClientSettings` for MongoDB driver settings that are not represented directly by `MongoOptions`:

```csharp
services.AddMongo(options =>
{
    options.Url = new MongoUrl("mongodb://localhost:27017/myDatabase");
    options.ConfigureClientSettings = settings =>
    {
        settings.MaxConnectionPoolSize = 200;
        settings.MinConnectionPoolSize = 10;
        settings.ConnectTimeout = TimeSpan.FromSeconds(30);
        settings.ServerSelectionTimeout = TimeSpan.FromSeconds(30);
        settings.RetryWrites = true;
        settings.RetryReads = true;
    };
});
```

The callback is applied to the `MongoClientSettings` created from `Url` before the client is constructed.

## Related documentation

- [Getting Started](getting-started.md)
- [Database migrations](migrations.md)
- [Database configurators](configurators.md)
- [Distributed locking](distributed-locking.md)
