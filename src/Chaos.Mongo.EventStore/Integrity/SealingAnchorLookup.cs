// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

/// <summary>
/// Looks up the anchor that seals an event.
/// </summary>
/// <remarks>
/// The sealing anchor is looked up instead of stored on the event: a stored reference would modify sealed documents
/// after sealing and need a second rule for appended events. The same rule yields the anchor that proves an appended
/// event existed no later than its timestamp. #160 exposes it through the verification API.
/// </remarks>
internal sealed class SealingAnchorLookup
{
    private readonly AnchorStore _store;

    public SealingAnchorLookup(IMongoHelper mongoHelper, IntegrityAnchoringOptions options)
    {
        _store = new AnchorStore(mongoHelper, options);
    }

    /// <summary>
    /// Finds the first completed anchor whose leaf for the event's stream has a version greater than or equal to the
    /// event's version.
    /// </summary>
    /// <param name="aggregateType">The aggregate type name.</param>
    /// <param name="aggregateId">The aggregate identifier.</param>
    /// <param name="version">The event version.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The sealing anchor, or <c>null</c> while the event is not anchored by a completed anchor.</returns>
    public async Task<SealingAnchor?> FindAsync(String aggregateType, Guid aggregateId, Int64 version, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(aggregateType);

        // Leaves of discarded attempts and of an anchor awaiting its timestamps may come first; their anchors are skipped.
        await foreach (var leaf in _store.FindStreamLeavesAsync(aggregateType, aggregateId, version, cancellationToken))
        {
            if (await _store.FindAsync(leaf.AnchorId, cancellationToken) is { Status: AnchorStatus.Completed } anchor)
            {
                return new SealingAnchor(anchor, leaf);
            }
        }

        return null;
    }
}
