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
| `MigrationLockName`                       | `string`                       | `"ChaosMongoMigrations"` | Distributed lock used to coordinate migrations                 |
| `MigrationLockLeaseTime`                  | `TimeSpan`                     | 10 minutes               | Lease duration for the migration lock; minimum one millisecond |
| `MigrationLockWaitTimeout`                | `TimeSpan?`                    | 30 minutes               | How long to wait for the migration lock before reporting a skipped run; `null` waits indefinitely, `TimeSpan.Zero` makes a single attempt |
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
using Chaos.Mongo;

services.AddMongo(configuration, "MongoDB");
```

## Advanced client configuration

Use `ConfigureClientSettings` for MongoDB driver settings that are not represented directly by `MongoOptions`:

```csharp
using Chaos.Mongo;
using MongoDB.Driver;

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

## Replacing Connection Construction

`AddMongo` builds the connection in two replaceable steps, each behind an interface registered with
`TryAddSingleton`: `IMongoClientSettingsFactory.CreateMongoClientSettings(MongoUrl)` turns the configured URL into
`MongoClientSettings`, and `IMongoConnectionFactory.CreateConnection(MongoUrl, string?)` uses those settings to build
the `IMongoConnection` — the `MongoClient` and `IMongoDatabase` pair — that the rest of the library resolves.

An ordinary settings change, even one that depends on registered services, does not need a custom
`IMongoClientSettingsFactory`: the provider-aware `AddMongo` overloads already let the configure delegate resolve
services from the `IServiceProvider` and set `MongoOptions.ConfigureClientSettings` from them, exactly as
[Configuring options with registered services](#configuring-options-with-registered-services) shows. Replace
`IMongoClientSettingsFactory` only to change how settings are *constructed* from the URL itself — its
`CreateMongoClientSettings(MongoUrl)` responsibility — not merely because the settings depend on a service.

Replace `IMongoConnectionFactory` to change how `IMongoConnection` itself is constructed, for example to reuse an
already-registered `MongoClient` or to validate connectivity eagerly before returning it.

Because both are registered with `TryAddSingleton`, a registration of either interface replaces the library's
default, whether it is registered before `AddMongo` (so the library's own registration never takes effect, since
`TryAddSingleton` only adds a service when none is registered yet) or after it with `AddSingleton` (so the later
registration simply wins). Register before `AddMongo` when the replacement does not depend on anything `AddMongo`
itself registers; register after when it does.

```csharp
using Chaos.Mongo;
using MongoDB.Driver;

public sealed class SharedClientConnectionFactory(MongoClient client) : IMongoConnectionFactory
{
    public IMongoConnection CreateConnection(MongoUrl url, string? databaseName = null)
    {
        var name = databaseName ?? url.DatabaseName
                   ?? throw new ArgumentException("URL doesn't contain a database name and databaseName is not set");

        return new MongoConnection(client, client.GetDatabase(name));
    }
}
```

```csharp
using Chaos.Mongo;
using MongoDB.Driver;

services.AddSingleton(new MongoClient("mongodb://localhost:27017"));
services.AddSingleton<IMongoConnectionFactory, SharedClientConnectionFactory>();
services.AddMongo("mongodb://localhost:27017", "myDatabase");
```

Registering before `AddMongo` means the library's own `MongoConnectionFactory` never runs: the custom factory ignores
the settings `IMongoClientSettingsFactory` would have built and instead wraps the already-registered `MongoClient` in
a `MongoConnection`.

## Configuring options with registered services

When an option depends on a service registered in the dependency-injection container, use an `AddMongo` overload whose delegate also receives the `IServiceProvider`. For example, route the MongoDB driver's logging through the application's `ILoggerFactory`:

```csharp
using Chaos.Mongo;
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
using Chaos.Mongo;

services.AddMongo(configuration, "MongoDB")
        .ConfigureOptions((serviceProvider, options) =>
        {
            var environment = serviceProvider.GetRequiredService<IHostEnvironment>();
            options.DefaultDatabase = $"myapp-{environment.EnvironmentName.ToLowerInvariant()}";
        });
```

Configuration delegates run in registration order, so `ConfigureOptions` runs after the configuration applied by the `AddMongo` call and can override bound values. Options validation runs after all of them.

The delegate runs when the options are first resolved (at startup under a generic host, because the options are validated on start), not when `AddMongo` is called. For `IOptions<MongoOptions>`, as used by the library, it receives the root service provider: resolve singleton or transient services only, because resolving a scoped service throws when scope validation is enabled.

## Collection names

Every feature stores its data in collections whose default names derive from a CLR type name or a fixed default.
Default names are part of the [compatibility policy](compatibility-policy.md#stored-data), but a type-derived default
follows your code: renaming a mapped class, an aggregate type or a queue payload type silently moves its data to a new,
empty collection. In production, pin every collection name explicitly, so that refactoring never changes where data
lives:

| Feature | Default name | Pin it with |
|---|---|---|
| Collections of `IMongoHelper.GetCollection<T>()` | The type name, while `UseDefaultCollectionNames` is `true` | `MongoOptions.AddMapping<T>(name)` or `CollectionTypeMap` |
| [Event store](event-store.md#collections-created) | Prefix = aggregate type name: `{prefix}`, `{prefix}_Events`, `{prefix}_Checkpoints`, `{prefix}_IntegrityState` | `MongoEventStoreBuilder<TAggregate>.WithCollectionPrefix(prefix)` |
| [Tamper-evidence anchors](event-store.md#anchoring) | Prefix `Integrity`: `IntegrityAnchors`, `IntegrityAnchorLeaves` and so on | `WithCollectionPrefix(prefix)` on both `IntegrityAnchoringBuilder` and `IntegrityVerificationBuilder`, with the same value |
| [Queues](queues.md) | `_Queue.` followed by a name derived from the payload type | `MongoQueueBuilder<TPayload>.WithCollectionName(name)` |
| [Outboxes](transactional-outbox.md) | `Outbox` | `OutboxBuilder.WithCollectionName(name)`, distinct for every outbox |
| Locks and migration history | `_locks` and `_migrations` | `MongoOptions.LockCollectionName` and `MigrationHistoryCollectionName` |

Setting `UseDefaultCollectionNames` to `false` makes `IMongoHelper.GetCollection<T>()` throw `KeyNotFoundException`
for an unmapped type instead of falling back to its name. It does not affect the other features, which keep their own
defaults until you pin them:

```csharp
using Chaos.Mongo;
using Chaos.Mongo.EventStore;
using Chaos.Mongo.Outbox;
using MongoDB.Driver;

services.AddMongo(options =>
    {
        options.Url = new MongoUrl("mongodb://localhost:27017/shop");
        options.UseDefaultCollectionNames = false; // every GetCollection<T>() type needs a mapping
        options.AddMapping<Customer>("customers");
    })
    .WithEventStore<OrderAggregate>(es => es
        .WithEvent<OrderPlacedEvent>("OrderPlaced")
        .WithCollectionPrefix("Orders"))
    .WithQueue<InvoiceRequested>(queue => queue
        .WithPayloadHandler<InvoiceRequestedHandler>()
        .WithCollectionName("InvoiceRequests"))
    .WithOutbox(outbox => outbox
        .WithPublisher<BrokerPublisher>()
        .WithMessage<OrderPlacedMessage>("OrderPlaced")
        .WithCollectionName("Outbox"));
```

Before renaming a queue payload type, read `IMongoQueue<TPayload>.QueueDefinition.CollectionName` from the
running deployment. Pass that exact name to `WithCollectionName(...)` before starting the renamed worker so it
continues processing pending items. If the rename has already shipped, recover the original collection name from
the previous deployment or the database. A worker using the renamed payload type and default naming reports the
new name.

When renaming a mapped class, map it to its existing collection. When renaming an aggregate, keep its existing
event-store prefix with `WithCollectionPrefix(...)`. Anchor verification also uses the aggregate's CLR type name
to find historical evidence. After a rename, `VerifyEventAnchorAsync` can return `NotAnchored` for a previously
anchored event, and `VerifyStreamAsync` can report `AggregateTypeMismatch` even with the original prefix. That name
is always the current CLR type name and cannot be configured, so the anchor coverage of a renamed aggregate splits
between the old and the new name; see
[Threat Model, Guarantees and Limitations](event-store.md#threat-model-guarantees-and-limitations).

## Related documentation

- [Getting Started](getting-started.md)
- [Database migrations](migrations.md)
- [Database configurators](configurators.md)
- [Distributed locking](distributed-locking.md)
