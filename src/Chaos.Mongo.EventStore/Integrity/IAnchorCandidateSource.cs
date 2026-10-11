// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

using MongoDB.Driver;

/// <summary>
/// Supplies the sealed stream heads that changed since the last acknowledged batch, across all protected
/// aggregate types.
/// </summary>
/// <remarks>
/// Delivery is at-least-once. At most one batch is outstanding at a time: reading again before acknowledging
/// returns the unacknowledged candidates again, and acknowledging a batch covers everything read up to it.
/// Acknowledged candidates are never returned again, also not after a restart.
/// Only one consumer may read at a time across all application instances; the anchoring run ensures this with
/// its distributed lock. An acknowledgement fails when another consumer changed the persisted position since the
/// batch was read.
/// </remarks>
internal interface IAnchorCandidateSource : IDisposable
{
    /// <summary>
    /// Acknowledges a batch inside the caller's transaction, so it can be persisted atomically with an anchor.
    /// </summary>
    /// <param name="session">The session of the caller's transaction.</param>
    /// <param name="batch">The batch returned by the last read.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>A task that completes when the acknowledgement is written to the transaction.</returns>
    Task AcknowledgeAsync(IClientSessionHandle session, AnchorCandidateBatch batch, CancellationToken cancellationToken);

    /// <summary>
    /// Reads the candidates that are currently available, without waiting for new events.
    /// </summary>
    /// <param name="maxCandidates">The maximum number of candidates in the batch.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The batch, possibly empty.</returns>
    Task<AnchorCandidateBatch> ReadBatchAsync(Int32 maxCandidates, CancellationToken cancellationToken);
}
