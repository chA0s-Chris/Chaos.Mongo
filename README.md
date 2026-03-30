# Chaos.Mongo

[![GitHub License](https://img.shields.io/github/license/chA0s-Chris/Chaos.Mongo?style=for-the-badge)](https://github.com/chA0s-Chris/Chaos.Mongo/blob/main/LICENSE)
[![NuGet Version](https://img.shields.io/nuget/v/Chaos.Mongo?style=for-the-badge)](https://www.nuget.org/packages/Chaos.Mongo)
[![NuGet Downloads](https://img.shields.io/nuget/dt/Chaos.Mongo?style=for-the-badge)](https://www.nuget.org/packages/Chaos.Mongo)
[![GitHub last commit](https://img.shields.io/github/last-commit/chA0s-Chris/Mongo?style=for-the-badge)](https://github.com/chA0s-Chris/Mongo/commits/)
[![GitHub Actions Workflow Status](https://img.shields.io/github/actions/workflow/status/chA0s-Chris/Mongo/ci.yml?style=for-the-badge)]()

A comprehensive MongoDB library for .NET that simplifies working with MongoDB by providing:

- 🗄️ **Database Migrations** - Version-controlled database schema changes with automatic execution and history tracking
- 🔒 **Distributed Locking** - MongoDB-based distributed locks for coordinating work across multiple instances
- 📬 **Message Queues** - MongoDB-backed message queues with automatic retry and error handling
- 📖 **Event Store** - Event sourcing with automatic read model updates, concurrency control, and checkpoints
- ⚙️ **Database Configurators** - Automated database initialization and index management
- 💉 **Dependency Injection** - First-class support for Microsoft.Extensions.DependencyInjection
- 🔄 **Transaction Support** - Helper methods for working with MongoDB transactions

## Table of Contents

- [Installation](#installation)
- [Quick Start](#quick-start)
- [Core Features](#core-features)
  - [Connection Setup](#connection-setup)
  - [Collection Type Mapping](#collection-type-mapping)
  - [Database Migrations](#database-migrations)
  - [Database Configurators](#database-configurators)
  - [Distributed Locking](#distributed-locking)
  - [Message Queues](#message-queues)
  - [Event Store](#event-store)
  - [Transaction Support](#transaction-support)
- [Configuration](#configuration)
- [Advanced Usage](#advanced-usage)
- [Best Practices](#best-practices)
- [License](#license)

## Installation

```bash
dotnet add package Chaos.Mongo
```

## Quick Start

### Basic Setup

```csharp
using Chaos.Mongo;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

// Add MongoDB with connection string
builder.Services.AddMongo("mongodb://localhost:27017", "myDatabase");

// Or use configuration
builder.Services.AddMongo(builder.Configuration, "MongoDB");

var app = builder.Build();
app.Run();
```

### Using the MongoDB Helper

```csharp
public class UserService
{
    private readonly IMongoHelper _mongo;

    public UserService(IMongoHelper mongo)
    {
        _mongo = mongo;
    }

    public async Task<User?> GetUserAsync(string id)
    {
        var collection = _mongo.GetCollection<User>();
        return await collection.Find(u => u.Id == id).FirstOrDefaultAsync();
    }
}
```

## Core Features

### Connection Setup

#### Using Connection String

```csharp
services.AddMongo(
    connectionString: "mongodb://localhost:27017",
    databaseName: "myDatabase",
    configure: options =>
    {
        options.UseDefaultCollectionNames = true;
        options.ApplyMigrationsOnStartup = true;
    }
);
```

#### Using Configuration

**appsettings.json:**
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

**Program.cs:**
```csharp
services.AddMongo(configuration, "MongoDB");
```

#### Using MongoUrl

```csharp
var mongoUrl = new MongoUrl("mongodb://localhost:27017/myDatabase");
services.AddMongo(mongoUrl);
```

### Collection Type Mapping

Map CLR types to MongoDB collection names:

```csharp
services.AddMongo(options =>
{
    options.Url = new MongoUrl("mongodb://localhost:27017/myDatabase");
    
    // Map types to collection names
    options.AddMapping<User>("users");
    options.AddMapping<Order>("orders");
    options.AddMapping<Product>("products");
    
    // Or use default naming (type name)
    options.UseDefaultCollectionNames = true;
});
```

**Using the Collection:**
```csharp
public class UserRepository
{
    private readonly IMongoHelper _mongo;

    public UserRepository(IMongoHelper mongo)
    {
        _mongo = mongo;
    }

    public async Task SaveUserAsync(User user)
    {
        var collection = _mongo.GetCollection<User>(); // Gets "users" collection
        await collection.InsertOneAsync(user);
    }
}
```

### Database Migrations

Migrations provide version-controlled database schema changes.

#### Creating a Migration

```csharp
using Chaos.Mongo.Migrations;
using MongoDB.Driver;

public class AddUserIndexes : IMongoMigration
{
    public string Id => "20250126001_AddUserIndexes";
    public string? Description => "Add indexes to users collection";

    public async Task ApplyAsync(
        IMongoHelper mongoHelper,
        IClientSessionHandle? session = null,
        CancellationToken cancellationToken = default)
    {
        var collection = mongoHelper.GetCollection<User>();
        var indexManager = collection.Indexes;

        // Create email index
        var emailIndex = new CreateIndexModel<User>(
            Builders<User>.IndexKeys.Ascending(u => u.Email),
            new CreateIndexOptions { Unique = true }
        );

        // Use CreateOneOrUpdateAsync to handle existing indexes
        await indexManager.CreateOneOrUpdateAsync(emailIndex, cancellationToken: cancellationToken);
    }
}
```

#### Registering Migrations

```csharp
// Register individual migrations
services.AddMongo("mongodb://localhost:27017", "myDatabase")
    .WithMigration<AddUserIndexes>()
    .WithMigration<AddOrderIndexes>();

// Or use auto-discovery
services.AddMongo("mongodb://localhost:27017", "myDatabase")
    .WithMigrationAutoDiscovery(); // Scans calling assembly

// Or specify assemblies to scan
services.AddMongo("mongodb://localhost:27017", "myDatabase")
    .WithMigrationAutoDiscovery(new[] { typeof(Program).Assembly });
```

#### Migration Execution

**Automatic (Recommended):**
```csharp
services.AddMongo(options =>
{
    options.Url = new MongoUrl("mongodb://localhost:27017/myDatabase");
    options.ApplyMigrationsOnStartup = true; // Runs on app startup
});
```

**Manual:**
```csharp
public class MyService
{
    private readonly IMongoMigrationRunner _migrationRunner;

    public MyService(IMongoMigrationRunner migrationRunner)
    {
        _migrationRunner = migrationRunner;
    }

    public async Task RunMigrationsAsync()
    {
        await _migrationRunner.RunMigrationsAsync();
    }
}
```

#### Migration Features

- **Ordering**: Migrations run in order based on their `Id` (ordinal string comparison)
- **History Tracking**: Executed migrations are stored in the `_migrations` collection
- **Distributed Locking**: Only one instance runs migrations at a time
- **Transaction Support**: Migrations run in transactions when available
- **Idempotency**: Migrations should be safe to run multiple times

### Database Configurators

Configurators run initialization logic on application startup (e.g., creating collections, ensuring indexes).

#### Creating a Configurator

```csharp
using Chaos.Mongo.Configuration;

public class UserCollectionConfigurator : IMongoConfigurator
{
    public async Task ConfigureAsync(
        IMongoHelper helper,
        CancellationToken cancellationToken = default)
    {
        var collection = helper.GetCollection<User>();
        var indexManager = collection.Indexes;

        // Ensure indexes exist
        var indexes = new[]
        {
            new CreateIndexModel<User>(
                Builders<User>.IndexKeys.Ascending(u => u.Email),
                new CreateIndexOptions { Unique = true, Name = "email_unique" }
            ),
            new CreateIndexModel<User>(
                Builders<User>.IndexKeys.Ascending(u => u.CreatedAt),
                new CreateIndexOptions { Name = "created_at" }
            )
        };

        foreach (var index in indexes)
        {
            await indexManager.CreateOneOrUpdateAsync(index, cancellationToken: cancellationToken);
        }
    }
}
```

#### Registering Configurators

```csharp
// Register individual configurators
services.AddMongo("mongodb://localhost:27017", "myDatabase")
    .WithConfigurator<UserCollectionConfigurator>()
    .WithConfigurator<OrderCollectionConfigurator>();

// Or use auto-discovery
services.AddMongo("mongodb://localhost:27017", "myDatabase")
    .WithConfiguratorAutoDiscovery();
```

#### Enabling Configurators

```csharp
services.AddMongo(options =>
{
    options.Url = new MongoUrl("mongodb://localhost:27017/myDatabase");
    options.RunConfiguratorsOnStartup = true; // Runs on app startup
});
```

### Distributed Locking

Acquire distributed locks stored in MongoDB to coordinate work across multiple instances.

#### Acquiring a Lock (with Retry)

```csharp
public class JobProcessor
{
    private readonly IMongoHelper _mongo;

    public JobProcessor(IMongoHelper mongo)
    {
        _mongo = mongo;
    }

    public async Task ProcessJobAsync()
    {
        // Acquire lock with automatic retry until acquired or cancelled
        await using var lock = await _mongo.AcquireLockAsync(
            lockName: "process-daily-reports",
            leaseTime: TimeSpan.FromMinutes(10),
            retryDelay: TimeSpan.FromSeconds(5)
        );

        // Lock is held - do the work
        await ProcessReportsAsync();

        // Lock is automatically released when disposed
    }
}
```

#### Try Acquiring a Lock (No Retry)

```csharp
public async Task TryProcessJobAsync()
{
    // Try to acquire lock without retry
    await using var lockInstance = await _mongo.TryAcquireLockAsync(
        lockName: "process-daily-reports",
        leaseTime: TimeSpan.FromMinutes(10)
    );

    if (lockInstance is null)
    {
        // Lock is held by another instance
        _logger.LogInformation("Job is already running on another instance");
        return;
    }

    // Lock acquired - do the work
    await ProcessReportsAsync();
}
```

#### Lock Features

- **Automatic Release**: Locks are released when disposed
- **Lease Expiration**: Locks automatically expire if not released
- **Validation**: Check if lock is still valid with `lock.IsValid`
- **Multiple Instances**: Safe to use across multiple application instances

### Message Queues

MongoDB-backed message queues for reliable async processing.

#### Setting Up a Queue

**1. Define your payload:**
```csharp
public class EmailMessage
{
    public string To { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
}
```

**2. Create a handler:**
```csharp
using Chaos.Mongo.Queues;

public class EmailMessageHandler : IMongoQueuePayloadHandler<EmailMessage>
{
    private readonly IEmailService _emailService;
    private readonly ILogger<EmailMessageHandler> _logger;

    public EmailMessageHandler(
        IEmailService emailService,
        ILogger<EmailMessageHandler> logger)
    {
        _emailService = emailService;
        _logger = logger;
    }

    public async Task HandlePayloadAsync(
        EmailMessage payload,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Sending email to {To}", payload.To);
        await _emailService.SendAsync(payload.To, payload.Subject, payload.Body);
        _logger.LogInformation("Email sent successfully");
    }
}
```

**3. Register the queue:**
```csharp
services.AddMongo("mongodb://localhost:27017", "myDatabase")
    .WithQueue<EmailMessage>(queue => queue
        .WithPayloadHandler<EmailMessageHandler>()
        .WithCollectionName("email_queue")
        .WithAutoStartSubscription() // Start processing on app startup
        .WithQueryLimit(10) // Process up to 10 messages at a time
    );
```

#### Publishing Messages

```csharp
public class UserService
{
    private readonly IMongoQueue<EmailMessage> _emailQueue;

    public UserService(IMongoQueue<EmailMessage> emailQueue)
    {
        _emailQueue = emailQueue;
    }

    public async Task RegisterUserAsync(User user)
    {
        // Save user...

        // Queue welcome email
        await _emailQueue.PublishAsync(new EmailMessage
        {
            To = user.Email,
            Subject = "Welcome!",
            Body = $"Welcome to our service, {user.Name}!"
        });
    }
}
```

#### Manual Queue Control

```csharp
public class QueueManager
{
    private readonly IMongoQueue<EmailMessage> _queue;

    public QueueManager(IMongoQueue<EmailMessage> queue)
    {
        _queue = queue;
    }

    public async Task StartProcessingAsync()
    {
        if (!_queue.IsRunning)
        {
            await _queue.StartSubscriptionAsync();
        }
    }

    public async Task StopProcessingAsync()
    {
        if (_queue.IsRunning)
        {
            await _queue.StopSubscriptionAsync();
        }
    }
}
```

### Event Store

Event sourcing capabilities backed by MongoDB. Available in the `Chaos.Mongo.EventStore` package.

```bash
dotnet add package Chaos.Mongo.EventStore
```

#### Quick Example

```csharp
// Define your aggregate
public class OrderAggregate : Aggregate
{
    public string CustomerName { get; set; } = string.Empty;
    public string Status { get; set; } = "Pending";
}

// Define events
public class OrderCreatedEvent : Event<OrderAggregate>
{
    public string CustomerName { get; set; } = string.Empty;

    public override void Execute(OrderAggregate aggregate)
    {
        aggregate.CustomerName = CustomerName;
        aggregate.Status = "Created";
    }
}

// Register the event store
services.AddMongo("mongodb://localhost:27017", "myDatabase")
    .WithEventStore<OrderAggregate>(es => es
        .WithEvent<OrderCreatedEvent>("OrderCreated")
        .WithCollectionPrefix("Orders"));

// Use the event store
public class OrderService
{
    private readonly IEventStore<OrderAggregate> _eventStore;
    private readonly IAggregateRepository<OrderAggregate> _repository;

    public async Task<Guid> CreateOrderAsync(string customer)
    {
        var orderId = Guid.CreateVersion7();
        var version = await _eventStore.GetExpectedNextVersionAsync(orderId);

        await _eventStore.AppendEventsAsync(
        [
            new OrderCreatedEvent
            {
                Id = Guid.CreateVersion7(),
                AggregateId = orderId,
                Version = version,
                CustomerName = customer
            }
        ]);

        return orderId;
    }

    public async Task<OrderAggregate?> GetOrderAsync(Guid orderId)
    {
        return await _repository.GetAsync(orderId);
    }
}
```

#### Key Features

- **Append-only event streams** with automatic versioning
- **Read model maintenance** within the same transaction as event writes
- **Optimistic concurrency** via unique `(AggregateId, Version)` index
- **Idempotency** via unique event IDs
- **Optional checkpoints** for faster aggregate reconstruction
- **Transactional callbacks** for patterns like transactional outbox

📚 **[Full Event Store Documentation](./docs/event-store.md)**

### Transaction Support

Helper methods for working with MongoDB transactions.

#### Execute in Transaction

```csharp
public class OrderService
{
    private readonly IMongoHelper _mongo;

    public OrderService(IMongoHelper mongo)
    {
        _mongo = mongo;
    }

    public async Task<Order> CreateOrderAsync(Order order, Payment payment)
    {
        return await _mongo.ExecuteInTransaction(async (helper, session, ct) =>
        {
            // Insert order
            var orders = helper.GetCollection<Order>();
            await orders.InsertOneAsync(session, order, cancellationToken: ct);

            // Insert payment
            var payments = helper.GetCollection<Payment>();
            await payments.InsertOneAsync(session, payment, cancellationToken: ct);

            // Update inventory
            var products = helper.GetCollection<Product>();
            await products.UpdateOneAsync(
                session,
                p => p.Id == order.ProductId,
                Builders<Product>.Update.Inc(p => p.Stock, -order.Quantity),
                cancellationToken: ct
            );

            return order;
        });
    }
}
```

#### Try Starting a Transaction

```csharp
public async Task ProcessWithOptionalTransactionAsync()
{
    // Try to start transaction (returns null if not supported)
    var session = await _mongo.TryStartTransactionAsync();

    try
    {
        if (session is not null)
        {
            // Use transaction
            await DoWorkAsync(session);
            await session.CommitTransactionAsync();
        }
        else
        {
            // Transactions not supported - proceed without
            await DoWorkAsync(null);
        }
    }
    finally
    {
        session?.Dispose();
    }
}
```

## Configuration

### MongoOptions Properties

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Url` | `MongoUrl?` | `null` | MongoDB connection URL (required) |
| `DefaultDatabase` | `string?` | `null` | Default database name |
| `CollectionTypeMap` | `Dictionary<Type, string>` | `[]` | Map CLR types to collection names |
| `UseDefaultCollectionNames` | `bool` | `true` | Use type name as collection name if not mapped |
| `ApplyMigrationsOnStartup` | `bool` | `false` | Run migrations on app startup |
| `RunConfiguratorsOnStartup` | `bool` | `false` | Run configurators on app startup |
| `UseTransactionsForMigrationsIfAvailable` | `bool` | `true` | Use transactions for migrations when supported |
| `LockCollectionName` | `string` | `"_locks"` | Collection name for distributed locks |
| `MigrationHistoryCollectionName` | `string` | `"_migrations"` | Collection name for migration history |
| `MigrationsLockName` | `string` | `"ChaosMongoMigrations"` | Lock name for migration coordination |
| `MigrationLockLeaseTime` | `TimeSpan` | `10 minutes` | Lease time for migration lock |
| `HolderId` | `string?` | `Guid.NewGuid()` | Unique identifier for this instance |
| `ConfigureClientSettings` | `Action<MongoClientSettings>?` | `null` | Configure MongoDB client settings |

### Advanced Client Configuration

```csharp
services.AddMongo(options =>
{
    options.Url = new MongoUrl("mongodb://localhost:27017/myDatabase");
    
    options.ConfigureClientSettings = settings =>
    {
        // Configure connection pool
        settings.MaxConnectionPoolSize = 200;
        settings.MinConnectionPoolSize = 10;
        
        // Configure timeouts
        settings.ConnectTimeout = TimeSpan.FromSeconds(30);
        settings.ServerSelectionTimeout = TimeSpan.FromSeconds(30);
        
        // Configure retry writes
        settings.RetryWrites = true;
        settings.RetryReads = true;
    };
});
```

## Advanced Usage

### Accessing MongoDB Client and Database

```csharp
public class DataService
{
    private readonly IMongoHelper _mongo;

    public DataService(IMongoHelper mongo)
    {
        _mongo = mongo;
    }

    public async Task RunCommandAsync()
    {
        // Access the client
        var client = _mongo.Client;
        
        // Access the database
        var database = _mongo.Database;
        
        // Run a command
        var command = new BsonDocument("ping", 1);
        var result = await database.RunCommandAsync<BsonDocument>(command);
    }
}
```

### Custom Payload Prioritizer

Customize queue processing order:

```csharp
public class PriorityEmailMessage
{
    public string To { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public int Priority { get; set; } // Higher = more important
}

public class EmailPriorityComparer : IComparer<PriorityEmailMessage>
{
    public int Compare(PriorityEmailMessage? x, PriorityEmailMessage? y)
    {
        if (x is null || y is null) return 0;
        // Higher priority first
        return y.Priority.CompareTo(x.Priority);
    }
}

// Register with custom prioritizer
services.AddSingleton<IMongoQueuePayloadPrioritizer>(sp =>
    new MongoQueuePayloadPrioritizer(
        new Dictionary<Type, object>
        {
            [typeof(PriorityEmailMessage)] = new EmailPriorityComparer()
        }
    )
);
```

### Index Management

Create or update indexes safely:

```csharp
var collection = _mongo.GetCollection<User>();
var indexManager = collection.Indexes;

var index = new CreateIndexModel<User>(
    Builders<User>.IndexKeys.Ascending(u => u.Email),
    new CreateIndexOptions { Unique = true }
);

// Creates index or updates it if specifications changed
await indexManager.CreateOneOrUpdateAsync(index);
```

## Best Practices

### Migration Best Practices

- **Use timestamp-based IDs**: Format migrations as `YYYYMMDDXX_Description` (e.g., `20250126001_AddUserIndexes`)
- **Make migrations idempotent**: Migrations should be safe to run multiple times
- **Use transactions when possible**: Enable `UseTransactionsForMigrationsIfAvailable`
- **Keep migrations small**: Break large changes into smaller migrations
- **Test migrations**: Test migrations against a copy of production data

### Lock Best Practices

- **Use descriptive lock names**: Make it clear what the lock protects
- **Set appropriate lease times**: Long enough to complete work, short enough to recover from failures
- **Always use `await using`**: Ensures locks are released even if exceptions occur
- **Handle lock timeouts**: Plan for scenarios where lock acquisition fails

### Queue Best Practices

- **Make handlers idempotent**: Messages may be processed more than once
- **Handle errors gracefully**: Log errors and consider dead letter queues
- **Set appropriate query limits**: Balance throughput and resource usage
- **Monitor queue depth**: Track unprocessed messages
- **Use separate queues for different priorities**: Don't mix critical and non-critical work

## Target Frameworks

Chaos.Mongo is built for all currently supported .NET versions.

## License

MIT License - see [LICENSE](./LICENSE) for more information.
