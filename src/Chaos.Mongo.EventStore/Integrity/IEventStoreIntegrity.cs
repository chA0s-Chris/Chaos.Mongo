// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

/// <summary>
/// Verifies the tamper evidence of the streams of one protected aggregate type: their hash chains, their anchoring,
/// their derived data and the anchor coverage of all of them. Registered for every aggregate type with integrity
/// protection.
/// </summary>
/// <typeparam name="TAggregate">The aggregate type.</typeparam>
public interface IEventStoreIntegrity<TAggregate> where TAggregate : class, IAggregate, new()
{
    /// <summary>
    /// Checks that every event of the aggregate type is covered by a completed anchor and that every anchored stream
    /// still exists up to its anchored version, after verifying the chain of anchors. An audit operation whose cost
    /// grows with the number of events and leaves; findings are streamed.
    /// </summary>
    /// <param name="trustedHead">
    /// An anchor kept outside the database, for example by the completion hook, that the chain of anchors must contain
    /// unchanged; without it, deleting the newest anchors together with the streams they cover cannot be detected.
    /// </param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The findings: a broken chain of anchors first, then the findings of each stream.</returns>
    /// <exception cref="InvalidOperationException">Thrown when neither anchoring nor verification is configured.</exception>
    IAsyncEnumerable<CompletenessFinding> CheckCompletenessAsync(TrustedAnchorHead? trustedHead = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the progress of the retroactive sealing sweep, or <c>null</c> before its first pass.
    /// </summary>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The status.</returns>
    Task<IntegritySealingStatus?> GetSealingStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Verifies a stream's chain and, when it is intact, replays the stream from its start and compares the read model
    /// and every checkpoint with the replayed state, and every checkpoint's recorded chain hash with the stream.
    /// </summary>
    /// <param name="aggregateId">The aggregate identifier.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The result.</returns>
    Task<DerivedDataVerificationResult> VerifyDerivedDataAsync(Guid aggregateId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Verifies that an event is anchored: the chain from the event to the first completed anchor that covers it, the
    /// anchor's leaf and inclusion proof, its digest and every stored timestamp token, including their revocation
    /// status recomputed from the stored CRLs.
    /// </summary>
    /// <param name="aggregateId">The aggregate identifier.</param>
    /// <param name="version">The event version.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The result.</returns>
    Task<EventAnchorVerificationResult> VerifyEventAnchorAsync(Guid aggregateId, Int64 version, CancellationToken cancellationToken = default);

    /// <summary>
    /// Recomputes a stream's hash chain from the stored bytes and reports the first broken version, the integrity data
    /// of every verified version and, for an intact chain, its comparison with the stream's leaves in completed
    /// anchors. The per-version data is returned as a list, so memory grows with the stream's length.
    /// </summary>
    /// <param name="aggregateId">The aggregate identifier.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The result.</returns>
    Task<StreamVerificationResult> VerifyStreamAsync(Guid aggregateId, CancellationToken cancellationToken = default);
}
