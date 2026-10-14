// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

using Chaos.Mongo.Configuration;

/// <summary>
/// Creates the indexes of the anchors and leaves collections.
/// </summary>
internal sealed class IntegrityAnchoringConfigurator : IMongoConfigurator
{
    private readonly IntegrityAnchoringOptions _options;

    public IntegrityAnchoringConfigurator(IntegrityAnchoringOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    /// <inheritdoc/>
    public Task ConfigureAsync(IMongoHelper helper, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(helper);
        return new AnchorStore(helper, _options).EnsureIndexesAsync(cancellationToken);
    }
}
