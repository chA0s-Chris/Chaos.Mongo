// Copyright (c) 2025-2026 Christian Flessa. All rights reserved.
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
    IMongoCollection<TAggregate> Collection { get; }

    /// <summary>
    /// Returns the current read model for the aggregate, or <c>null</c> if not found.
    /// </summary>
    /// <param name="aggregateId">The aggregate identifier.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The current aggregate state, or <c>null</c>.</returns>
    Task<TAggregate?> GetAsync(Guid aggregateId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reconstructs the aggregate state at a specific version.
    /// </summary>
    /// <remarks>
    /// Uses checkpoints if available (loads nearest checkpoint ≤ target version, then replays remaining events).
    /// Returns <c>null</c> if the aggregate doesn't exist or has no events up to that version.
    /// </remarks>
    /// <param name="aggregateId">The aggregate identifier.</param>
    /// <param name="version">The target version to reconstruct.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The aggregate state at the specified version, or <c>null</c>.</returns>
    Task<TAggregate?> GetAtVersionAsync(Guid aggregateId, Int64 version, CancellationToken cancellationToken = default);
}
