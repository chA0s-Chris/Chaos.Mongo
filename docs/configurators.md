# Database Configurators

Configurators run database initialization logic such as creating collections or ensuring indexes. They can run automatically during application startup or be invoked through `IMongoConfiguratorRunner`.

## Creating a configurator

Implement `IMongoConfigurator` for each cohesive configuration task:

```csharp
using Chaos.Mongo;
using Chaos.Mongo.Configuration;
using MongoDB.Driver;

public sealed class UserCollectionConfigurator : IMongoConfigurator
{
    public async Task ConfigureAsync(
        IMongoHelper helper,
        CancellationToken cancellationToken = default)
    {
        var collection = helper.GetCollection<User>();
        var indexes = new[]
        {
            new CreateIndexModel<User>(
                Builders<User>.IndexKeys.Ascending(user => user.Email),
                new CreateIndexOptions { Unique = true, Name = "email_unique" }),
            new CreateIndexModel<User>(
                Builders<User>.IndexKeys.Ascending(user => user.CreatedAt),
                new CreateIndexOptions { Name = "created_at" })
        };

        foreach (var index in indexes)
        {
            await collection.Indexes.CreateOneOrUpdateAsync(
                index,
                cancellationToken: cancellationToken);
        }
    }
}
```

`CreateOneOrUpdateAsync` safely recreates an index when its specification has changed. See [Index Management](index-management.md) for details.

## Registration

Register configurators individually:

```csharp
services.AddMongo("mongodb://localhost:27017", "myDatabase")
    .WithConfigurator<UserCollectionConfigurator>()
    .WithConfigurator<OrderCollectionConfigurator>();
```

Or discover configurators from the calling assembly:

```csharp
services.AddMongo("mongodb://localhost:27017", "myDatabase")
    .WithConfiguratorAutoDiscovery();
```

## Startup execution

Enable configurators during hosted application startup:

```csharp
services.AddMongo(options =>
{
    options.Url = new MongoUrl("mongodb://localhost:27017/myDatabase");
    options.RunConfiguratorsOnStartup = true;
});
```

Keep configurators idempotent so repeated startup execution is safe.

## Related documentation

- [Configuration](configuration.md)
- [Index Management](index-management.md)
- [Database Migrations](migrations.md)
