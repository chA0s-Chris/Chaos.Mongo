// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo;

using MongoDB.Driver;

/// <summary>
/// Default implementation of <see cref="IMongoLock"/> representing a distributed lock in MongoDB.
/// </summary>
internal class MongoLock : ITransactionFencedLock
{
    private readonly Func<TimeSpan, CancellationToken, Task<DateTime?>> _extendAction;
    private readonly Func<IClientSessionHandle, CancellationToken, Task<Boolean>>? _fenceAction;
    private readonly TimeSpan _leaseTime;

    // Serializes extension, the transactional ownership check and disposal. Deliberately never disposed: an extension arriving after disposal must
    // observe the disposed flag and return false, not throw ObjectDisposedException out of WaitAsync.
    private readonly SemaphoreSlim _operationSemaphore = new(1, 1);
    private readonly Func<Task> _releaseAction;
    private readonly Object _stateLock = new();
    private readonly TimeProvider _timeProvider;
    private Boolean _disposed;
    private Boolean _lost;
    private DateTime _validUntilUtc;

    /// <summary>
    /// Initializes a new instance of the <see cref="MongoLock"/> class.
    /// </summary>
    /// <param name="id">The unique identifier of the lock.</param>
    /// <param name="validUntilUtc">The UTC date and time when the lock will automatically expire.</param>
    /// <param name="leaseTime">The duration used to acquire the lock.</param>
    /// <param name="timeProvider">The time provider for getting current time.</param>
    /// <param name="releaseAction">The action to execute when the lock is released.</param>
    /// <param name="extendAction">The action to execute when the lock lease is extended.</param>
    /// <param name="fenceAction">
    /// The action that writes to the lock document inside a transaction and reports whether the lock is still held.
    /// Without it, <see cref="TryEnsureHeldAsync"/> performs no server operation.
    /// </param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="id"/> is null or whitespace.</exception>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="timeProvider"/>, <paramref name="releaseAction"/>,
    /// or <paramref name="extendAction"/> is null.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="leaseTime"/> is shorter than one millisecond.</exception>
    public MongoLock(String id,
                     DateTime validUntilUtc,
                     TimeSpan leaseTime,
                     TimeProvider timeProvider,
                     Func<Task> releaseAction,
                     Func<TimeSpan, CancellationToken, Task<DateTime?>> extendAction,
                     Func<IClientSessionHandle, CancellationToken, Task<Boolean>>? fenceAction = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(releaseAction);
        ArgumentNullException.ThrowIfNull(extendAction);
        ValidateLeaseTime(leaseTime, nameof(leaseTime));

        Id = id;
        _validUntilUtc = validUntilUtc;
        _leaseTime = leaseTime;
        _timeProvider = timeProvider;
        _releaseAction = releaseAction;
        _extendAction = extendAction;
        _fenceAction = fenceAction;
    }

    /// <inheritdoc/>
    public String Id { get; }

    /// <inheritdoc/>
    public Boolean IsValid
    {
        get
        {
            lock (_stateLock)
                return !_disposed && !_lost && _validUntilUtc > _timeProvider.GetUtcNow().UtcDateTime;
        }
    }

    /// <inheritdoc/>
    public DateTime ValidUntilUtc
    {
        get
        {
            lock (_stateLock)
                return _validUntilUtc;
        }
    }

    private static void ValidateLeaseTime(TimeSpan leaseTime, String parameterName)
    {
        if (leaseTime < TimeSpan.FromMilliseconds(1))
            throw new ArgumentOutOfRangeException(parameterName, leaseTime, "Lease time must be at least one millisecond.");
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        // Waits for an in-flight extension to finish before releasing. The wait is unbounded by design: disposal is
        // a cold path, and a bounded wait would race the extension it exists to exclude.
        await _operationSemaphore.WaitAsync();

        try
        {
            lock (_stateLock)
            {
                if (_disposed)
                    return;

                _disposed = true;
            }

            try
            {
                await _releaseAction();
            }
            catch
            {
                // Suppress exceptions during dispose to prevent unhandled exceptions.
                // Locks will eventually expire anyway.
            }
        }
        finally
        {
            _operationSemaphore.Release();
        }
    }

    /// <inheritdoc/>
    public async Task<Boolean> TryExtendAsync(TimeSpan? leaseTime = null, CancellationToken cancellationToken = default)
    {
        var requestedLeaseTime = leaseTime ?? _leaseTime;
        ValidateLeaseTime(requestedLeaseTime, nameof(leaseTime));

        await _operationSemaphore.WaitAsync(cancellationToken);

        try
        {
            lock (_stateLock)
            {
                if (_disposed || _lost)
                    return false;
            }

            var extendedUntilUtc = await _extendAction(requestedLeaseTime, cancellationToken);

            lock (_stateLock)
            {
                if (extendedUntilUtc is null)
                {
                    _lost = true;
                    return false;
                }

                _validUntilUtc = extendedUntilUtc.Value;
                return true;
            }
        }
        finally
        {
            _operationSemaphore.Release();
        }
    }

    /// <inheritdoc/>
    public async Task<Boolean> TryEnsureHeldAsync(IClientSessionHandle session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        await _operationSemaphore.WaitAsync(cancellationToken);

        try
        {
            lock (_stateLock)
            {
                if (_disposed || _lost)
                    return false;
            }

            if (_fenceAction is null)
                return true;

            Boolean held;
            try
            {
                held = await _fenceAction(session, cancellationToken);
            }
            catch (Exception ex) when (MongoHelper.IsTransactionWriteConflict(ex))
            {
                // A change of the lock document committed after the transaction's snapshot: the lock changed hands
                // or was renewed behind the transaction's back. Either way this holder cannot prove ownership.
                held = false;
            }

            if (!held)
            {
                lock (_stateLock)
                    _lost = true;
            }

            return held;
        }
        finally
        {
            _operationSemaphore.Release();
        }
    }
}
