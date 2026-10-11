// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

/// <summary>
/// The <see cref="ProtectedStreamType"/> of one aggregate type.
/// </summary>
/// <typeparam name="TAggregate">The aggregate type.</typeparam>
internal sealed class ProtectedStreamType<TAggregate> : ProtectedStreamType where TAggregate : class, IAggregate, new()
{
    private readonly MongoEventStoreOptions<TAggregate> _options;

    public ProtectedStreamType(MongoEventStoreOptions<TAggregate> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    /// <inheritdoc/>
    public override String AggregateIdElement => EventDocumentFields<TAggregate>.AggregateId;

    /// <inheritdoc/>
    public override String AggregateTypeName => typeof(TAggregate).Name;

    /// <inheritdoc/>
    public override String EventsCollectionName => _options.EventsCollectionName;

    /// <inheritdoc/>
    public override String VersionElement => EventDocumentFields<TAggregate>.Version;

    /// <inheritdoc/>
    public override SweepQuery NextStream(Guid? cursor) => SealingSweepQueries<TAggregate>.NextStream(cursor);

    /// <inheritdoc/>
    public override SweepQuery StreamHead(Guid aggregateId) => SealingSweepQueries<TAggregate>.StreamHead(aggregateId);
}
