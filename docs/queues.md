# Message Queues

Chaos.Mongo provides typed, MongoDB-backed queues for asynchronous processing. Queue items are leased while a handler runs, allowing another subscriber to recover abandoned work and providing at-least-once processing.

## Define a payload and handler

Queue payloads are reference types that the MongoDB driver can serialize:

```csharp
public sealed class EmailMessage
{
    public string To { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
}
```

A parameterless constructor is not required, so a positional record such as
`public sealed record EmailMessage(string To, string Subject, string Body);` works as well.

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
using Chaos.Mongo;

services.AddMongo("mongodb://localhost:27017", "myDatabase")
    .WithQueue<EmailMessage>(queue => queue
        .WithPayloadHandler<EmailMessageHandler>()
        .WithCollectionName("email_queue")
        .WithAutoStartSubscription()
        .WithBatchSize(10)
        .WithLockLeaseTime(TimeSpan.FromMinutes(2))
        .WithMaxRetries(5)
        .WithRetentionPeriod(TimeSpan.FromHours(6)));
```

When the handler needs more than constructor injection can supply, register a factory instead. Each queue item is handled in its own DI scope: the handler is resolved from that scope and the scope is disposed once handling completes, whether it succeeds, fails, or is canceled. The handler type registration and the factory both receive the item's scoped `IServiceProvider`, so either way a handler may depend on scoped services such as a `DbContext`. A queue accepts either a handler type or a factory, not both:

```csharp
using Chaos.Mongo;

services.AddMongo("mongodb://localhost:27017", "myDatabase")
    .WithQueue<EmailMessage>(queue => queue
        .WithPayloadHandler(sp => new EmailMessageHandler(
            sp.GetRequiredService<IEmailService>()))
        .WithAutoStartSubscription());
```

If no collection name is specified, `IMongoQueueCollectionNameGenerator` derives one from the payload type. A queue is registered as `IMongoQueue<TPayload>` and as the non-generic `IMongoQueue`.

`IMongoQueue<TPayload>.QueueDefinition` exposes the resolved configuration as a `MongoQueueDefinition`:
`AutoStartSubscription`, `BatchSize`, `CollectionName`, `LockLeaseTime`, `MaxRetries`, `PayloadType` and
`RetentionPeriod`. Use it to introspect a queue's effective settings at runtime, for example in
diagnostics or health checks.

### Processing options

- `WithAutoStartSubscription()` starts processing with the hosted application; manual startup is the default.
- `WithBatchSize(...)` controls the maximum items fetched in each query.
- `WithLockLeaseTime(...)` determines when work abandoned by another subscriber becomes eligible for recovery.
- `WithMaxRetries(...)` sets the number of retries allowed after the first delivery attempt, so a configured
  value of `N` allows `N + 1` total delivery attempts before an item becomes terminal. A granted lock acquisition
  — not only a thrown handler exception — counts as a delivery attempt. `WithNoRetry()` allows exactly one
  attempt: the item becomes terminal only if that attempt fails or is abandoned, not merely by being acquired.
  This is the same meaning as the transactional outbox's `WithMaxRetries`.
- `WithRetentionPeriod(...)` retains successful items for MongoDB TTL cleanup.
- `WithImmediateDelete()` removes successful items immediately.

Terminal failed items are excluded from TTL cleanup so they remain available for dead-letter handling.

## Publish messages

Resolve the typed queue and publish a payload:

```csharp
using Chaos.Mongo.Queues;

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

## Publishing within a lease

A handler runs while its queue item is locked for `WithLockLeaseTime(...)`, and work protected by an `IMongoLock` runs
within that lock's lease. `PublishAsync` takes no session: it inserts the new item immediately and on its own, so the
publish is not atomic with completing the current item, with releasing the lock, or with any other database write.

- **A publish can repeat.** When a handler publishes and then throws, its item is retried and the handler publishes
  again. When the handler runs past the lease, which is not renewed while it runs, another instance can acquire the
  same item and publish while the first one is still running; the late first instance then cannot complete the item.
  The same applies to work under an `IMongoLock` whose lease expires or whose renewal is refused.
- **A publish is never rolled back.** An item published before the handler fails, or before a surrounding
  transaction aborts, stays in the queue.

Design follow-up payloads for this: give each one an identifier derived from the work that produced it, such as the
source item's business key, and have its handler ignore an identifier it has already processed. Publish as the last
step of the handler, after the effects that the follow-up item relies on. When the publish must be atomic with a
database write, write a [transactional outbox](transactional-outbox.md) message in the same transaction instead, and
have its publisher publish to the queue.

## Manual subscription control

```csharp
using Chaos.Mongo.Queues;

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
using Chaos.Mongo.Queues;
using MongoDB.Driver;

public sealed class PriorityEmailMessage
{
    public string To { get; set; } = string.Empty;
    public int Priority { get; set; }
}

public sealed class EmailPriorityQueuePrioritizer : IMongoQueuePayloadPrioritizer
{
    public SortDefinition<MongoQueueItem<TPayload>> CreateSortDefinition<TPayload>()
        where TPayload : class
    {
        var sort = Builders<MongoQueueItem<TPayload>>.Sort;

        return typeof(TPayload) == typeof(PriorityEmailMessage)
            ? sort.Descending("Payload.Priority").Ascending(item => item.Id)
            : sort.Ascending(item => item.Id);
    }
}
```

Register it before or after `AddMongo`:

```csharp
using Chaos.Mongo.Queues;

services.AddSingleton<IMongoQueuePayloadPrioritizer, EmailPriorityQueuePrioritizer>();
```

The prioritizer is shared by registered queues, so provide a deterministic fallback for payload types that do not need custom ordering.

## Recovery and retention

- Every granted lock acquisition counts as a delivery attempt — including a lease-expiry reclaim after a crash, a
  DI resolution failure, or a payload that can never deserialize — not only a thrown handler exception. The
  `AttemptCount` on `MongoQueueItem` reflects this: an item that succeeds on its very first acquisition has
  `AttemptCount == 1`.
- The acquisition itself atomically decides, from the item's own pre-update attempt count, whether to grant a new
  attempt or terminalize the item directly: a new attempt is granted, and counted, whenever the existing attempt
  count is still `<= MaxRetries`. A process crash leaves a lock that becomes recoverable after the configured
  lease expires; that reclaim is granted and counted the same way, as long as the existing count still allows it.
  Once the existing attempt count already exceeds `MaxRetries`, the next acquisition — whether a fresh attempt or
  a lease-expiry reclaim — transitions the item directly to terminal instead, without granting it a lock or
  incrementing the count further. For example, with `WithMaxRetries(1)`: the first acquisition grants attempt 1
  (existing count `0 <= 1`); if the handler fails and the failure is resolved — a thrown exception, a DI
  resolution failure, or a poison-payload read failure — a second acquisition grants attempt 2 (existing count
  `1 <= 1`). If that second attempt's failure is also resolved, the item terminalizes immediately at that point
  (`AttemptCount == 2`); no further acquisition is needed or possible. Only an attempt that is abandoned instead
  of resolved — the process crashes or the handler hangs past the lease — leaves the item locked until the lease
  expires; the next reclaim then finds the existing count `2 > 1` and transitions the item straight to terminal.
- Successful items are retained for one hour by default and removed by a TTL index on `ClosedUtc`.
- Terminal items remain queryable for inspection and dead-letter workflows.
- `chaos.mongo.queue.lock.recovered` reflects only a lock actually abandoned by a crash, not an ordinary, already
  retried and already-logged failure: a resolved failure records that it deferred the item's retry, so the next
  reacquisition recognizes it and skips the recovery metric and warning for it.
- Change-stream notifications require a replica set or sharded cluster. On a standalone server, the subscription
  logs one clear message identifying that cause and relies on polling alone for the lifetime of that subscription,
  bounded by `WithLockLeaseTime(...)`/the lease recovery interval; it does not get real-time notification of newly
  published items. Any other change-stream failure retries with increasing backoff instead of failing the queue.

`IMongoQueue<TPayload>.IsRunning`, `StartSubscriptionAsync` and `StopSubscriptionAsync` control the queue's
subscription, which manages the change-stream monitor and processing loop. The subscription itself is internal to
the library; application code does not create or replace it. The token of `StopSubscriptionAsync` is a shutdown
timeout: when it expires while a handler that ignores cancellation is still running, and the stop has acquired the
queue's internal semaphore, the call returns without error (a token that is already cancelled, or that expires while
the stop still waits behind another stop or start, makes that wait throw as before), the subscription keeps shutting
down in the background, and a later `StartSubscriptionAsync` waits for that shutdown
(bounded by its own token) before it creates a new subscription.

## Diagnostics

Queues emit structured logs for lock recovery, retry and terminal transitions, cleanup mode, and successful processing.

Runtime metrics use `MongoQueueMetrics.MeterName`. Instrument and tag names are available through `MongoQueueMetrics.Instruments` and `MongoQueueMetrics.Tags`, whose instruments are:

| Instrument | Type | Measures |
|---|---|---|
| `chaos.mongo.queue.lock.recovered` | Counter | Expired or malformed queue item locks recovered. |
| `chaos.mongo.queue.lock.recovery_age` | Histogram (s) | Age of recovered queue item locks. |
| `chaos.mongo.queue.processing.duration` | Histogram (s) | Queue item processing duration. |
| `chaos.mongo.queue.processing.failed` | Counter | Failed queue item processing attempts. |
| `chaos.mongo.queue.processing.queue_age` | Histogram (s) | Time from queue item creation to lock acquisition. |
| `chaos.mongo.queue.processing.succeeded` | Counter | Successfully completed queue items. |
| `chaos.mongo.queue.published` | Counter | Queue items published. |

## Recommendations

- Make handlers idempotent to tolerate at-least-once processing.
- Select a lease that covers normal handler execution while permitting timely recovery.
- Use retry limits to prevent poison messages from cycling indefinitely.
- Monitor queue age, failures, terminal items, and lock recovery.
- Use separate queues when workloads require distinct capacity or priority policies.
