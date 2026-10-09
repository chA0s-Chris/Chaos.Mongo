// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore;

using Chaos.Mongo.Configuration;
using MongoDB.Driver;

/// <summary>
/// Configurator that creates the unique compound index on <c>(AggregateId, Version)</c>
/// in the events collection for a specific aggregate type, and the nearest-checkpoint lookup indexes
/// in the checkpoint collection when checkpoints are enabled.
/// </summary>
/// <typeparam name="TAggregate">The aggregate type.</typeparam>
public sealed class MongoEventStoreConfigurator<TAggregate> : IMongoConfigurator
    where TAggregate : class, IAggregate, new()
{
    private readonly MongoEventStoreOptions<TAggregate> _options;

    public MongoEventStoreConfigurator(MongoEventStoreOptions<TAggregate> options)
    {
        _options = options;
    }

    public async Task ConfigureAsync(IMongoHelper helper, CancellationToken cancellationToken = default)
    {
        var eventsCollection = helper.Database.GetCollection<Event<TAggregate>>(_options.EventsCollectionName);

        var indexModel = new CreateIndexModel<Event<TAggregate>>(
            Builders<Event<TAggregate>>.IndexKeys
                                       .Ascending(e => e.AggregateId)
                                       .Ascending(e => e.Version),
            new CreateIndexOptions
            {
                Unique = true,
                Name = IndexNames.AggregateIdWithVersionUnique
            });

        await eventsCollection.Indexes.CreateOneOrUpdateAsync(indexModel, cancellationToken: cancellationToken);

        if (!_options.CheckpointsEnabled)
        {
            return;
        }

        var checkpointCollection = helper.Database.GetCollection<CheckpointDocument<TAggregate>>(_options.CheckpointCollectionName);
        var checkpointKeys = Builders<CheckpointDocument<TAggregate>>.IndexKeys;

        await checkpointCollection.Indexes.CreateOneOrUpdateAsync(
            new CreateIndexModel<CheckpointDocument<TAggregate>>(
                checkpointKeys.Ascending(c => c.Id.AggregateId).Descending(c => c.Id.Version),
                new CreateIndexOptions
                {
                    Name = IndexNames.CheckpointAggregateIdWithVersion
                }),
            cancellationToken: cancellationToken);

        await checkpointCollection.Indexes.CreateOneOrUpdateAsync(
            new CreateIndexModel<CheckpointDocument<TAggregate>>(
                checkpointKeys.Ascending(c => c.Id.AggregateId).Descending(c => c.Revision),
                new CreateIndexOptions
                {
                    Name = IndexNames.CheckpointAggregateIdWithRevision
                }),
            cancellationToken: cancellationToken);
    }
}
