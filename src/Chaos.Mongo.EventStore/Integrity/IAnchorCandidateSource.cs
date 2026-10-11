// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

using MongoDB.Driver;

/// <summary>
/// Supplies the sealed stream heads that changed since the last acknowledged batch, across all protected
/// aggregate types.
/// </summary>
/// <remarks>
/// Delivery is at-least-once. Consecutive reads continue from the last read position, and acknowledging the last
/// batch persists the position for everything read up to it, so one acknowledgement covers any number of reads.
/// Unacknowledged candidates are returned again after a restart or <see cref="DiscardUnacknowledgedReads"/>;
/// acknowledged candidates are never returned again, also not after a restart.
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
    /// <param name="batch">The batch returned by the last read; it covers every unacknowledged read before it.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>A task that completes when the acknowledgement is written to the transaction.</returns>
    /// <exception cref="ArgumentException">Thrown when the batch is not the one returned by the last read.</exception>
    Task AcknowledgeAsync(IClientSessionHandle session, AnchorCandidateBatch batch, CancellationToken cancellationToken);

    /// <summary>
    /// Discards the reads since the last acknowledgement, so the next read starts at the persisted position again.
    /// A consumer calls this before it starts reading, because an earlier attempt may have read without acknowledging.
    /// </summary>
    void DiscardUnacknowledgedReads();

    /// <summary>
    /// Reads the candidates that are currently available, without waiting for new events.
    /// </summary>
    /// <param name="maxCandidates">The maximum number of candidates in the batch.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The batch, possibly empty.</returns>
    Task<AnchorCandidateBatch> ReadBatchAsync(Int32 maxCandidates, CancellationToken cancellationToken);
}
