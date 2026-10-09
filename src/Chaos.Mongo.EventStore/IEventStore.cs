// Copyright (c) 2025 Christian Flessa. All rights reserved.
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
    ///     All events must target the same aggregate (same <see cref="Event{TAggregate}.AggregateId"/>).
    ///     Their versions are either all left unset (<c>0</c>) or all set explicitly.
    ///     </para>
    ///     <para>
    ///     <b>Store-assigned versions:</b> when every version is <c>0</c>, the event store assigns sequential
    ///     versions after the stream's highest version and writes them back to the events. If another writer
    ///     commits to the stream concurrently, the append reloads the stream and the read model, re-checks
    ///     <paramref name="expectedRevision"/>, executes the events again against the fresh aggregate and retries,
    ///     up to <see cref="MongoEventStoreOptions{TAggregate}.MaxAppendRetries"/> additional times.
    ///     <see cref="Event{TAggregate}.Execute"/> and <paramref name="onBeforeCommit"/> may therefore run more than
    ///     once and must be deterministic. If the append fails, the versions are reset to <c>0</c>.
    ///     </para>
    ///     <para>
    ///     <b>Explicit versions:</b> the events must have sequential versions starting from the stream's highest
    ///     version + 1. The highest version is read from the events collection, so it includes observational
    ///     events. Explicit appends are never retried.
    ///     </para>
    ///     <para>
    ///     A first event whose version was already committed is treated as an optimistic-concurrency
    ///     conflict rather than invalid input, because another writer may have committed that version
    ///     after the caller read the stream. Resubmitting an event that is already stored is an
    ///     idempotent retry and reports <see cref="MongoDuplicateEventException"/> instead. A version
    ///     above the stream's highest version + 1 remains invalid caller input.
    ///     </para>
    ///     <para>
    ///     Events are first applied to the aggregate in memory to validate that the aggregate's
    ///     current state permits the operations. If validation succeeds, the events are persisted
    ///     within a transaction along with the updated read model and optional checkpoint.
    ///     </para>
    ///     <para>
    ///     Each event's <see cref="Event{TAggregate}.Revision"/> is set by the event store. A state-changing
    ///     event increments the aggregate's <see cref="IAggregate.Revision"/> and sets its
    ///     <see cref="IAggregate.Version"/> to the event's position. An <see cref="ObservationalEvent{TAggregate}"/>
    ///     records the revision it observed and changes neither; a batch of only observational events
    ///     writes neither the read model nor a checkpoint. An observational event requires a preceding
    ///     state-changing event in the stream or earlier in the same batch.
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
    ///     <para>
    ///     The returned aggregate is a commit-time snapshot: it reflects the state after all events
    ///     have been applied and the transaction has committed. Callers may read its properties freely,
    ///     but mutations to the returned object will not be persisted back to the database.
    ///     </para>
    /// </remarks>
    /// <param name="events">
    /// The events to append. Must all target the same aggregate, with either unset or sequential versions.
    /// </param>
    /// <param name="expectedRevision">
    /// The aggregate revision the caller prepared the events against, or <c>null</c> to skip the check. When
    /// supplied and different from the aggregate's current revision, the append fails before anything is executed
    /// or persisted. Observational events appended concurrently do not change the revision.
    /// </param>
    /// <param name="onBeforeCommit">
    /// An optional callback invoked within the transaction before commit. Receives the session handle,
    /// the committed aggregate, and <see cref="IMongoHelper"/> for performing additional transactional operations.
    /// </param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The aggregate with all events applied, as it was at commit time.</returns>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="events"/> is empty, contains events for different aggregates,
    /// mixes unset and explicit versions, starts at a version below one, leaves a gap above the stream's
    /// highest version, or continues with non-sequential versions.
    /// </exception>
    /// <exception cref="MongoEventValidationException">
    /// Thrown when an event cannot be applied because the aggregate's state does not permit it, and when
    /// an observational event has no preceding state-changing event.
    /// </exception>
    /// <exception cref="MongoConcurrencyException">
    /// Thrown when <paramref name="expectedRevision"/> does not match the aggregate's revision. For explicit
    /// versions, also thrown when the first event's version was already committed for this aggregate, when a
    /// state change was committed between reading the stream and reading the read model, and when another
    /// process inserts an event for the same aggregate version before this append commits. For store-assigned
    /// versions, also thrown when these conflicts persist after
    /// <see cref="MongoEventStoreOptions{TAggregate}.MaxAppendRetries"/> retries.
    /// </exception>
    /// <exception cref="MongoDuplicateEventException">
    /// Thrown when an event with the same ID already exists. Never retried.
    /// </exception>
    /// <exception cref="MongoEventStoreException">
    /// Thrown when the stream contains events but the aggregate's read model is missing.
    /// </exception>
    Task<TAggregate> AppendEventsAsync(
        IEnumerable<Event<TAggregate>> events,
        Int64? expectedRevision = null,
        Func<IClientSessionHandle, TAggregate, IMongoHelper, CancellationToken, Task>? onBeforeCommit = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the event stream for an aggregate, ordered by version.
    /// </summary>
    /// <remarks>
    /// The stream includes observational events. Events stored before revisions existed are returned with
    /// <see cref="Event{TAggregate}.Revision"/> equal to their version.
    /// </remarks>
    /// <param name="aggregateId">The aggregate identifier.</param>
    /// <param name="fromVersion">The minimum version (inclusive). Defaults to 0 (all events).</param>
    /// <param name="toVersion">The maximum version (inclusive). Defaults to null (no upper bound).</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>An async enumerable of events ordered by version.</returns>
    IAsyncEnumerable<Event<TAggregate>> GetEventStream(
        Guid aggregateId,
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
