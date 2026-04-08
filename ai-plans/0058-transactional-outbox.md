# 0058 — Transactional Outbox

## Rationale

Add a new project `Chaos.Mongo.Outbox` that provides a generic transactional outbox pattern backed by MongoDB. The outbox guarantees that messages destined for external systems (message brokers, APIs, etc.) are persisted atomically within the same MongoDB transaction as the originating write operation. A background processor polls for pending messages and delegates publishing to a user-provided publisher implementation. This decouples reliable message delivery from the business operation, preventing message loss without requiring distributed transactions.

The outbox provides **at-least-once delivery** semantics. If the processor crashes after successfully publishing a message but before marking it as processed, the message will be published again on recovery. Consumers of outbox messages must be idempotent.

MongoDB transaction support is a hard requirement for this feature. The outbox is intended for replica set / sharded deployments where multi-document transactions are available. It must not silently degrade to non-transactional behavior on deployments that do not support transactions.

## Acceptance Criteria

- [ ] New project `Chaos.Mongo.Outbox` exists in `src/Chaos.Mongo.Outbox`, references `Chaos.Mongo`, and is included in the solution
- [ ] New test project `Chaos.Mongo.Outbox.Tests` exists in `tests/Chaos.Mongo.Outbox.Tests`
- [ ] `IOutbox` interface with generic `AddMessageAsync<TPayload>` method for writing typed outbox messages within a transaction
- [ ] `IOutboxPublisher` interface that users implement to deliver messages to their broker of choice
- [ ] `IOutboxProcessor` interface with `StartAsync`/`StopAsync` for manual processor lifecycle control
- [ ] `OutboxMessage` document type with `BsonDocument` payload, `DeserializePayload<TPayload>()` convenience method, metadata (type, created timestamp, correlation ID), processing state, and lock fields
- [ ] Payload types registered via builder API with discriminator names used as the `Type` field value
- [ ] Messages are inserted within the caller's MongoDB transaction, guaranteeing atomicity with the business operation
- [ ] The outbox fails fast with a clear exception if used without an active MongoDB transaction or on a deployment that does not support transactions
- [ ] Background processor picks up pending messages, invokes the user-provided `IOutboxPublisher`, and marks them as processed
- [ ] Failed messages are retried using a persisted retry schedule with configurable maximum retry count; permanently failed messages are marked with `State = Failed` and `FailedUtc` timestamp
- [ ] Optional TTL-based cleanup of processed and failed messages via configurable retention period; users can disable to retain messages indefinitely
- [ ] At-least-once delivery semantics: messages are guaranteed to be published at least once; consumers must be idempotent
- [ ] Ordering behavior is documented accurately: a single processor attempts eligible messages in ascending `_id` order, but strict global insertion ordering is not guaranteed
- [ ] Stale lock recovery: messages locked by a crashed processor are reclaimed after a configurable timeout
- [ ] Lock ownership is tracked explicitly so a stale processor cannot overwrite the state after another processor has reclaimed a message
- [ ] Retry backoff is durable across process restarts: retry timing is persisted in MongoDB rather than kept only in memory
- [ ] `IMongoConfigurator` implementation creates required indexes at startup
- [ ] User-facing configuration API via builder pattern on `MongoBuilder` (consistent with existing `WithQueue`, `WithEventStore` patterns)
- [ ] Structured logging (`ILogger`) throughout the processor: lifecycle events, message processing, failures, stale lock recovery, batch metrics
- [ ] Automated tests written

## Technical Details

### Project Structure

`Chaos.Mongo.Outbox` lives in `src/Chaos.Mongo.Outbox` and depends on `Chaos.Mongo`. Tests go in `tests/Chaos.Mongo.Outbox.Tests`. Add both to the solution file `Chaos.Mongo.slnx`.

### Core Types

**`OutboxMessage`** — The base (non-generic) class representing the outbox document as stored in MongoDB. The collection stores one document type. The payload is stored as a `BsonDocument` field, keeping the outbox agnostic of payload types at the storage level.

- `ObjectId Id` — MongoDB `_id`, used to sort eligible messages in approximate insertion order within a single processor. `ObjectId` is not a strict global sequence across concurrent writers, so it must not be treated as a hard ordering guarantee.
- `String Type` — A string identifying the message type (e.g., `"OrderCreated"`). Set automatically from the discriminator registered via `WithMessage<TPayload>`. Used by the publisher for routing.
- `BsonDocument Payload` — The payload as a raw BSON document. `IOutbox.AddMessageAsync<TPayload>` serializes the typed payload to `BsonDocument` before inserting. The publisher can access this directly for relay scenarios (e.g., `Payload.ToJson()`).
- `DateTime CreatedUtc` — Timestamp when the message was written to the outbox.
- `String? CorrelationId` — Optional correlation identifier for tracing across systems.
- `OutboxMessageState State` — Processing state: `Pending`, `Processed`, `Failed`.
- `DateTime? ProcessedUtc` — Timestamp when the message was successfully published.
- `DateTime? FailedUtc` — Timestamp when the message was permanently marked as failed (after exhausting retries). Used for TTL cleanup of failed messages.
- `Int32 RetryCount` — Number of failed processing attempts so far.
- `DateTime? NextAttemptUtc` — When the message becomes eligible for another processing attempt after a failure. `null` means the message is eligible immediately.
- `String? Error` — Last error message from a failed processing attempt.
- `Boolean IsLocked` — Whether the message is currently being processed.
- `DateTime? LockedUtc` — Timestamp when the lock was acquired. Used for stale lock detection.
- `String? LockId` — Opaque claim token identifying the processor's current lock ownership. A new value is generated every time a message is claimed or reclaimed. Success / failure updates must match on `LockId` so an older processor instance cannot overwrite a newer owner's result.

**`OutboxMessage.DeserializePayload<TPayload>()`** — A convenience method on `OutboxMessage` that deserializes the `BsonDocument Payload` to a typed `TPayload` using `BsonSerializer.Deserialize<TPayload>(Payload)`. This gives publishers typed access to the payload without a subclass hierarchy. Since documents round-trip through MongoDB as `OutboxMessage`, a generic subclass would not survive deserialization — this method is honest about the serialization boundary. Example usage in a publisher: `var order = message.DeserializePayload<OrderCreatedMessage>();`

**`OutboxMessageState`** — Enum with values `Pending`, `Processed`, `Failed`.

### Interface: `IOutbox`

```csharp
Task AddMessageAsync<TPayload>(
    IClientSessionHandle session,
    TPayload payload,
    String? correlationId = null,
    CancellationToken ct = default)
    where TPayload : class, new()
```

- Generic method on a non-generic interface. The `Type` discriminator is resolved automatically from the registered payload type configuration.
- Accepts the caller's `IClientSessionHandle` so the insert participates in the caller's transaction.
- Requires an active MongoDB transaction. If the session is null, not in a transaction, or the deployment does not support transactions, the implementation throws a clear `InvalidOperationException` rather than inserting non-transactionally.
- The payload is serialized to BSON by the MongoDB driver — no manual serialization required from the caller.
- `TPayload` must be a registered payload type (registered via the builder API). Passing an unregistered type throws at runtime.
- Registered as **singleton** in DI. The implementation is stateless — it only needs the outbox options (collection name, type mappings) and the `IMongoHelper` to serialize and insert.

### Interface: `IOutboxPublisher`

```csharp
Task PublishAsync(OutboxMessage message, CancellationToken ct = default)
```

- Non-generic interface, non-generic method. Receives the concrete `OutboxMessage`.
- Implemented by the user to deliver the message to their external system (RabbitMQ, Kafka, Azure Service Bus, HTTP, etc.).
- Throwing an exception signals a failed attempt; the processor will increment `RetryCount` and retry later.
- The publisher receives the full `OutboxMessage` including `Type`, `CorrelationId`, and `Payload` (as `BsonDocument`). The publisher can:
  - Access the raw BSON payload via `message.Payload` and convert to JSON (`message.Payload.ToJson()`) for relay scenarios.
  - Deserialize to a typed payload: `var order = message.DeserializePayload<OrderCreatedMessage>();`
  - Use `Type` for routing to different broker topics/queues.
- Registered as **transient** by default in DI. The lifetime can be overridden via `WithPublisher<T>(ServiceLifetime)`. The background processor creates a **DI scope per batch**, resolves `IOutboxPublisher` within that scope, and disposes the scope after the batch completes. This allows the publisher to safely depend on scoped services (e.g., `IHttpClientFactory`, scoped loggers) while avoiding per-message scope overhead.

### Background Processor

**`IOutboxProcessor`** — Interface with `StartAsync(CancellationToken)` and `StopAsync(CancellationToken)` methods. Registered as **singleton**. When auto-start is enabled, the outbox registers its own `OutboxHostedService` as an `IHostedLifecycleService`. This hosted service is intentionally separate from `MongoHostedService` because outbox startup is package-local and optional. `OutboxHostedService` is responsible for running outbox configurators first and only then starting the configured processor(s). When auto-start is off, the user can inject `IOutboxProcessor` and start/stop it manually — or simply leave it stopped (messages accumulate in the outbox until a processor picks them up, which could be a different service instance).

**`OutboxProcessor`** — The concrete implementation. Polls the outbox collection for pending messages and publishes them.

**`OutboxHostedService`** — An `IHostedLifecycleService` used only when `WithAutoStartProcessor()` is enabled. During `StartingAsync`, it creates a scope, resolves an outbox-specific configurator runner, and ensures indexes exist before any processor starts. During `StartedAsync`, it starts the registered outbox processor(s). During shutdown, it stops those processor(s). This keeps outbox lifecycle management self-contained and avoids coupling optional outbox behavior to the base package's `MongoHostedService`.

**Processing loop:**

1. Query for messages where `State == Pending`, `NextAttemptUtc` is null or less than / equal to the current time, and (`IsLocked == false` or `LockedUtc <= now - lockTimeout`), ordered by `_id` ascending, limited by configurable batch size (default: 100). This provides a stable processing order for eligible messages within a single processor instance, but it is not a strict global insertion-order guarantee. The processor should use this single query shape consistently so index design and query performance stay predictable.
2. Create a DI scope for the batch. Resolve `IOutboxPublisher` within that scope.
3. For each message in the batch: atomically claim it via `FindOneAndUpdate`, setting `IsLocked = true`, `LockedUtc = TimeProvider.GetUtcNow()`, and a newly generated `LockId`. The claim filter must only match messages that are currently unlocked or stale-locked.
4. Invoke `IOutboxPublisher.PublishAsync`.
5. On success: update the message to `State = Processed`, set `ProcessedUtc = TimeProvider.GetUtcNow()`, and clear lock fields, but only if the update filter still matches both the message `Id` and the current `LockId`.
6. On failure: increment `RetryCount`, store the error message, compute and persist `NextAttemptUtc` using the configured retry schedule, and clear lock fields, but only if the update filter still matches both the message `Id` and the current `LockId`. If `RetryCount` reaches the configured maximum (default: 5), set `State = Failed`, set `FailedUtc = TimeProvider.GetUtcNow()`, and clear `NextAttemptUtc`.
7. If a success / failure update affects zero documents, the processor has lost ownership of the message and must not attempt any further state changes for that processing attempt. This condition should be logged at warning level.
8. Dispose the DI scope after all messages in the batch are processed.
9. If the batch was full, immediately poll again without waiting. Otherwise, wait for the configured polling interval (default: 5 seconds) before the next poll.

**Timestamps:** All timestamps use the injected `TimeProvider` (defaulting to `TimeProvider.System`, consistent with existing `MongoQueueSubscription`) rather than `DateTime.UtcNow`. This makes the processor fully testable — tests can fake time to verify stale lock recovery, retry timing, and TTL behavior without waiting real seconds.

**Error handling:** Transient MongoDB errors (network blips, replica set elections) in the polling loop are caught, logged, and retried after a brief back-off delay. This prevents a single `MongoConnectionException` from killing the processor. Individual message publish failures are handled per-message (retry/fail logic above) and do not abort the rest of the batch.

**Retry scheduling:** Retry timing is persisted in the outbox document so it survives process restarts and coordinates correctly across multiple processor instances. The default retry policy is exponential backoff with a configurable initial delay and maximum delay cap. This is part of the outbox's durable processing semantics, not an in-memory retry wrapper around `IOutboxPublisher.PublishAsync`. Users may still choose to use Polly or similar libraries inside their publisher implementation for broker-specific transient handling, but that is optional and separate from the outbox's own retry scheduling.

**Ordering:** A single processor instance attempts eligible messages in ascending `_id` order. This is a best-effort ordering strategy, not a strict global insertion-order guarantee: concurrent writers can produce interleaved `ObjectId` values, retry backoff can temporarily defer older failed messages while newer eligible messages continue, and multiple processor instances further weaken ordering.

**Stale lock recovery:** A lock is considered stale when `LockedUtc` is older than a configurable lock timeout (default: 5 minutes). The polling query includes stale-locked messages alongside unlocked pending messages, so they are automatically reclaimed in the normal processing loop. This handles the case where a processor crashes after locking a message but before completing or releasing it.

**Lock ownership:** `IsLocked` and `LockedUtc` alone are not sufficient to protect correctness during stale lock recovery. Every claim writes a fresh `LockId`, and every completion update must match on that `LockId`. This ensures that if processor A locks a message, processor B later reclaims it after the lock becomes stale, and A eventually resumes, A cannot overwrite B's final state. This does not remove the at-least-once duplicate publish possibility if A already published before losing ownership, but it does prevent stored state corruption.

**Graceful shutdown:** When `StopAsync` is called (e.g., during container termination / SIGTERM):
- The processor stops accepting new batches (cancellation token is triggered).
- The currently in-flight message is allowed to finish processing (publish + state update). The processor does not abandon a message mid-publish.
- If the processor loses lock ownership before finalizing the in-flight message, it must skip the final state update and log the ownership loss.
- Any remaining unclaimed messages from the current batch query are not processed — they remain `Pending` and unlocked for the next processor startup or another instance to pick up.
- The DI scope for the current batch is disposed.

**Startup sequencing:** Outbox index creation must happen before any processor begins polling. When auto-start is enabled, `OutboxHostedService.StartingAsync` runs outbox configurators first, then `OutboxHostedService.StartedAsync` starts the processor(s). This sequencing is owned by the outbox package itself and must not depend on `MongoOptions.RunConfiguratorsOnStartup` or the base `MongoHostedService`.

### Indexes

The processor has one primary polling query shape:

```text
State == Pending
AND NextAttemptUtc <= now (or NextAttemptUtc is null)
AND (IsLocked == false OR LockedUtc <= staleLockThreshold)
ORDER BY _id ASC
LIMIT batchSize
```

Index design should be optimized specifically for this query, not for multiple alternate polling strategies.

An `OutboxConfigurator` implementing `IMongoConfigurator` creates the following indexes on the outbox collection:

- **Primary polling index** on pending messages, for example `{ NextAttemptUtc, LockedUtc, _id }` with a partial filter `State == Pending` — this is the main index used by the processor's polling query. It keeps the index limited to pending messages while supporting retry eligibility, stale-lock reclaim, and `_id` ordering among eligible messages.
- **TTL index** on `ProcessedUtc` (optional) — automatically removes processed messages after a configurable retention period. Created only if the user has configured a retention period via `WithRetentionPeriod()`. Users who need to keep processed messages indefinitely (for auditing, observability, or compliance) can omit the retention period configuration to skip TTL index creation entirely.
- **TTL index** on `FailedUtc` (optional) — automatically removes permanently failed messages after the same configurable retention period. Created alongside the `ProcessedUtc` TTL index when a retention period is configured. This prevents unbounded accumulation of failed messages.

Implementation note: the exact index key order may be adjusted based on MongoDB query planner behavior during implementation, but the plan assumes one concrete polling index for the single primary query above. Any deviation should be justified by measured query behavior rather than added speculatively.

### Configuration / DI Registration

Extend `MongoBuilder` with a `WithOutbox` extension method (in `MongoBuilderExtensions`):

```csharp
services.AddMongo(connectionString)
    .WithOutbox(outbox => outbox
        .WithPublisher<RabbitMqOutboxPublisher>()       // required; the user's publisher implementation (default: transient; overload accepts ServiceLifetime)
        .WithMessage<OrderCreatedMessage>("OrderCreated")   // register payload type with discriminator
        .WithMessage<OrderShippedMessage>("OrderShipped")   // discriminator optional; defaults to class name
        .WithCollectionName("Outbox")                   // optional; default is "Outbox"
        .WithMaxRetries(5)                              // optional; default: 5
        .WithRetryBackoff(TimeSpan.FromSeconds(5),
                          maxDelay: TimeSpan.FromMinutes(5)) // optional; default: exponential backoff with cap
        .WithRetentionPeriod(TimeSpan.FromDays(7))      // optional; omit to disable TTL cleanup entirely
        .WithBatchSize(100)                             // optional; default: 100
        .WithPollingInterval(TimeSpan.FromSeconds(5))   // optional; default: 5 seconds
        .WithLockTimeout(TimeSpan.FromMinutes(5))       // optional; default: 5 minutes; stale lock recovery threshold
        .WithAutoStartProcessor());                     // optional; default: off
```

**Message type registration:** Payload types are registered via `WithMessage<TPayload>(string? discriminator)`, following the same pattern as `WithEvent<TEvent>` in EventStore. The discriminator defaults to the class name if not specified. The discriminator string is used as the `Type` field value when `AddMessageAsync<TPayload>` is called. `WithMessage<TPayload>` has the same `where TPayload : class, new()` constraint as `IOutbox.AddMessageAsync<TPayload>`. At startup, `BsonClassMap` registrations are created for all payload types so the MongoDB driver can serialize them to `BsonDocument`.

**`OutboxBuilder`** provides fluent configuration and validates that exactly one publisher is registered.

**`OutboxOptions`** stores the resolved configuration as a singleton.

This registers:

- `IOutbox` as **singleton** (backed by `MongoOutbox`) — stateless; depends only on options and `IMongoHelper`
- `IOutboxPublisher` (the user's implementation) as **transient** by default (configurable via `WithPublisher<T>(ServiceLifetime)`) — resolved within a DI scope per batch by the processor
- `IOutboxProcessor` as **singleton** (backed by `OutboxProcessor`) — always registered, can be started manually by the user
- `OutboxOptions` as **singleton**
- `OutboxConfigurator` as `IMongoConfigurator` for index creation
- outbox-specific configurator runner service used by `OutboxHostedService` to run outbox initialization before processor startup
- `OutboxHostedService` as `IHostedLifecycleService` — **only if** `WithAutoStartProcessor()` is called; runs outbox configurators during startup and then starts / stops the processor(s)

### Usage Example

**Standalone (without EventStore):**

```csharp
await helper.ExecuteInTransaction(async (helper, session, ct) =>
{
    var orders = helper.GetCollection<Order>();
    await orders.InsertOneAsync(session, newOrder, cancellationToken: ct);

    await outbox.AddMessageAsync(session,
        new OrderCreatedMessage { OrderId = newOrder.Id },
        correlationId: correlationId, ct: ct);
}, cancellationToken);
```

This API is intentionally transaction-only. Callers using `TryStartTransactionAsync()` must check for `null` and choose a fallback strategy themselves; the outbox does not offer a best-effort non-transactional mode because that would break its core guarantee.

**With EventStore (via onBeforeCommit):**

```csharp
await eventStore.AppendEventsAsync(
    [new OrderCreatedEvent { ... }],
    onBeforeCommit: async (session, aggregate, helper, ct) =>
    {
        await outbox.AddMessageAsync(session,
            new OrderCreatedMessage { OrderId = aggregate.Id },
            ct: ct);
    });
```

### What Not to Build (For Now)

#### EventStore Integration Package (Optional — Future Enhancement)

**Not included in this plan.** A dedicated `Chaos.Mongo.EventStore.Outbox` package (or outbox extensions within `Chaos.Mongo.Outbox` that reference `Chaos.Mongo.EventStore`) could offer a streamlined builder API like `.WithOutbox()` directly on the EventStore builder, automatically writing outbox messages for appended events without manual `onBeforeCommit` wiring.

**Recommendation:** Build the standalone outbox first. Evaluate the integration package once both packages are stable and real usage patterns emerge.

#### Change Stream Signaling (Optional — Future Enhancement)

**Not included in this plan.** The initial implementation uses polling only. A future enhancement could add MongoDB change stream signaling (matching `MongoQueue`'s dual-strategy approach) to reduce latency between message insertion and publishing.

**Recommendation:** Polling is sufficient for typical outbox relay scenarios. Add change stream signaling if latency requirements demand it.
