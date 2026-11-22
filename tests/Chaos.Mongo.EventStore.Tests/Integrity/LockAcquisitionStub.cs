// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Tests.Integrity;

using MongoDB.Driver;

/// <summary>
/// A hand-crafted <see cref="IMongoHelper"/> whose only working member is the lock acquisition that opens every
/// sealing pass and anchoring run, and that receives the token of the loop. Tests control how that call reacts to
/// cancellation; every other member throws, so a test fails loudly if the code under test reaches the database.
/// </summary>
internal sealed class LockAcquisitionStub : IMongoHelper
{
    private readonly Func<CancellationToken, Task<IMongoLock?>> _tryAcquireLock;
    private Int32 _acquisitions;

    public LockAcquisitionStub(Func<CancellationToken, Task<IMongoLock?>> tryAcquireLock)
    {
        ArgumentNullException.ThrowIfNull(tryAcquireLock);
        _tryAcquireLock = tryAcquireLock;
    }

    /// <summary>
    /// Gets how often a lock was requested, which is how often a loop started a pass or run.
    /// </summary>
    public Int32 Acquisitions => Volatile.Read(ref _acquisitions);

    public IMongoClient Client => throw new NotSupportedException();

    public IMongoDatabase Database => throw new NotSupportedException();

    public IMongoCollection<TDocument> GetCollection<TDocument>(MongoCollectionSettings? settings = null) => throw new NotSupportedException();

    public Task<IMongoLock?> TryAcquireLockAsync(String lockName, TimeSpan? leaseTime = null, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _acquisitions);
        return _tryAcquireLock(cancellationToken);
    }
}
