// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo;

/// <summary>
/// Extensions for <see cref="IMongoLock"/>.
/// </summary>
public static class MongoLockExtensions
{
    /// <summary>
    /// Ensures that the lock is valid.
    /// </summary>
    /// <param name="mongoLock">The lock to validate.</param>
    /// <returns>The same lock instance if it is valid.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="mongoLock"/> is null.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the lock is not valid.</exception>
    public static IMongoLock EnsureValid(this IMongoLock mongoLock)
    {
        ArgumentNullException.ThrowIfNull(mongoLock);

        if (!mongoLock.IsValid)
        {
            throw new InvalidOperationException($"MongoDB lock {mongoLock.Id} has expired or been released.");
        }

        return mongoLock;
    }

    /// <summary>
    /// Extends the lock lease or throws when the lock is no longer held.
    /// </summary>
    /// <param name="mongoLock">The lock to extend.</param>
    /// <param name="leaseTime">Optional lease duration. Defaults to the duration used to acquire the lock.</param>
    /// <param name="cancellationToken">Optional cancellation token.</param>
    /// <returns>The same lock instance after its lease has been extended.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="mongoLock"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="leaseTime"/> is shorter than one millisecond.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the lock lease cannot be extended.</exception>
    /// <exception cref="OperationCanceledException">Thrown when <paramref name="cancellationToken"/> is cancelled.</exception>
    public static async Task<IMongoLock> ExtendAsync(this IMongoLock mongoLock,
                                                     TimeSpan? leaseTime = null,
                                                     CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mongoLock);

        if (!await mongoLock.TryExtendAsync(leaseTime, cancellationToken))
            throw new InvalidOperationException($"MongoDB lock {mongoLock.Id} could not be extended because it is no longer held.");

        return mongoLock;
    }
}
