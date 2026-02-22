// Copyright (c) 2025-2026 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore;

using Chaos.Mongo.EventStore.Errors;
using MongoDB.Driver;

/// <summary>
/// Provides event sourcing operations for a specific aggregate type.
/// </summary>
/// <typeparam name="TAggregate">The aggregate type.</typeparam>
public interface IEventStore<TAggregate> where TAggregate : class, IAggregate, new()
{
    /// <summary>
    /// Appends events to the event store within a transaction.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///     Events are first applied to the aggregate in memory to validate that the aggregate's
    ///     current state permits the operations. If validation succeeds, the events are persisted
    ///     within a transaction along with the updated read model and optional checkpoint.
    ///     </para>
    ///     <para>
    ///     If an event's <see cref="Event{TAggregate}.Execute"/> method throws (e.g., because the
    ///     aggregate is in an invalid state), no events are persisted.
    ///     </para>
    ///     <para>
    ///     If <paramref name="onBeforeCommit"/> is provided, it is invoked within the transaction
    ///     after all event store operations complete but before the transaction commits. This allows
    ///     additional transactional operations (e.g., inserting into a transactional outbox).
    ///     </para>
    /// </remarks>
    /// <param name="events">The events to append.</param>
    /// <param name="onBeforeCommit">
    /// An optional callback invoked within the transaction before commit. Receives the session handle
    /// and <see cref="IMongoHelper"/> for performing additional transactional operations.
    /// </param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The version of the last inserted event.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="events"/> is empty.</exception>
    /// <exception cref="MongoEventValidationException">
    /// Thrown when an event cannot be applied because the aggregate's state does not permit it.
    /// </exception>
    /// <exception cref="MongoConcurrencyException">
    /// Thrown when another process inserted an event for the same aggregate version.
    /// </exception>
    /// <exception cref="MongoDuplicateEventException">
    /// Thrown when an event with the same ID already exists.
    /// </exception>
    Task<Int64> AppendEventsAsync(
        IEnumerable<Event<TAggregate>> events,
        Func<IClientSessionHandle, IMongoHelper, CancellationToken, Task>? onBeforeCommit = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the event stream for an aggregate, ordered by version.
    /// </summary>
    /// <param name="aggregateId">The aggregate identifier.</param>
    /// <param name="fromVersion">The minimum version (inclusive). Defaults to 0 (all events).</param>
    /// <param name="toVersion">The maximum version (inclusive). Defaults to null (no upper bound).</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>An async enumerable of events ordered by version.</returns>
    IAsyncEnumerable<Event<TAggregate>> GetEventStream(Guid aggregateId,
                                                       Int64 fromVersion = 0,
                                                       Int64? toVersion = null,
                                                       CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the expected next version for the specified aggregate.
    /// </summary>
    /// <remarks>
    /// Returns the highest existing version + 1, or 1 if no events exist for this aggregate.
    /// This returns the <em>expected</em> next version based on current state, not a reserved slot.
    /// Concurrent callers may receive the same value; only the first to insert will succeed
    /// (enforced by the unique compound index on <c>(AggregateId, Version)</c>).
    /// </remarks>
    /// <param name="aggregateId">The aggregate identifier.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The expected next version number.</returns>
    Task<Int64> GetExpectedNextVersionAsync(Guid aggregateId, CancellationToken cancellationToken = default);
}
