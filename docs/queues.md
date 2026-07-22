# Message Queues

Chaos.Mongo provides typed, MongoDB-backed queues for asynchronous processing. Queue items are leased while a handler runs, allowing another subscriber to recover abandoned work and providing at-least-once processing.

## Define a payload and handler

Queue payloads are reference types with a parameterless constructor:

```csharp
public sealed class EmailMessage
{
    public string To { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
}
```

Implement the typed handler:

```csharp
using Chaos.Mongo.Queues;

public sealed class EmailMessageHandler(IEmailService emailService)
    : IMongoQueuePayloadHandler<EmailMessage>
{
    public Task HandlePayloadAsync(
        EmailMessage payload,
        CancellationToken cancellationToken = default)
        => emailService.SendAsync(
            payload.To,
            payload.Subject,
            payload.Body,
            cancellationToken);
}
```

## Register a queue

```csharp
services.AddMongo("mongodb://localhost:27017", "myDatabase")
    .WithQueue<EmailMessage>(queue => queue
        .WithPayloadHandler<EmailMessageHandler>()
        .WithCollectionName("email_queue")
        .WithAutoStartSubscription()
        .WithQueryLimit(10)
        .WithLockLeaseTime(TimeSpan.FromMinutes(2))
        .WithMaxRetries(5)
        .WithClosedItemRetention(TimeSpan.FromHours(6)));
```

If no collection name is specified, `IMongoQueueCollectionNameGenerator` derives one from the payload type. A queue is registered as `IMongoQueue<TPayload>` and as the non-generic `IMongoQueue`.

### Processing options

- `WithAutoStartSubscription()` starts processing with the hosted application; manual startup is the default.
- `WithQueryLimit(...)` controls the maximum items fetched in each query.
- `WithLockLeaseTime(...)` determines when work abandoned by another subscriber becomes eligible for recovery.
- `WithMaxRetries(...)` limits retries after the initial attempt; `WithNoRetry()` makes the first failure terminal.
- `WithClosedItemRetention(...)` retains successful items for MongoDB TTL cleanup.
- `WithImmediateDelete()` removes successful items immediately.

Terminal failed items are excluded from TTL cleanup so they remain available for dead-letter handling.

## Publish messages

Resolve the typed queue and publish a payload:

```csharp
public sealed class UserService(IMongoQueue<EmailMessage> emailQueue)
{
    public Task SendWelcomeEmailAsync(
        User user,
        CancellationToken cancellationToken = default)
    {
        return emailQueue.PublishAsync(
            new EmailMessage
            {
                To = user.Email,
                Subject = "Welcome!",
                Body = $"Welcome to our service, {user.Name}!"
            },
            cancellationToken);
    }
}
```

Publishing and processing are separate operations. Make handlers idempotent because an item can be processed again after a failure or expired lease.

## Manual subscription control

```csharp
public sealed class QueueManager(IMongoQueue<EmailMessage> queue)
{
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (!queue.IsRunning)
        {
            await queue.StartSubscriptionAsync(cancellationToken);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (queue.IsRunning)
        {
            await queue.StopSubscriptionAsync(cancellationToken);
        }
    }
}
```

## Custom payload priority

Implement and register `IMongoQueuePayloadPrioritizer` to change the order in which payloads are processed:

```csharp
public sealed class PriorityEmailMessage
{
    public string To { get; set; } = string.Empty;
    public int Priority { get; set; }
}

using MongoDB.Driver;

public sealed class EmailPriorityQueuePrioritizer : IMongoQueuePayloadPrioritizer
{
    public SortDefinition<MongoQueueItem<TPayload>> CreateSortDefinition<TPayload>()
        where TPayload : class, new()
    {
        var sort = Builders<MongoQueueItem<TPayload>>.Sort;

        return typeof(TPayload) == typeof(PriorityEmailMessage)
            ? sort.Descending("Payload.Priority").Ascending(item => item.Id)
            : sort.Ascending(item => item.Id);
    }
}

services.AddSingleton<IMongoQueuePayloadPrioritizer, EmailPriorityQueuePrioritizer>();
```

The prioritizer is shared by registered queues, so provide a deterministic fallback for payload types that do not need custom ordering.

## Recovery and retention

- A handler failure increments the retry count and makes the item eligible for another attempt.
- A process crash leaves a lock that becomes recoverable after the configured lease expires.
- Successful items are retained for one hour by default and removed by a TTL index on `ClosedUtc`.
- Terminal items remain queryable for inspection and dead-letter workflows.

## Diagnostics

Queues emit structured logs for lock recovery, retry and terminal transitions, cleanup mode, and successful processing.

Runtime metrics use `MongoQueueMetrics.MeterName`. Instrument and tag names are available through `MongoQueueMetrics.Instruments` and `MongoQueueMetrics.Tags`, including published items, recovered locks, processing outcomes, recovery age, processing duration, and queue age.

## Recommendations

- Make handlers idempotent to tolerate at-least-once processing.
- Select a lease that covers normal handler execution while permitting timely recovery.
- Use retry limits to prevent poison messages from cycling indefinitely.
- Monitor queue age, failures, terminal items, and lock recovery.
- Use separate queues when workloads require distinct capacity or priority policies.
