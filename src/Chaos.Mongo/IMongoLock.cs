// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo;

/// <summary>
/// Represents a distributed lock in MongoDB.
/// </summary>
/// <remarks>
/// Dispose the lock to release it. If not disposed, the lock will automatically expire after the lease time.
/// Disposal waits for an in-flight <see cref="TryExtendAsync"/> call to complete, so the release uses the expiry
/// that extension stored.
/// </remarks>
public interface IMongoLock : IAsyncDisposable
{
    /// <summary>
    /// Gets the unique identifier of the lock.
    /// </summary>
    String Id { get; }

    /// <summary>
    /// Gets a value indicating whether the lock is still valid.
    /// </summary>
    /// <remarks>
    /// Reports <see langword="false"/> once the lock has been disposed, its lease has expired, or an extension was refused.
    /// </remarks>
    Boolean IsValid { get; }

    /// <summary>
    /// Gets the UTC date and time when the lock will automatically expire.
    /// </summary>
    DateTime ValidUntilUtc { get; }

    /// <summary>
    /// Attempts to extend the lease while this instance still owns the lock.
    /// </summary>
    /// <remarks>
    /// A <see langword="false"/> result is final for this instance: the lock counts as lost, <see cref="IsValid"/> reports
    /// <see langword="false"/> from then on, and further extension attempts are refused without contacting the database.
    /// Exceptions propagate instead, leaving the lock untouched.
    /// Disposing the lock waits for an extension already in flight, so cancel <paramref name="cancellationToken"/>
    /// when disposal must not block on a slow renewal.
    /// </remarks>
    /// <param name="leaseTime">Optional lease duration. Defaults to the duration used to acquire the lock.</param>
    /// <param name="cancellationToken">Optional cancellation token.</param>
    /// <returns><see langword="true"/> when the lease was extended; otherwise, <see langword="false"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="leaseTime"/> is shorter than one millisecond.</exception>
    /// <exception cref="OperationCanceledException">Thrown when <paramref name="cancellationToken"/> is cancelled.</exception>
    Task<Boolean> TryExtendAsync(TimeSpan? leaseTime = null, CancellationToken cancellationToken = default);
}
