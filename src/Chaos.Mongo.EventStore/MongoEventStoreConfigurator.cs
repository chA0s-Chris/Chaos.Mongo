// Copyright (c) 2025-2026 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore;

using Chaos.Mongo.Configuration;
using MongoDB.Driver;

/// <summary>
/// Configurator that creates the unique compound index on <c>(AggregateId, Version)</c>
/// in the events collection for a specific aggregate type.
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
            new()
            {
                Unique = true,
                Name = IndexNames.AggregateIdWithVersionUnique
            });

        await eventsCollection.Indexes.CreateOneOrUpdateAsync(indexModel, cancellationToken: cancellationToken);
    }
}
