// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

/// <summary>
/// Streams the leaves of an anchor from the anchor store.
/// </summary>
internal sealed class AnchorLeafReader : IAnchorLeafReader
{
    private readonly AnchorStore _store;

    internal AnchorLeafReader(IMongoHelper mongoHelper, IntegrityAnchoringOptions options)
    {
        _store = new AnchorStore(mongoHelper, options);
    }

    /// <inheritdoc/>
    public IAsyncEnumerable<AnchorLeaf> ReadLeavesAsync(Guid anchorId, CancellationToken cancellationToken = default)
        => _store.StreamLeavesAsync(anchorId, cancellationToken);
}
