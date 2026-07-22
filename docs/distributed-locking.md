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

The lease is not an automatic renewal mechanism. Choose a duration that covers the protected work, and do not continue making exclusivity-sensitive changes after the lock is no longer valid.

## Recommendations

- Use descriptive names that identify the protected operation or resource.
- Set leases long enough for expected work but short enough for useful recovery.
- Always use `await using` so normal and exceptional exits release the lock.
- Pass cancellation tokens to retrying acquisition calls.
- Decide explicitly how callers should behave when immediate acquisition fails.

See [Configuration](configuration.md) for lock collection and holder settings.
