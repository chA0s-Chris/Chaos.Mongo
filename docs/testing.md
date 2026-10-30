# Testing

Guidance for testing application code that depends on Chaos.Mongo.

## Unit Tests: Mocking the Public Interfaces

Chaos.Mongo's core services are plain interfaces — `IMongoHelper`, `IMongoMigrationRunner`,
`IMongoConfiguratorRunner`, `IMongoQueue<TPayload>`, `IEventStore<TAggregate>` (`Chaos.Mongo.EventStore`), `IOutbox`
(`Chaos.Mongo.Outbox`), and similar — so unit tests that only exercise in-memory logic can depend on a hand-crafted
test double instead of a real database. Implement only the members your code under test uses; throw
`NotImplementedException` from the rest so an unexpected call fails loudly instead of returning a misleading default:

```csharp
private sealed class FakeMongoHelper : IMongoHelper
{
    public IMongoClient Client => throw new NotImplementedException();
    public IMongoDatabase Database => throw new NotImplementedException();

    public IMongoCollection<TDocument> GetCollection<TDocument>(MongoCollectionSettings? settings = null)
        => throw new NotImplementedException();

    public Task<IMongoLock?> TryAcquireLockAsync(String lockName, TimeSpan? leaseTime = null, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();
}
```

A double can also record calls or inject failures instead of just standing in for an unused dependency — for
example a `RecordingMongoHelper` that captures the collection names it was asked for, or a `ThrowingMongoHelper` that
fails a specific operation to exercise your error handling. Prefer this kind of hand-crafted double over a mocking
framework: it is easier to read at the call site and does not need a library dependency.

## Integration Tests: a Real Database

An integration test that needs the real behavior — serialization, indexes, transactions, concurrency — builds its
own `ServiceCollection`, calls `AddMongo` with a real test database's URL, and resolves the actual implementations
from the built provider instead of faking them. Resolving through the container, rather than constructing the
implementation types directly, ensures collection names and any other builder-configured values match what
production would use for the same configuration:

```csharp
var services = new ServiceCollection();
var databaseName = $"MyAppTests_{Guid.NewGuid():N}";

services.AddMongo(testDatabaseUrl, databaseName)
        .WithQueue<EmailMessage>(queue => queue
            .WithPayloadHandler<EmailMessageHandler>()
            .WithCollectionName("email_queue"));

var serviceProvider = services.BuildServiceProvider();
var mongo = serviceProvider.GetRequiredService<IMongoHelper>();
var queue = serviceProvider.GetRequiredService<IMongoQueue<EmailMessage>>();
```

`testDatabaseUrl` is a `MongoUrl` for a database your test suite owns, for example one managed by
[Testcontainers](https://dotnet.testcontainers.org/) for the duration of the test run. Give each test (or test
class) its own database name, as above, so tests that run concurrently do not interfere with each other's
collections, indexes or locks.

## Overriding the Clock

Code that depends on `TimeProvider` — including the library's own distributed-locking lease expiry — can be tested
deterministically by registering a test `TimeProvider`, such as
[`Microsoft.Extensions.TimeProvider.Testing`](https://www.nuget.org/packages/Microsoft.Extensions.TimeProvider.Testing)'s
`FakeTimeProvider`, **before** the `AddMongo` call. `AddMongo` registers the system clock with `TryAddSingleton`
(`services.TryAddSingleton(TimeProvider.System)`), so an earlier registration of `TimeProvider` is left untouched and
the whole container, including the library's own services, observes your fake clock:

```csharp
var timeProvider = new FakeTimeProvider();

var services = new ServiceCollection();
services.AddSingleton<TimeProvider>(timeProvider);
services.AddMongo(testDatabaseUrl, databaseName);

var mongo = services.BuildServiceProvider().GetRequiredService<IMongoHelper>();

var @lock = await mongo.TryAcquireLockAsync("my-lock", leaseTime: TimeSpan.FromMinutes(1));
timeProvider.Advance(TimeSpan.FromMinutes(2));
// The lease has now expired according to the code under test, without an actual two-minute wait.
```

## Testing a Single Migration

Test an [`IMongoMigration`](migrations.md#creating-a-migration) implementation directly by instantiating it and
calling `ApplyAsync` yourself, without going through `IMongoMigrationRunner` (which tracks migration history and
orders migrations — concerns a single-migration test does not need):

```csharp
var migration = new AddUserIndexes();
await migration.ApplyAsync(mongoHelper, session: null, cancellationToken: default);

var users = mongoHelper.GetCollection<User>();
var indexes = await users.Indexes.ListAsync();
// Assert the index the migration creates is present.
```

Pass a real `IMongoHelper` from an [integration test](#integration-tests-a-real-database) when the migration's
effect, such as an index, can only be observed on a real database; pass a hand-crafted
[fake](#unit-tests-mocking-the-public-interfaces) when the migration's logic can be verified without one.

## Related documentation

- [Getting Started](getting-started.md)
- [Database migrations](migrations.md)
- [Distributed locking](distributed-locking.md)
- [Message queues](queues.md)
