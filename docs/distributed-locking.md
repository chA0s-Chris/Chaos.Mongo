# Distributed Locking

Chaos.Mongo stores leased locks in MongoDB so multiple application instances can coordinate work. A lock is released when disposed and can be reclaimed after its lease expires.

## Acquire with retry

`AcquireLockAsync` retries until the lock is acquired or the operation is cancelled:

```csharp
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
- A lease allows another instance to recover work if the current holder stops responding.
- `IMongoLock.IsValid` indicates whether the lock is still within its lease.
- Lock documents are stored in the collection configured by `MongoOptions.LockCollectionName`.
- Lease durations shorter than one millisecond are rejected with `ArgumentOutOfRangeException`, because MongoDB stores `DateTime` at millisecond precision. This applies to acquisition, extension, and `MongoOptions.MigrationLockLeaseTime`.

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
await mongoLock.ExtendAsync(
    leaseTime: TimeSpan.FromMinutes(10),
    cancellationToken: cancellationToken);
```

An expired, disposed, or stolen lock cannot be renewed. A refused extension makes that lock instance permanently invalid. MongoDB and cancellation failures propagate without marking the lock as lost, allowing the caller to distinguish an inconclusive operation from a definitive refusal.

Retrying after such a failure is worthwhile but not guaranteed to succeed: a renewal that reached the server before the response was lost leaves the lock instance tracking a stale expiry, and the retry is then refused even though no other holder took over. Treat a refusal following a failed renewal as a signal to stop the protected work and acquire the lock again, not as proof that another instance owns it.

Extension reduces the chance that long-running work outlives its lease, but it cannot eliminate the overlap window entirely. A holder can stall after its last renewal while another instance acquires the expired lock, then resume. Expiry is evaluated using each instance's client clock, so clock skew can widen this window. Check `IsValid` before exclusivity-sensitive changes and stop the protected work after a refused extension.

The library does not renew locks automatically. Callers remain responsible for choosing renewal intervals, handling failures, and abandoning work after losing the lock.

## Recommendations

- Use descriptive names that identify the protected operation or resource.
- Set leases long enough for expected work but short enough for useful recovery.
- Extend long-running leases before they expire, using an application-specific renewal policy.
- Always use `await using` so normal and exceptional exits release the lock.
- Pass cancellation tokens to retrying acquisition calls.
- Decide explicitly how callers should behave when immediate acquisition fails.

See [Configuration](configuration.md) for lock collection and holder settings.
