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
| `MigrationLockLeaseTime`                  | `TimeSpan`                     | 10 minutes               | Lease duration for the migration lock; minimum one millisecond |
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

## Configuring options with registered services

When an option depends on a service registered in the dependency-injection container, use an `AddMongo` overload whose delegate also receives the `IServiceProvider`. For example, route the MongoDB driver's logging through the application's `ILoggerFactory`:

```csharp
using MongoDB.Driver.Core.Configuration;

services.AddMongo("mongodb://localhost:27017", "myDatabase", (serviceProvider, options) =>
{
    var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
    options.ConfigureClientSettings = settings => settings.LoggingSettings = new LoggingSettings(loggerFactory);
});
```

Provider-aware overloads exist for the parameterless, connection-string, and `MongoUrl` entry points. The connection-string and `MongoUrl` overloads set `Url` and `DefaultDatabase` before invoking the delegate; pass `null` as `databaseName` to use the database from the URL.

`MongoBuilder.ConfigureOptions` adds the same kind of delegate after any entry point, including configuration binding:

```csharp
services.AddMongo(configuration, "MongoDB")
        .ConfigureOptions((serviceProvider, options) =>
        {
            var environment = serviceProvider.GetRequiredService<IHostEnvironment>();
            options.DefaultDatabase = $"myapp-{environment.EnvironmentName.ToLowerInvariant()}";
        });
```

Configuration delegates run in registration order, so `ConfigureOptions` runs after the configuration applied by the `AddMongo` call and can override bound values. Options validation runs after all of them.

The delegate runs when the options are first resolved (at startup under a generic host, because the options are validated on start), not when `AddMongo` is called. For `IOptions<MongoOptions>`, as used by the library, it receives the root service provider: resolve singleton or transient services only, because resolving a scoped service throws when scope validation is enabled.

## Related documentation

- [Getting Started](getting-started.md)
- [Database migrations](migrations.md)
- [Database configurators](configurators.md)
- [Distributed locking](distributed-locking.md)
