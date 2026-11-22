# Distributed Locking

Chaos.Mongo stores leased locks in MongoDB so multiple application instances can coordinate work. A lock is released when disposed and can be reclaimed after its lease expires.

The lock is a lease without fencing: it coordinates work but does not guarantee mutual exclusion. A holder that stalls past its lease can still write after another instance has acquired the lock. See [Guarding writes](#guarding-writes).

## Acquire with retry

`AcquireLockAsync` retries until the lock is acquired or the operation is cancelled:

```csharp
using Chaos.Mongo;

public sealed class JobProcessor(IMongoHelper mongo)
{
    public async Task ProcessJobAsync(CancellationToken cancellationToken = default)
    {
        await using var mongoLock = await mongo.AcquireLockAsync(
            lockName: "process-daily-reports",
            leaseTime: TimeSpan.FromMinutes(10),
            retryDelay: TimeSpan.FromSeconds(5),
            cancellationToken: cancellationToken);

        await ProcessReportsAsync(cancellationToken);
    }
}
```

The lock is released when `mongoLock` is disposed, including when the protected operation throws.

## Try once

`TryAcquireLockAsync` returns `null` immediately when another holder owns a valid lease:

```csharp
public async Task TryProcessJobAsync(CancellationToken cancellationToken = default)
{
    await using var mongoLock = await mongo.TryAcquireLockAsync(
        lockName: "process-daily-reports",
        leaseTime: TimeSpan.FromMinutes(10),
        cancellationToken: cancellationToken);

    if (mongoLock is null)
    {
        logger.LogInformation("The job is already running on another instance");
        return;
    }

    await ProcessReportsAsync(cancellationToken);
}
```

## Lock behavior

- `await using` releases a held lock automatically.
- A lease allows another instance to recover work if the current holder stops responding. The previous holder is not stopped, so two instances can briefly act as holders. See [Guarding writes](#guarding-writes).
- `IMongoLock.Id` is the lock name supplied to `AcquireLockAsync`/`TryAcquireLockAsync`; it identifies the protected resource and stays the same across acquisitions of that name, so it is not a unique per-acquisition token.
- `IMongoLock.IsValid` indicates whether the lock is still within its lease according to the last known expiry and the local clock, and is `false` after disposal or once loss was detected. It does not contact the database.
- `IMongoLock.ValidUntilUtc` is the UTC time the current lease expires.
- Lock documents are stored in the collection configured by `MongoOptions.LockCollectionName`.
- Lease durations shorter than one millisecond are rejected with `ArgumentOutOfRangeException`, because MongoDB stores `DateTime` at millisecond precision. This applies to acquisition, extension, and `MongoOptions.MigrationLockLeaseTime`.

Use `EnsureValid()` to fail fast with a named `InvalidOperationException` instead of continuing with a lock that has already expired or been released:

```csharp
using Chaos.Mongo;

mongoLock.EnsureValid();
```

It returns the same lock instance when `IsValid` is `true`, so calls can be chained.

## Extend a held lock

Use `TryExtendAsync` to renew a lock before its lease expires. Omitting the duration reuses the duration supplied during acquisition:

```csharp
if (!await mongoLock.TryExtendAsync(cancellationToken: cancellationToken))
{
    logger.LogWarning("The distributed lock was lost");
    return;
}
```

`ExtendAsync` is the throwing companion and returns the same lock after a successful renewal:

```csharp
using Chaos.Mongo;

await mongoLock.ExtendAsync(
    leaseTime: TimeSpan.FromMinutes(10),
    cancellationToken: cancellationToken);
```

An expired, disposed, or stolen lock cannot be renewed. A refused extension makes that lock instance permanently invalid. MongoDB and cancellation failures propagate without marking the lock as lost, allowing the caller to distinguish an inconclusive operation from a definitive refusal.

Retrying after such a failure can succeed even when the earlier attempt's result was never observed: extension and release are keyed on a per-acquisition token, not on the lock's last-known expiry, so a renewal that reached the server before the response was lost does not cause the retry to be refused. Treat a refusal following a failed renewal as a signal to stop the protected work and acquire the lock again, not as proof that another instance owns it.

Extension reduces the chance that long-running work outlives its lease, but it cannot eliminate the overlap window entirely. A holder can stall after its last renewal while another instance acquires the expired lock, then resume. Expiry is evaluated using each instance's client clock, so clock skew can widen this window. Stop the protected work after a refused extension, and see [Guarding writes](#guarding-writes) for writes that must stay correct when the overlap happens. The migration runner closes the window for transactional migrations by writing to the lock document inside the migration transaction, so MongoDB decides between the contenders atomically with the commit (see [Migrations](migrations.md)); runs without a transaction and other protected work keep the window, and effects outside the transaction's session cannot be guarded by the lock.

Disposing a lock waits for a renewal that is already in flight before releasing. A slow renewal therefore delays disposal. Cancel the token passed to the renewal when shutdown must not wait for it: release is keyed on the lock's per-acquisition token rather than its last-known expiry, so even a renewal that reaches the server after cancellation is observed does not prevent the lock from being released.

The library does not renew locks automatically. Callers remain responsible for choosing renewal intervals, handling failures, and abandoning work after losing the lock.

## Guarding writes

A lock held through `IMongoLock` is a lease without fencing. A holder that stalls past its lease (a garbage collection pause, a suspended process, a slow call, clock skew) can lose the lock without noticing and still write after another instance has acquired it. The holder cannot detect this before its write. `IMongoLock.Id` is the lock name, not a per-acquisition token, so it cannot serve as a fencing token either.

`IsValid`, `EnsureValid()`, and a refused `TryExtendAsync` are ways to stop work early. Use them to abandon work that no longer has a lease, but do not rely on them as a guarantee: a pause between the check and the write leaves the same race.

Make writes that must not happen twice correct even when two holders overlap:

1. **Writes that are safe to repeat.** Design the write so that running it twice gives the same result and a stale copy running after a newer one cannot undo it. An insert keyed by an immutable operation ID, where a duplicate key means the same operation already completed, qualifies. An upsert or `$set` on a natural key does not by itself: a holder with older input can resume and overwrite the newer holder's value. When older values must be rejected, condition the update on a version or timestamp stored with the data.
2. **Writes conditioned on a token stored with the data.** Generate a token in the application each time work on a record is claimed, store it on the record, and condition the later write on it. Both steps are needed: every claim or reclaim replaces the stored token, so an old holder's token no longer matches, and the later update is a filter-based, non-upsert update on the record's id and the claim's token that treats an update matching nothing as lost ownership (for example `MatchedCount == 0`). Without the first step, a stale holder whose token is still on the record would match after another instance took over. The [transactional outbox](transactional-outbox.md#locking-and-stale-lock-recovery) uses this predicate with its `LockId`: claiming sets it, and the completion and failure updates filter on the message ID and `LockId`. The guard protects only that conditioned update: a late publish in the outbox can still happen twice, so it does not make side effects exactly-once.
3. **An ownership check inside a transaction.** This guard is specific to migrations and was introduced by [#317](https://github.com/chA0s-Chris/Chaos.Mongo/issues/317). It lives in the migration runner and is not a public API; `IMongoLock` is unchanged. For a transactional migration, the runner writes to the lock document inside the migration transaction, filtered on the acquisition's token and an unexpired lease, before the migration runs, so MongoDB orders any takeover against the transaction. It protects only effects that use the migration's session: runs without a transaction, custom `IMongoLock` implementations and writes outside the session keep the overlap window. Reading the lock, or checking `IsValid`, inside a transaction does not close the race and is not such a guard; only a write to the lock document that is part of the transaction does. See [Database Migrations](migrations.md#behavior).

## Recommendations

- Use descriptive names that identify the protected operation or resource.
- Set leases long enough for expected work but short enough for useful recovery.
- Extend long-running leases before they expire, using an application-specific renewal policy.
- Always use `await using` so normal and exceptional exits release the lock.
- Pass cancellation tokens to retrying acquisition calls.
- Decide explicitly how callers should behave when immediate acquisition fails.
- Do not treat the lock as mutual exclusion; make writes safe to repeat or conditioned on the data, as described in [Guarding writes](#guarding-writes).

See [Configuration](configuration.md) for lock collection and holder settings.
