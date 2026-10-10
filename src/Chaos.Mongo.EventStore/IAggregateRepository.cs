// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore;

using MongoDB.Driver;

/// <summary>
/// Provides read access to aggregate state.
/// </summary>
/// <typeparam name="TAggregate">The aggregate type.</typeparam>
/// <remarks>
/// The repository reads from the read-model and checkpoint collections but does not write to them.
/// Use <see cref="IEventStore{TAggregate}"/> for appending events.
/// </remarks>
public interface IAggregateRepository<TAggregate> where TAggregate : class, IAggregate, new()
{
    /// <summary>
    /// Gets the underlying read-model collection for running custom queries.
    /// </summary>
    /// <remarks>
    /// Queries through this collection bypass the repository: read models stored before revisions existed
    /// are returned with <see cref="IAggregate.Revision"/> <c>0</c> instead of their version.
    /// </remarks>
    IMongoCollection<TAggregate> Collection { get; }

    /// <summary>
    /// Returns the current read model for the aggregate, or <c>null</c> if not found.
    /// </summary>
    /// <param name="aggregateId">The aggregate identifier.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The current aggregate state, or <c>null</c>.</returns>
    Task<TAggregate?> GetAsync(Guid aggregateId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reconstructs the aggregate state at a specific revision.
    /// </summary>
    /// <remarks>
    /// Uses checkpoints if available (loads the nearest checkpoint whose revision is ≤ the target, then replays
    /// the remaining state-changing events). Observational events are excluded on the server and never replayed.
    /// Checkpoints stored before revisions existed lack the revision and are not used until they are backfilled.
    /// Returns <c>null</c> if the aggregate doesn't exist or has no events up to that revision.
    /// </remarks>
    /// <param name="aggregateId">The aggregate identifier.</param>
    /// <param name="revision">The target revision to reconstruct.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The aggregate state at the specified revision, or <c>null</c>.</returns>
    Task<TAggregate?> GetAtRevisionAsync(Guid aggregateId, Int64 revision, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reconstructs the aggregate state at a specific stream position.
    /// </summary>
    /// <remarks>
    /// Uses checkpoints if available (loads nearest checkpoint ≤ target version, then replays remaining events).
    /// Observational events are excluded on the server and never replayed, so the returned aggregate's
    /// <see cref="IAggregate.Version"/> and <see cref="IAggregate.Revision"/> are those of the last state-changing
    /// event at or below the target.
    /// Returns <c>null</c> if the aggregate doesn't exist or has no events up to that version.
    /// </remarks>
    /// <param name="aggregateId">The aggregate identifier.</param>
    /// <param name="version">The target version to reconstruct.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The aggregate state at the specified version, or <c>null</c>.</returns>
    Task<TAggregate?> GetAtVersionAsync(Guid aggregateId, Int64 version, CancellationToken cancellationToken = default);
}
