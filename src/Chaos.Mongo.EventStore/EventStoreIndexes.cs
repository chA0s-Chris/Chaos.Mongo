// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore;

using MongoDB.Driver;

/// <summary>
/// Builds the index models shared between <see cref="MongoEventStoreConfigurator{TAggregate}"/> and the
/// concurrency index check that appends perform, so both use the exact same index definition.
/// </summary>
internal static class EventStoreIndexes
{
    /// <summary>
    /// Builds the unique compound index on <c>(AggregateId, Version)</c> that protects optimistic concurrency.
    /// </summary>
    public static CreateIndexModel<Event<TAggregate>> CreateConcurrencyIndexModel<TAggregate>()
        where TAggregate : class, IAggregate, new()
        => new(
            Builders<Event<TAggregate>>.IndexKeys
                                       .Ascending(e => e.AggregateId)
                                       .Ascending(e => e.Version),
            new CreateIndexOptions
            {
                Unique = true,
                Name = IndexNames.AggregateIdWithVersionUnique
            });
}
