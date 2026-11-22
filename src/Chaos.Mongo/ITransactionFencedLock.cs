// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo;

using MongoDB.Driver;

/// <summary>
/// An <see cref="IMongoLock"/> that can check, on the server and inside a transaction, that its holder still owns it.
/// </summary>
/// <remarks>
/// The check is a write to the lock document that joins the transaction of <c>session</c>. MongoDB then orders every
/// competing change of the lock document against the transaction, so the check is atomic with the commit, which a
/// client-side validity check cannot be.
/// </remarks>
internal interface ITransactionFencedLock : IMongoLock
{
    /// <summary>
    /// Writes to the lock document inside the transaction of <paramref name="session"/>, filtered on this lock's
    /// acquisition and its unexpired lease.
    /// </summary>
    /// <remarks>
    /// The write is serialized with <see cref="IMongoLock.TryExtendAsync"/> and disposal. Call it as the first server
    /// operation of the transaction: the transaction then holds the lock document until it ends, and the lease filter is
    /// evaluated on current data. Once the lock is lost or disposed, it returns <see langword="false"/> without a
    /// server call.
    /// </remarks>
    /// <param name="session">The session with the started transaction the write joins.</param>
    /// <param name="cancellationToken">Optional cancellation token.</param>
    /// <returns>
    /// <see langword="true"/> when the lock is held; <see langword="false"/> when it was lost, which includes a write
    /// conflict with a concurrent change of the lock document.
    /// </returns>
    Task<Boolean> TryEnsureHeldAsync(IClientSessionHandle session, CancellationToken cancellationToken = default);
}
