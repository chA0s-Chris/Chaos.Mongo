// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo;

using Microsoft.Extensions.Options;
using MongoDB.Driver;

/// <summary>
/// Provides a helper abstraction for working with MongoDB, including collection access and distributed locking.
/// </summary>
public sealed class MongoHelper : IMongoHelper
{
    private readonly ICollectionTypeMap _collectionTypeMap;
    private readonly String _holderId;
    private readonly String _lockCollectionName;
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="MongoHelper"/> class.
    /// </summary>
    /// <param name="connection">The MongoDB connection instance.</param>
    /// <param name="collectionTypeMap">The collection type map for resolving collection names.</param>
    /// <param name="timeProvider">The time provider for getting current time.</param>
    /// <param name="options">Optional MongoDB configuration options.</param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="connection"/> or
    /// <paramref name="collectionTypeMap"/> is null.
    /// </exception>
    public MongoHelper(IMongoConnection connection,
                       ICollectionTypeMap collectionTypeMap,
                       TimeProvider timeProvider,
                       IOptions<MongoOptions>? options)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(collectionTypeMap);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _collectionTypeMap = collectionTypeMap;
        _timeProvider = timeProvider;

        Client = connection.Client;
        Database = connection.Database;

        _holderId = options?.Value.HolderId ?? Guid.NewGuid().ToString();
        _lockCollectionName = options?.Value.LockCollectionName ?? MongoDefaults.LockCollectionName;
    }

    /// <inheritdoc/>
    public IMongoClient Client { get; }

    /// <inheritdoc/>
    public IMongoDatabase Database { get; }

    /// <summary>
    /// Extends a MongoDB distributed lock if it is still held and unexpired.
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="TryAcquireLockAsync"/>, this method does not validate <paramref name="leaseTime"/>:
    /// <see cref="MongoLock.TryExtendAsync"/> validates it before invoking this operation.
    /// </remarks>
    /// <param name="lockName">The name of the lock to extend.</param>
    /// <param name="holder">The holder ID that currently owns the lock.</param>
    /// <param name="expectedLeaseUntilUtc">The lease expiry owned by the lock instance.</param>
    /// <param name="leaseTime">The new lease duration.</param>
    /// <param name="cancellationToken">Optional cancellation token.</param>
    /// <returns>The stored lease expiry when extension succeeds; otherwise, <see langword="null"/>.</returns>
    internal async Task<DateTime?> ExtendLockAsync(String lockName,
                                                   String holder,
                                                   DateTime expectedLeaseUntilUtc,
                                                   TimeSpan leaseTime,
                                                   CancellationToken cancellationToken = default)
    {
        var lockCollection = Database.GetCollection<MongoLockDocument>(_lockCollectionName);
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var leaseUntilUtc = now.Add(leaseTime);

        var filter = Builders<MongoLockDocument>.Filter.Eq(x => x.Id, lockName) &
                     Builders<MongoLockDocument>.Filter.Eq(x => x.Holder, holder) &
                     Builders<MongoLockDocument>.Filter.Eq(x => x.LeaseUntilUtc, expectedLeaseUntilUtc) &
                     Builders<MongoLockDocument>.Filter.Gt(x => x.LeaseUntilUtc, now);

        var update = Builders<MongoLockDocument>.Update.Set(x => x.LeaseUntilUtc, leaseUntilUtc);
        var options = new FindOneAndUpdateOptions<MongoLockDocument>
        {
            ReturnDocument = ReturnDocument.After
        };

        var lockDocument = await lockCollection.FindOneAndUpdateAsync(filter, update, options, cancellationToken);
        return lockDocument?.LeaseUntilUtc;
    }

    /// <summary>
    /// Releases a MongoDB distributed lock.
    /// </summary>
    /// <param name="lockName">The name of the lock to release.</param>
    /// <param name="holder">The holder ID that currently owns the lock.</param>
    /// <param name="leaseUntilUtc">The lease expiry owned by the lock instance.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    internal async Task ReleaseLockAsync(String lockName, String holder, DateTime leaseUntilUtc)
    {
        var lockCollection = Database.GetCollection<MongoLockDocument>(_lockCollectionName);

        var filter = Builders<MongoLockDocument>.Filter.Eq(x => x.Id, lockName) &
                     Builders<MongoLockDocument>.Filter.Eq(x => x.Holder, holder) &
                     Builders<MongoLockDocument>.Filter.Eq(x => x.LeaseUntilUtc, leaseUntilUtc);

        await lockCollection.DeleteOneAsync(filter);
    }

    private static void ValidateLeaseTime(TimeSpan leaseTime)
    {
        if (leaseTime < TimeSpan.FromMilliseconds(1))
            throw new ArgumentOutOfRangeException(nameof(leaseTime), leaseTime, "Lease time must be at least one millisecond.");
    }

    /// <inheritdoc/>
    public IMongoCollection<T> GetCollection<T>(MongoCollectionSettings? settings = null)
    {
        var collectionName = _collectionTypeMap.GetCollectionName<T>();
        return Database.GetCollection<T>(collectionName, settings);
    }

    /// <inheritdoc/>
    public async Task<IMongoLock?> TryAcquireLockAsync(String lockName, TimeSpan? leaseTime = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lockName);
        leaseTime ??= MongoDefaults.LockLeaseTime;
        ValidateLeaseTime(leaseTime.Value);

        var lockCollection = Database.GetCollection<MongoLockDocument>(_lockCollectionName);
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var leaseUntil = now.Add(leaseTime.Value);

        // Try to atomically take or extend the lock if expired
        var filter = Builders<MongoLockDocument>.Filter.Eq(x => x.Id, lockName) &
                     Builders<MongoLockDocument>.Filter.Lte(x => x.LeaseUntilUtc, now);

        var update = Builders<MongoLockDocument>.Update
                                                .SetOnInsert(x => x.Id, lockName)
                                                .Set(x => x.Holder, _holderId)
                                                .Set(x => x.LeaseUntilUtc, leaseUntil);

        var options = new FindOneAndUpdateOptions<MongoLockDocument>
        {
            IsUpsert = true,
            ReturnDocument = ReturnDocument.After
        };

        try
        {
            var lockDocument = await lockCollection.FindOneAndUpdateAsync(filter, update, options, cancellationToken);

            // Verify we successfully acquired the lock
            if (lockDocument?.Holder == _holderId)
            {
                return new MongoLock(lockName,
                                     lockDocument.LeaseUntilUtc,
                                     leaseTime.Value,
                                     _timeProvider,
                                     validUntilUtc => ReleaseLockAsync(lockName, _holderId, validUntilUtc),
                                     (validUntilUtc, extensionLeaseTime, token) =>
                                         ExtendLockAsync(lockName, _holderId, validUntilUtc, extensionLeaseTime, token));
            }

            return null;
        }
        catch (MongoException ex)
            when (ex is MongoCommandException { Code: 11000 } or
                        MongoWriteException { WriteError.Category: ServerErrorCategory.DuplicateKey })
        {
            // Duplicate key error - another process created the lock during our upsert attempt
            return null;
        }
    }
}
