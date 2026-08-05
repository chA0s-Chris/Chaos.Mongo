// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo;

using MongoDB.Driver;

public interface IMongoHelper : IMongoConnection
{
    /// <summary>
    /// Get the collection for the specified document type <typeparamref name="TDocument"/>.
    /// </summary>
    /// <typeparam name="TDocument">Type of the document.</typeparam>
    /// <param name="settings">Optional <see cref="MongoCollectionSettings"/>.</param>
    /// <returns>Collection for the type <typeparamref name="TDocument"/>.</returns>
    IMongoCollection<TDocument> GetCollection<TDocument>(MongoCollectionSettings? settings = null);

    /// <summary>
    /// Attempt to acquire a distributed lock in MongoDB without retrying.
    /// </summary>
    /// <remarks>
    /// This method attempts to acquire the lock once and returns immediately.
    /// Returns <c>null</c> if the lock is currently held by another holder.
    /// The lock is automatically released when the returned <see cref="IMongoLock"/> is disposed.
    /// If the lock is not explicitly released, it will automatically expire after <paramref name="leaseTime"/>.
    /// </remarks>
    /// <param name="lockName">The name of the lock to acquire.</param>
    /// <param name="leaseTime">Optional lease duration. Defaults to <see cref="MongoDefaults.LockLeaseTime"/>.</param>
    /// <param name="cancellationToken">Optional cancellation token.</param>
    /// <returns>A lock instance if successful, or <c>null</c> if the lock could not be acquired.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="lockName"/> is null or whitespace.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="leaseTime"/> is shorter than one millisecond.</exception>
    Task<IMongoLock?> TryAcquireLockAsync(String lockName, TimeSpan? leaseTime = null, CancellationToken cancellationToken = default);
}
