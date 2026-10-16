// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

/// <summary>
/// Streams the leaves of an anchor, which may exceed what fits into memory or one document.
/// </summary>
public sealed class AnchorLeafReader
{
    private readonly AnchorStore _store;

    internal AnchorLeafReader(IMongoHelper mongoHelper, IntegrityAnchoringOptions options)
    {
        _store = new AnchorStore(mongoHelper, options);
    }

    /// <summary>
    /// Streams the leaves of an anchor in leaf order.
    /// </summary>
    /// <param name="anchorId">The anchor identifier.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The leaves with their inclusion proofs.</returns>
    public IAsyncEnumerable<AnchorLeaf> ReadLeavesAsync(Guid anchorId, CancellationToken cancellationToken = default)
        => _store.StreamLeavesAsync(anchorId, cancellationToken);
}
