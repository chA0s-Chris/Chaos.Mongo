# Queue Lock Lease Recovery

## When to use

Use this pattern when queue work items can remain locked after a handler crash and you want passive recovery without a separate scavenger job.

## Pattern

1. Put a per-queue lease duration on the queue definition and default it in the fluent builder.
2. Treat an item as available when it is open and either unlocked, has no lock timestamp, or its lock timestamp is older than `now - lease`.
3. Reacquire the lock with a single `FindOneAndUpdateAsync` call so recovery stays atomic.
4. When the queue goes idle, wake the processing loop on a bounded timer (`min(lockLeaseTime, 1s)`) so expired locks are retried even if no new inserts arrive.
5. Only close/unlock the item if the persisted `LockedUtc` still matches the timestamp written by this consumer when it acquired the lock.
6. Index `(IsClosed, IsLocked, LockedUtc)` to support both normal polling and expired-lock lookups.
7. Test both behaviors:
   - handler fails once, then item is retried after lease expiry
   - slow first consumer cannot clear a replacement consumer's renewed lock
   - subscription creates the compound recovery index

## Chaos.Mongo file paths

- `src/Chaos.Mongo/Queues/MongoQueueDefinition.cs`
- `src/Chaos.Mongo/Queues/MongoQueueBuilder.cs`
- `src/Chaos.Mongo/Queues/MongoQueueSubscription.cs`
- `tests/Chaos.Mongo.Tests/Integration/Queues/MongoQueueLockExpiryIntegrationTests.cs`
