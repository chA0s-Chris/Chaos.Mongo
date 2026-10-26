// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

/// <summary>
/// Erases personal data from the events of one protected aggregate type without breaking their hash chains or anchors.
/// Registered for every aggregate type with integrity protection. Erasure needs <c>update</c> on the events
/// collection; resolve it from a separate container with its own credentials, so the application keeps insert and find
/// only.
/// </summary>
/// <typeparam name="TAggregate">The aggregate type.</typeparam>
public interface IPersonalDataEraser<TAggregate> where TAggregate : class, IAggregate, new()
{
    /// <summary>
    /// Erases the selected personal data values of a stream in one transaction: sets them to <c>null</c> in place,
    /// removes their salts, appends a <see cref="PersonalDataErased{TAggregate}"/> event that records them, and rebuilds
    /// the read model and the affected checkpoints by replay. Values already erased are skipped; when nothing is left
    /// to erase, no event is appended.
    /// </summary>
    /// <remarks>
    /// Event <c>Execute</c> implementations must handle <c>null</c> in marked members; an <c>Execute</c> that throws
    /// aborts the erasure. The rebuilt read model and checkpoints hold only what replay produces, so read-model fields
    /// written outside <c>Execute</c> are reset.
    /// </remarks>
    /// <param name="aggregateId">The aggregate identifier.</param>
    /// <param name="request">The selection and the reference to record.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The erased paths per event version, empty when nothing was left to erase.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="aggregateId"/> is empty.</exception>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="request"/> is <c>null</c>.</exception>
    /// <exception cref="Errors.MongoEventStoreException">
    /// Thrown when the request cannot be fulfilled, for example because a
    /// requested version does not exist.
    /// </exception>
    /// <exception cref="Errors.MongoConcurrencyException">Thrown when the stream changed concurrently.</exception>
    Task<IReadOnlyList<PersonalDataErasureEntry>> ErasePersonalDataAsync(Guid aggregateId, ErasureRequest request, CancellationToken cancellationToken = default);
}
