# Index Management

Chaos.Mongo extends the MongoDB driver's index manager with `CreateOneOrUpdateAsync`. It creates a missing index and recreates an existing named index when its specification differs from the desired definition.

```csharp
var collection = mongo.GetCollection<User>();
var index = new CreateIndexModel<User>(
    Builders<User>.IndexKeys.Ascending(user => user.Email),
    new CreateIndexOptions
    {
        Name = "email_unique",
        Unique = true
    });

await collection.Indexes.CreateOneOrUpdateAsync(
    index,
    cancellationToken: cancellationToken);
```

Explicit names make it possible to compare and replace an existing definition predictably. Index creation is commonly placed in an idempotent [database configurator](configurators.md) or [migration](migrations.md), depending on whether the change is startup configuration or a versioned database transition.
